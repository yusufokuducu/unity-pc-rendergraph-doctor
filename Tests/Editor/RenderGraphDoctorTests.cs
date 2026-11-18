using System.Collections.Generic;
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

                ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { mr },
                    isStaticBatchingEnabledPredicate: _ => true,
                    isShaderCompatiblePredicate: _ => false);

                Assert.AreEqual(1, report.totalMeshRenderers);
                Assert.AreEqual(1, report.disqualifiedRenderers);
                Assert.AreEqual(1, report.shaderIncompatibilityViolations);

                RendererEligibilityRecord rec = report.records[0];
                Assert.IsFalse(rec.IsEligible);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.NegativeTransformScale) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.ShaderMissingDotsInstancing) != 0);
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

        [Test]
        public void SourceAnalyzer_DoesNotFalselyFlagDualPathExecuteOrStringLiteralsAndHandlesMethodGroups()
        {
            const string dualPathAndStaticMethodGroupCSharp = @"
public class DualPathPass : ScriptableRenderPass
{
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var cmd = CommandBufferPool.Get(""Legacy2022Pass"");
        var temp = RenderTexture.GetTemporary(256, 256);
    }

    private static void ExecutePassStatic(PassData data, RasterGraphContext ctx)
    {
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        string note = ""Migrated away from CommandBufferPool.Get and RenderTexture.GetTemporary"";
        #if false
        var deadCmd = CommandBufferPool.Get(""Dead"");
        #endif
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""DualPass"", out var passData))
        {
            builder.SetRenderFunc<PassData>(ExecutePassStatic);
        }
    }
}";

            const string instanceMethodGroupCSharp = @"
public class InstanceMethodGroupPass : ScriptableRenderPass
{
    private void ExecuteInstance(PassData data, RasterGraphContext ctx)
    {
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""InstancePass"", out var passData))
        {
            builder.SetRenderFunc<PassData>(ExecuteInstance);
        }
    }
}";

            RenderGraphSourceAuditResult dualAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(
                dualPathAndStaticMethodGroupCSharp,
                "Assets/DualPathPass.cs");

            Assert.IsTrue(dualAudit.usesRecordRenderGraph);
            Assert.IsTrue(dualAudit.usesAddRasterRenderPass);
            Assert.IsTrue(dualAudit.usesStaticRenderFuncLambda);
            Assert.IsFalse(dualAudit.hasNonStaticRenderFuncClosureRisk);
            Assert.IsFalse(dualAudit.usesLegacyCommandBufferPoolInGraph);
            Assert.IsFalse(dualAudit.usesLegacyGetTemporaryRtInGraph);

            RenderGraphSourceAuditResult instanceGroupAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(
                instanceMethodGroupCSharp,
                "Assets/InstanceMethodGroupPass.cs");

            Assert.IsTrue(instanceGroupAudit.usesRecordRenderGraph);
            Assert.IsTrue(instanceGroupAudit.hasNonStaticRenderFuncClosureRisk);
        }

        [Test]
        public void McpBridge_AutoFixSceneRenderers_And_AutoFixRenderPassCSharpSource_ResolveViolations()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial.enableInstancing = false;
                mr.lightProbeUsage = LightProbeUsage.UseProxyVolume;
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", Color.red);
                mr.SetPropertyBlock(mpb);

                bool staticBatchFlag = true;

                ResidentDrawerAuditReport beforeScene = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { mr },
                    _ => staticBatchFlag);
                Assert.AreEqual(1, beforeScene.disqualifiedRenderers);

                SceneAutoFixReport sceneFix = RenderGraphDoctorMcpBridge.AutoFixSceneRenderers(
                    new[] { mr },
                    _ => staticBatchFlag,
                    _ => staticBatchFlag = false);

                Assert.AreEqual(1, sceneFix.renderersFixed);
                Assert.AreEqual(1, sceneFix.propertyBlocksCleared);
                Assert.AreEqual(1, sceneFix.materialsInstancingEnabled);
                Assert.AreEqual(1, sceneFix.lppvConvertedToBlendProbes);
                Assert.AreEqual(1, sceneFix.staticBatchingFlagsCleared);
                Assert.IsFalse(mr.HasPropertyBlock());
                Assert.IsTrue(mr.sharedMaterial.enableInstancing);
                Assert.AreEqual(LightProbeUsage.BlendProbes, mr.lightProbeUsage);
                Assert.IsFalse(staticBatchFlag);

                ResidentDrawerAuditReport afterScene = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { mr },
                    _ => staticBatchFlag);
                Assert.AreEqual(1, afterScene.eligibleRenderers);
                Assert.AreEqual(0, afterScene.disqualifiedRenderers);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            // Test C# Render Graph pass non-static SetRenderFunc lambda auto-fix
            const string nonStaticLambdaPass = @"
public class ClosurePass : ScriptableRenderPass
{
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""ClosurePass"", out var passData))
        {
            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
            });
        }
    }
}";

            RenderPassSourceAutoFixReport passFix = RenderGraphDoctorMcpBridge.AutoFixRenderPassCSharpSource(
                nonStaticLambdaPass,
                "Assets/ClosurePass.cs");

            Assert.IsTrue(passFix.modified);
            Assert.IsTrue(passFix.beforeAudit.hasNonStaticRenderFuncClosureRisk);
            Assert.IsFalse(passFix.afterAudit.hasNonStaticRenderFuncClosureRisk);
            Assert.IsTrue(passFix.afterAudit.usesStaticRenderFuncLambda);
            StringAssert.Contains("SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>", passFix.patchedSource);
        }

        [Test]
        public void McpBridge_HandlesJsonRpcInitializeToolsListToolsCallAndErrors()
        {
            string initRes = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");
            StringAssert.Contains(@"""protocolVersion"":""2024-11-05""", initRes);
            StringAssert.Contains(@"""name"":""unity-pc-rendergraph-doctor""", initRes);

            string listRes = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list""}");
            StringAssert.Contains("rendergraph_doctor.audit_scene_renderers", listRes);
            StringAssert.Contains("rendergraph_doctor.autofix_renderpass_csharp", listRes);

            string callBrg = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""rendergraph_doctor.audit_shader_brg"",""arguments"":{""source"":""Shader \""Custom/BrgOk\"" { SubShader { Pass { HLSLPROGRAM\n#pragma target 4.5\n#pragma multi_compile _ DOTS_INSTANCING_ON\nCBUFFER_START(UnityPerMaterial) float4 _C; CBUFFER_END ENDHLSL } } }""}}}");
            StringAssert.Contains(@"""isCompatibleWithResidentDrawer"":true", callBrg);
            StringAssert.Contains(@"""isError"":false", callBrg);

            string callFixPass = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""rendergraph_doctor.autofix_renderpass_csharp"",""arguments"":{""source"":""public class P : ScriptableRenderPass { public override void RecordRenderGraph(RenderGraph rg, ContextContainer fd) { using (var b = rg.AddRasterRenderPass<D>(\""P\"", out var d)) { b.SetRenderFunc((D data, RasterGraphContext ctx) => {}); } } }""}}}");
            StringAssert.Contains(@"""modified"":true", callFixPass);
            StringAssert.Contains(@"""hasClosureRiskAfter"":false", callFixPass);

            string errUnknown = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":99,""method"":""nonexistent""}");
            StringAssert.Contains("-32601", errUnknown);
        }

        [Test]
        public void UiToolkitWindow_BuildsHeatmapSwitchesTabsAndFiltersSceneAndPassRecords()
        {
            var goGood = GameObject.CreatePrimitive(PrimitiveType.Cube);
            goGood.name = "EligibleProp";
            var goBad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            goBad.name = "BrokenRock";

            try
            {
                var mrGood = goGood.GetComponent<MeshRenderer>();
                var mrBad = goBad.GetComponent<MeshRenderer>();
                mrBad.sharedMaterial.enableInstancing = false;

                ResidentDrawerAuditReport sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(new[] { mrGood, mrBad });
                var passes = new List<RenderPassMigrationFinding>
                {
                    RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(NativeUnity6BloomPass)),
                    RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(LegacyCustomOutlinePass))
                };

                var win = new RenderGraphDoctorWindow();
                win.CreateGUI();
                Assert.Greater(win.rootVisualElement.childCount, 0);

                win.SetCustomAuditData(sceneReport, passes);

                ResidentDrawerHeatmapSummary heatmap = RenderGraphDoctorMcpBridge.ComputeHeatmapSummary(sceneReport, passes);
                Assert.AreEqual(2, heatmap.totalRenderers);
                Assert.AreEqual(1, heatmap.eligibleRenderers);
                Assert.AreEqual(1, heatmap.disqualifiedRenderers);
                Assert.AreEqual(0.5f, heatmap.eligibilityRatio, 0.001f);
                Assert.AreEqual(2, heatmap.totalRenderPasses);
                Assert.AreEqual(0.5f, heatmap.renderGraphMigrationRatio, 0.001f);

                // Tab 0: Disqualified scene renderers
                Assert.AreEqual(1, win.GetFilteredSceneRecords().Count);
                Assert.AreEqual("BrokenRock", win.GetFilteredSceneRecords()[0].gameObjectName);

                // Tab 1: RenderPass findings with search filter
                win.SetSelectedTab(1);
                Assert.AreEqual(2, win.GetFilteredPassFindings().Count);
                win.SetSearchFilter("NativeUnity6BloomPass");
                Assert.AreEqual(1, win.GetFilteredPassFindings().Count);
            }
            finally
            {
                Object.DestroyImmediate(goGood);
                Object.DestroyImmediate(goBad);
            }
        }
    }
}
