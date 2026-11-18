using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using YusufOkuducu.PcRenderGraphDoctor.Editor;

namespace YusufOkuducu.PcRenderGraphDoctor.Tests
{
    public sealed class RenderGraphDoctorTests
    {
        private abstract class ScriptableRenderPass
        {
            public virtual void Execute() { }
        }

        private sealed class LegacyCustomOutlinePass : ScriptableRenderPass
        {
            public override void Execute() { }
        }

        private sealed class NativeUnity6BloomPass : ScriptableRenderPass
        {
            public void RecordRenderGraph() { }
        }

        private sealed class DualPathSsaoPass : ScriptableRenderPass
        {
            public override void Execute() { }
            public void RecordRenderGraph() { }
        }

        [Test]
        public void ResidentDrawerAuditor_FlagsPropertyBlockNegativeScaleAndLppvDisqualifiers()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.transform.localScale = new Vector3(-1f, 1f, 1f);
                var mr = go.GetComponent<MeshRenderer>();
                mr.lightProbeUsage = LightProbeUsage.UseProxyVolume;

                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", Color.red);
                mr.SetPropertyBlock(mpb);

                RendererEligibilityRecord rec = GpuResidentDrawerAuditor.AnalyzeRenderer(mr, hasStaticBatchingFlag: true);

                Assert.IsFalse(rec.IsEligible);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.NegativeTransformScale) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed) != 0);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ShaderResidentDrawerAudit_VerifiesTarget45DotsInstancingAndCBuffer()
        {
            const string compatibleShader = @"
Shader ""Custom/BrgReadyLit""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            ENDHLSL
        }
    }
}";

            const string legacyShader = @"
Shader ""Custom/LegacySurface""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            float4 _BaseColor;
            ENDHLSL
        }
    }
}";

            ShaderResidentDrawerAudit good = GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer(compatibleShader, "Assets/BrgReadyLit.shader");
            ShaderResidentDrawerAudit bad = GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer(legacyShader, "Assets/LegacySurface.shader");

            Assert.IsTrue(good.isCompatibleWithResidentDrawer);
            Assert.IsTrue(good.hasDotsInstancedProps);

            Assert.IsFalse(bad.isCompatibleWithResidentDrawer);
            Assert.IsFalse(bad.hasTarget45OrHigher);
            Assert.IsFalse(bad.hasDotsInstancingPragma);
            Assert.IsFalse(bad.hasSrpBatcherCBuffer);
        }

        [Test]
        public void AssemblyAuditor_ClassifiesLegacyNativeAndDualPathRenderPassesCorrectly()
        {
            RenderPassMigrationFinding legacy = RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(LegacyCustomOutlinePass));
            RenderPassMigrationFinding native = RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(NativeUnity6BloomPass));
            RenderPassMigrationFinding dual = RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(DualPathSsaoPass));

            Assert.IsFalse(legacy.implementsRecordRenderGraph);
            Assert.IsTrue(legacy.implementsLegacyExecute);
            StringAssert.Contains("LEGACY COMPATIBILITY ONLY", legacy.migrationStatus);

            Assert.IsTrue(native.implementsRecordRenderGraph);
            Assert.IsFalse(native.implementsLegacyExecute);
            StringAssert.Contains("NATIVE RENDER GRAPH", native.migrationStatus);

            Assert.IsTrue(dual.implementsRecordRenderGraph);
            Assert.IsTrue(dual.implementsLegacyExecute);
            StringAssert.Contains("DUAL-PATH COMPATIBLE", dual.migrationStatus);
        }

        [Test]
        public void SourceAnalyzer_DetectsNonStaticRenderFuncClosureAndUnsafePassHazards()
        {
            const string badPassCSharp = @"
public class BadRenderGraphPass : ScriptableRenderPass
{
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var cmd = CommandBufferPool.Get(""Bad"");
        var temp = RenderTexture.GetTemporary(512, 512);
        using (var builder = renderGraph.AddUnsafePass<PassData>(""BadPass"", out var passData))
        {
            builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
            {
                ExecutePass(data, ctx);
            });
        }
    }
}";

            const string cleanPassCSharp = @"
public class CleanRenderGraphPass : ScriptableRenderPass
{
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""CleanPass"", out var passData))
        {
            builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawProcedural(Matrix4x4.identity, data.mat, 0, MeshTopology.Triangles, 3);
            });
        }
    }
}";

            RenderGraphSourceAuditResult badAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(badPassCSharp, "Assets/BadRenderGraphPass.cs");
            RenderGraphSourceAuditResult cleanAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(cleanPassCSharp, "Assets/CleanRenderGraphPass.cs");

            Assert.IsTrue(badAudit.usesRecordRenderGraph);
            Assert.IsTrue(badAudit.usesAddUnsafePass);
            Assert.IsTrue(badAudit.hasNonStaticRenderFuncClosureRisk);
            Assert.IsTrue(badAudit.usesLegacyCommandBufferPoolInGraph);
            Assert.IsTrue(badAudit.usesLegacyGetTemporaryRtInGraph);
            Assert.AreEqual(4, badAudit.findings.Count);

            Assert.IsTrue(cleanAudit.usesRecordRenderGraph);
            Assert.IsTrue(cleanAudit.usesAddRasterRenderPass);
            Assert.IsTrue(cleanAudit.usesStaticRenderFuncLambda);
            Assert.IsFalse(cleanAudit.hasNonStaticRenderFuncClosureRisk);
        }
    }
}
