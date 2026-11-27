using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
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

        private sealed class MockUniversalRendererData
        {
            public int m_RenderingMode = 1; // Standard Deferred (NOT supported by URP 17 GRD!)
        }

        private sealed class MockUniversalRenderPipelineAsset : RenderPipelineAsset
        {
            public bool m_UseSRPBatcher = false;
            public int m_GPUResidentDrawerMode = 0; // Disabled
            public object[] m_RendererDataList = { new MockUniversalRendererData() };

            protected override RenderPipeline CreatePipeline() => null;
        }

        private const string BrgCompatibleTestShaderSrc = @"
Shader ""Custom/TestDotsReadyLit""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            float4 vert() : SV_Position { return 0; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}";

        private const string NonBrgLegacyTestShaderSrc = @"
Shader ""Custom/TestNoDotsLegacy""
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            float4 vert() : SV_Position { return 0; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}";

        [Test]
        public void ResidentDrawerAuditor_DetectsAllGrdExclusionReasonsAndMultiSubmeshDotsKeyword()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var anchorGo = new GameObject("ProbeAnchorTarget");
            var lodHost = new GameObject("LodParent");
            Shader brgShader = ShaderUtil.CreateShaderAsset(BrgCompatibleTestShaderSrc, false);
            Shader nonBrgShader = ShaderUtil.CreateShaderAsset(NonBrgLegacyTestShaderSrc, false);
            var matSub0 = new Material(brgShader) { name = "SubMesh0_BrgOk", enableInstancing = true };
            var matSub1 = new Material(nonBrgShader) { name = "SubMesh1_MissingDots", enableInstancing = true };
            var extraStreamMesh = new Mesh { name = "ExtraVertexStream" };

            try
            {
                var lodGroup = lodHost.AddComponent<LODGroup>();
                lodGroup.fadeMode = LODFadeMode.CrossFade;
                lodGroup.animateCrossFading = true;
                go.transform.parent = lodHost.transform;
                go.transform.localScale = new Vector3(-1f, 1f, 1f);

                var mr = go.GetComponent<MeshRenderer>();
                // Submesh 0 has DOTS_INSTANCING_ON, Submesh 1 is missing DOTS_INSTANCING_ON!
                mr.sharedMaterials = new[] { matSub0, matSub1 };
                mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                mr.probeAnchor = anchorGo.transform;
                mr.additionalVertexStreams = extraStreamMesh;

                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", Color.red);
                mr.SetPropertyBlock(mpb);

                // Omit isShaderCompatiblePredicate so automatic shader.keywordSpace DOTS_INSTANCING_ON check runs!
                ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { mr },
                    isStaticBatchingEnabledPredicate: _ => true);

                Assert.AreEqual(1, report.totalMeshRenderers);
                Assert.AreEqual(1, report.disqualifiedRenderers);
                Assert.AreEqual(1, report.shaderIncompatibilityViolations);
                Assert.AreEqual(1, report.probeAnchorViolations);
                Assert.AreEqual(1, report.customVertexStreamViolations);

                RendererEligibilityRecord rec = report.records[0];
                Assert.IsFalse(rec.IsEligible);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.NegativeTransformScale) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.BlendProbesWithAnchorUnsupported) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.CustomVertexStreamsUsed) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.ShaderMissingDotsInstancing) != 0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(extraStreamMesh);
                UnityEngine.Object.DestroyImmediate(matSub0);
                UnityEngine.Object.DestroyImmediate(matSub1);
                UnityEngine.Object.DestroyImmediate(brgShader);
                UnityEngine.Object.DestroyImmediate(nonBrgShader);
                UnityEngine.Object.DestroyImmediate(anchorGo);
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(lodHost);
            }
        }

        [Test]
        public void PipelinePrerequisiteDoctor_FlagsStandardDeferredAndAutoUpgradesToDeferredPlusAndKeepAll()
        {
            var mockUrp = ScriptableObject.CreateInstance<MockUniversalRenderPipelineAsset>();
            mockUrp.name = "PC_HighFidelity_URP";
            try
            {
                UrpPipelinePrerequisiteDoctor.SetBrgStrippingModeOverrideForTesting(BatchRendererGroupStrippingMode.StripAll);

                PipelinePrerequisiteAuditResult before = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites(mockUrp);
                Assert.IsTrue(before.hasActiveSrpAsset);
                Assert.IsFalse(before.srpBatcherEnabled);
                Assert.IsFalse(before.gpuResidentDrawerEnabled);
                Assert.AreEqual("Deferred", before.rendererRenderingMode);
                Assert.IsFalse(before.isForwardPlusOrDeferredPlus);
                Assert.IsFalse(before.brgKeepAllStrippingConfigured);
                Assert.IsFalse(before.isFullyReadyForResidentDrawer);
                Assert.GreaterOrEqual(before.issues.Count, 4);

                PipelinePrerequisiteAuditResult after = UrpPipelinePrerequisiteDoctor.AutoConfigurePipelineForResidentDrawer(mockUrp);
                Assert.IsTrue(after.isFullyReadyForResidentDrawer);
                Assert.IsTrue(after.srpBatcherEnabled);
                Assert.IsTrue(after.gpuResidentDrawerEnabled);
                Assert.AreEqual("DeferredPlus", after.rendererRenderingMode);
                Assert.IsTrue(after.isForwardPlusOrDeferredPlus);
                Assert.IsTrue(after.brgKeepAllStrippingConfigured);
                Assert.AreEqual(BatchRendererGroupStrippingMode.KeepAll, UrpPipelinePrerequisiteDoctor.GetCurrentBrgStrippingMode());
            }
            finally
            {
                UrpPipelinePrerequisiteDoctor.SetBrgStrippingModeOverrideForTesting(null);
                UnityEngine.Object.DestroyImmediate(mockUrp);
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
        public void AssemblyAuditor_FiltersInternalUnityAssembliesAndClassifiesPasses()
        {
            Assert.IsTrue(RenderGraphAssemblyAuditor.IsIgnoredEngineOrFrameworkAssembly("Unity.RenderPipelines.Universal.Runtime"));
            Assert.IsTrue(RenderGraphAssemblyAuditor.IsIgnoredEngineOrFrameworkAssembly("UnityEditor.CoreModule"));
            Assert.IsFalse(RenderGraphAssemblyAuditor.IsIgnoredEngineOrFrameworkAssembly("Game.CustomRendering"));

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
        public void EightItemRenderGraphAnalyzer_CatchesStalePassDataReadWriteHazardsTextureDescGlobalTexAndConfigureInput()
        {
            const string eightHazardsCSharp = @"
public class HazardRichRenderPass : ScriptableRenderPass
{
    private Material _unusedDeclaredMaterial;

    private class PassData
    {
        public TextureHandle activeSrc;
        public TextureHandle staleUnassignedLut;
        public Material blitMat;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resourceData = frameData.Get<UniversalResourceData>();
        var desc = new TextureDesc(1920, 1080);
        TextureHandle normals = resourceData.cameraNormalsTexture;

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""HazardPass"", out var passData))
        {
            passData.activeSrc = resourceData.activeColorTexture;
            // Notice: passData.staleUnassignedLut is NEVER assigned here (pooled PassData leak)!
            // Simultaneous read + write of same handle:
            builder.UseTexture(passData.activeSrc, AccessFlags.Read);
            builder.SetRenderAttachment(passData.activeSrc, 0, AccessFlags.Write);
            builder.UseAllGlobalTextures(true);

            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetGlobalTexture(""_BadGlobal"", data.staleUnassignedLut);
            });
        }
    }
}";

            const string simpleBlitPassCSharp = @"
public class ManualFullscreenBlitPass : ScriptableRenderPass
{
    private Material _effectMaterial;
    private class PassData { public TextureHandle src; public Material mat; }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resourceData = frameData.Get<UniversalResourceData>();
        var desc = resourceData.activeColorTexture.GetDescriptor(renderGraph);
        TextureHandle dst = renderGraph.CreateTexture(desc);

        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""SimpleBlit"", out var passData))
        {
            passData.src = resourceData.activeColorTexture;
            passData.mat = _effectMaterial;
            builder.UseTexture(passData.src);
            builder.SetRenderAttachment(dst, 0);
            builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, data.src, new Vector4(1, 1, 0, 0), data.mat, 0);
            });
        }
    }
}";

            RenderGraphSourceAuditResult hazards = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(eightHazardsCSharp, "Assets/HazardPass.cs");
            Assert.IsTrue(hazards.hasUnusedOrUnboundMaterialHazard);
            Assert.IsTrue(hazards.hasSimultaneousReadWriteTextureHazard);
            Assert.IsTrue(hazards.hasStalePooledPassDataHandleRisk);
            Assert.IsTrue(hazards.hasManualTextureDescHazard);
            Assert.IsTrue(hazards.hasUnsafeGlobalTextureHazard);
            Assert.IsTrue(hazards.hasMissingConfigureInputDeclaration);
            Assert.IsTrue(hazards.hasNonStaticRenderFuncClosureRisk);

            RenderGraphSourceAuditResult blitAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(simpleBlitPassCSharp, "Assets/SimpleBlit.cs");
            Assert.IsFalse(blitAudit.hasManualTextureDescHazard);
            Assert.IsFalse(blitAudit.hasStalePooledPassDataHandleRisk);
            Assert.IsTrue(blitAudit.canSimplifyFullscreenBlitPass);
        }

        [Test]
        public void CaptureSafeAutoFixer_HoistsCapturedFieldsIntoPassDataAndRefusesBrokenThisCapture()
        {
            const string passCapturingInstanceFields = @"
public class CapturedFieldPass : ScriptableRenderPass
{
    private Material _customMat;
    private float _intensity = 0.75f;

    private class PassData
    {
        public TextureHandle src;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""CapturePass"", out var passData))
        {
            passData.src = frameData.Get<UniversalResourceData>().activeColorTexture;
            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
                _customMat.SetFloat(""_Intensity"", _intensity);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, _customMat, 0, MeshTopology.Triangles, 3);
            });
        }
    }
}";

            RenderPassSourceAutoFixReport hoistedFix = RenderGraphDoctorMcpBridge.AutoFixRenderPassCSharpSource(
                passCapturingInstanceFields,
                "Assets/CapturedFieldPass.cs");

            Assert.IsTrue(hoistedFix.modified);
            Assert.AreEqual(2, hoistedFix.hoistedCapturedFieldCount);
            Assert.AreEqual(0, hoistedFix.skippedUnsafeCaptureLambdaCount);
            Assert.IsFalse(hoistedFix.afterAudit.hasNonStaticRenderFuncClosureRisk);
            Assert.IsTrue(hoistedFix.afterAudit.usesStaticRenderFuncLambda);
            StringAssert.Contains("public Material _customMat;", hoistedFix.patchedSource);
            StringAssert.Contains("public float _intensity;", hoistedFix.patchedSource);
            StringAssert.Contains("passData._customMat = _customMat;", hoistedFix.patchedSource);
            StringAssert.Contains("passData._intensity = _intensity;", hoistedFix.patchedSource);
            StringAssert.Contains("data._customMat.SetFloat(\"_Intensity\", data._intensity);", hoistedFix.patchedSource);
            StringAssert.Contains("SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>", hoistedFix.patchedSource);
            StringAssert.Contains("--- a/Assets/CapturedFieldPass.cs", hoistedFix.unifiedDiff);

            // Lambda invoking `this.Helper()` MUST NOT blindly get `static` (which would cause CS8820)!
            const string passWithThisCall = @"
public class UnsafeThisCallPass : ScriptableRenderPass
{
    private void Helper() { }
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        using (var builder = renderGraph.AddRasterRenderPass<PassData>(""UnsafeThis"", out var passData))
        {
            builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
            {
                this.Helper();
            });
        }
    }
}";

            RenderPassSourceAutoFixReport skippedFix = RenderGraphDoctorMcpBridge.AutoFixRenderPassCSharpSource(
                passWithThisCall,
                "Assets/UnsafeThisCallPass.cs");

            Assert.IsFalse(skippedFix.modified);
            Assert.AreEqual(1, skippedFix.skippedUnsafeCaptureLambdaCount);
            StringAssert.DoesNotContain("SetRenderFunc(static", skippedFix.patchedSource);
        }

        [Test]
        public void McpBridge_AutoFixSceneRenderers_FixesMultiMaterialWithoutMutatingBuiltInDefaultMaterial()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var anchor = new GameObject("Anchor");
            Shader brgShader = ShaderUtil.CreateShaderAsset(BrgCompatibleTestShaderSrc, false);
            var customMat = new Material(brgShader) { name = "CustomTestRockMat", enableInstancing = false };

            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                // Assign our own temporary Material instead of mutating Unity's built-in Default-Material!
                mr.sharedMaterial = customMat;
                mr.lightProbeUsage = LightProbeUsage.UseProxyVolume;
                mr.probeAnchor = anchor.transform;
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
                Assert.AreEqual(1, sceneFix.probeAnchorsCleared);
                Assert.AreEqual(1, sceneFix.staticBatchingFlagsCleared);
                Assert.IsFalse(mr.HasPropertyBlock());
                Assert.IsTrue(customMat.enableInstancing);
                Assert.AreEqual(LightProbeUsage.BlendProbes, mr.lightProbeUsage);
                Assert.IsNull(mr.probeAnchor);

                ResidentDrawerAuditReport afterScene = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { mr },
                    _ => staticBatchFlag);
                Assert.AreEqual(1, afterScene.eligibleRenderers);
                Assert.AreEqual(0, afterScene.disqualifiedRenderers);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(customMat);
                UnityEngine.Object.DestroyImmediate(brgShader);
                UnityEngine.Object.DestroyImmediate(anchor);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task McpBridge_And_LiveLoopbackServer_EnforceNotificationsSecurityAndPathTraversal()
        {
            string initRes = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");
            StringAssert.Contains(@"""protocolVersion"":""2024-11-05""", initRes);
            StringAssert.Contains(@"""version"":""2.0.0""", initRes);

            Assert.AreEqual(string.Empty, RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}"));

            StringAssert.Contains(@"""result"":{}", RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""ping""}"));

            StringAssert.Contains(RenderGraphDoctorMcpBridge.LatestReportResourceUri, RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""resources/list""}"));

            // Path traversal rejection on audit_project_prefabs
            string traversalRes = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""rendergraph_doctor.audit_project_prefabs"",""arguments"":{""rootDirectory"":""../../Windows""}}}");
            StringAssert.Contains("-32602", traversalRes);

            using (var server = new RenderGraphDoctorMcpLiveServer(dispatchDirectlyOnBackgroundThreadForTests: true))
            {
                server.Start(0, "rgd-secret-token");
                using (var http = new HttpClient())
                {
                    var unauth = await http.PostAsync(
                        server.EndpointUrl,
                        new StringContent(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", Encoding.UTF8, "application/json"));
                    Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

                    var badOrigin = new HttpRequestMessage(HttpMethod.Post, server.EndpointUrl)
                    {
                        Content = new StringContent(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", Encoding.UTF8, "application/json")
                    };
                    badOrigin.Headers.Add("Authorization", "Bearer rgd-secret-token");
                    badOrigin.Headers.Add("Origin", "https://evil.example.org");
                    var badResp = await http.SendAsync(badOrigin);
                    Assert.AreEqual(HttpStatusCode.Forbidden, badResp.StatusCode);

                    var okReq = new HttpRequestMessage(HttpMethod.Post, server.EndpointUrl)
                    {
                        Content = new StringContent(@"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/list""}", Encoding.UTF8, "application/json")
                    };
                    okReq.Headers.Add("Authorization", "Bearer rgd-secret-token");
                    var okResp = await http.SendAsync(okReq);
                    Assert.AreEqual(HttpStatusCode.OK, okResp.StatusCode);
                    string body = await okResp.Content.ReadAsStringAsync();
                    StringAssert.Contains("rendergraph_doctor.autoconfigure_pipeline_prerequisites", body);
                    StringAssert.Contains("rendergraph_doctor.audit_project_prefabs", body);
                }
            }
        }

        [Test]
        public void UiToolkitWindow_SupportsVirtualized20kRowsPainterChartAndCiQualityGates()
        {
            var win = ScriptableObject.CreateInstance<RenderGraphDoctorWindow>();
            Shader brgShader = ShaderUtil.CreateShaderAsset(BrgCompatibleTestShaderSrc, false);
            var matGood = new Material(brgShader) { name = "MatGood", enableInstancing = true };
            var matBad = new Material(brgShader) { name = "MatBad", enableInstancing = false };
            var goGood = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var goBad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            goGood.name = "EligibleProp";
            goBad.name = "BrokenRock";

            try
            {
                goGood.GetComponent<MeshRenderer>().sharedMaterial = matGood;
                goBad.GetComponent<MeshRenderer>().sharedMaterial = matBad;

                ResidentDrawerAuditReport sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    new[] { goGood.GetComponent<MeshRenderer>(), goBad.GetComponent<MeshRenderer>() });

                var passes = new List<RenderPassMigrationFinding>
                {
                    RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(NativeUnity6BloomPass)),
                    RenderGraphAssemblyAuditor.AnalyzeRenderPassType(typeof(LegacyCustomOutlinePass))
                };

                win.CreateGUI();
                Assert.Greater(win.rootVisualElement.childCount, 0);
                Assert.IsNotNull(win.VirtualizedSceneTable);
                Assert.IsNotNull(win.VirtualizedPassTable);

                win.SetCustomAuditData(sceneReport, passes);
                Assert.AreEqual(1, win.GetFilteredSceneRecords().Count);
                Assert.AreEqual("BrokenRock", win.GetFilteredSceneRecords()[0].gameObjectName);

                // CI Quality Gate fails when legacy Execute-only pass or disqualified renderers exist
                Assert.IsFalse(RenderGraphDoctorCli.EvaluateCiQualityGate(null, sceneReport, passes, out string ciReason));
                StringAssert.Contains("LegacyCustomOutlinePass", ciReason);

                // 20,000-row virtualized MultiColumnListView stress test
                for (int i = 0; i < 19999; i++)
                {
                    sceneReport.records.Add(new RendererEligibilityRecord
                    {
                        gameObjectName = $"DisqualifiedObj_{i}",
                        materialName = "MatBad",
                        shaderName = "Custom/Test",
                        disqualifiers = ResidentDrawerDisqualifier.InstancingDisabledOnMaterial,
                        recommendation = "Enable GPU Instancing"
                    });
                }
                win.SetSelectedTab(0);
                win.SetCustomAuditData(sceneReport, passes);
                Assert.AreEqual(20000, win.VirtualizedSceneTable.itemsSource.Count);

                win.SetSelectedTab(1);
                win.SetSearchFilter("NativeUnity6BloomPass");
                Assert.AreEqual(1, win.GetFilteredPassFindings().Count);
                Assert.IsNotNull(typeof(RenderGraphDoctorCli).GetMethod("RunMcpAuditJson", Type.EmptyTypes));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(win);
                UnityEngine.Object.DestroyImmediate(matGood);
                UnityEngine.Object.DestroyImmediate(matBad);
                UnityEngine.Object.DestroyImmediate(brgShader);
                UnityEngine.Object.DestroyImmediate(goGood);
                UnityEngine.Object.DestroyImmediate(goBad);
            }
        }
    }
}
