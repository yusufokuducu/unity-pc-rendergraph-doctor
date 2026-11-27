#if YUSUF_UNITY_PIPELINE_CLI
using Unity.Pipeline.Commands;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor.PipelineCli
{
    /// <summary>
    /// Native Unity 6 com.unity.pipeline ([CliCommand]) bindings for `unity mcp` and `unity command`.
    /// </summary>
    public static class RenderGraphDoctorPipelineCommands
    {
        [CliCommand("rendergraph_doctor", "audit", "Run full GPU Resident Drawer scene/pipeline & Render Graph assembly audit as JSON", MainThreadRequired = true)]
        public static string RunFullAudit()
        {
            return RenderGraphDoctorCli.RunMcpAuditJson();
        }

        [CliCommand("rendergraph_doctor", "configure_pipeline", "Auto-configure active URP 17 asset (Forward+/Deferred+, SRP Batcher, InstancedDrawing, BRG KeepAll)", MainThreadRequired = true)]
        public static string ConfigurePipeline()
        {
            PipelinePrerequisiteAuditResult res = UrpPipelinePrerequisiteDoctor.AutoConfigurePipelineForResidentDrawer();
            return RenderGraphDoctorMcpBridge.SerializePipelinePrerequisiteJson(res);
        }

        [CliCommand("rendergraph_doctor", "autofix_scene", "Auto-fix scene MeshRenderer GPU Resident Drawer disqualifiers (MPB, Instancing, LPPV, BatchingStatic)", MainThreadRequired = true)]
        public static string AutoFixScene()
        {
            return RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""rendergraph_doctor.autofix_scene_renderers""}}");
        }

        [CliCommand("rendergraph_doctor", "audit_prefabs", "Audit all Prefabs under Assets/ for GPU Resident Drawer eligibility", MainThreadRequired = true)]
        public static string AuditPrefabs(
            [CliArg("folder", "Project-relative folder under Assets/")] string folder = "Assets")
        {
            string safeFolder = RenderGraphDoctorMcpBridge.SanitizeProjectRelativePath(folder);
            ResidentDrawerAuditReport report = UrpPipelinePrerequisiteDoctor.AuditProjectPrefabsForResidentDrawer(safeFolder);
            return RenderGraphDoctorMcpBridge.SerializeSceneReportJson(report);
        }
    }
}
#endif
