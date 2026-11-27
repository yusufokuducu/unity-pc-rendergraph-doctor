using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    public static class RenderGraphDoctorCli
    {
        /// <summary>
        /// Batchmode / CI entrypoint:
        /// unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunCiAudit -failOnCritical
        /// </summary>
        public static void RunCiAudit()
        {
            string[] args = Environment.GetCommandLineArgs();
            ParseCliOptions(args, out bool failOnCritical);

            PipelinePrerequisiteAuditResult pipeReport = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
            MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
            ResidentDrawerAuditReport sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                renderers,
                go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic),
                GpuResidentDrawerAuditor.IsShaderDotsInstancingCompatible);

            List<RenderPassMigrationFinding> passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            var sb = new StringBuilder();
            sb.AppendLine(sceneReport.ExportToMarkdown());
            sb.AppendLine();
            sb.AppendLine(RenderGraphAssemblyAuditor.ExportPassFindingsToMarkdown(passFindings));

            string outPath = Path.Combine(logDir, "RenderGraphDoctorAudit.md");
            string jsonPath = Path.Combine(logDir, "RenderGraphDoctorMcpReport.json");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(jsonPath, RenderGraphDoctorMcpBridge.ExportFullMcpReportToJson(sceneReport, passFindings, pipeReport), Encoding.UTF8);

            bool passedGate = EvaluateCiQualityGate(pipeReport, sceneReport, passFindings, out string gateReason);
            Debug.Log(
                $"[PcRenderGraphDoctor CI] Audited {sceneReport.totalMeshRenderers} MeshRenderers " +
                $"({sceneReport.disqualifiedRenderers} disqualified) and {passFindings.Count} user ScriptableRenderPass classes. " +
                $"GatePassed={passedGate}. Report saved to {outPath}");

            if (failOnCritical && !passedGate)
            {
                Debug.LogError($"[PcRenderGraphDoctor CI] Quality gate failed: {gateReason}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        public static bool EvaluateCiQualityGate(
            PipelinePrerequisiteAuditResult pipeReport,
            ResidentDrawerAuditReport sceneReport,
            IReadOnlyList<RenderPassMigrationFinding> passFindings,
            out string failureReason)
        {
            failureReason = string.Empty;

            if (pipeReport != null && pipeReport.hasActiveSrpAsset && !pipeReport.isFullyReadyForResidentDrawer)
            {
                failureReason = "Pipeline prerequisites incomplete: " + string.Join("; ", pipeReport.issues);
                return false;
            }

            if (passFindings != null)
            {
                for (int i = 0; i < passFindings.Count; i++)
                {
                    if (!passFindings[i].implementsRecordRenderGraph && passFindings[i].implementsLegacyExecute)
                    {
                        failureReason = $"Custom render pass '{passFindings[i].typeFullName}' only implements legacy Execute() without RecordRenderGraph().";
                        return false;
                    }
                }
            }

            if (sceneReport != null && sceneReport.disqualifiedRenderers > 0)
            {
                failureReason = $"Scene contains {sceneReport.disqualifiedRenderers} disqualified GPU Resident Drawer MeshRenderer(s).";
                return false;
            }

            return true;
        }

        public static void ParseCliOptions(string[] args, out bool failOnCritical)
        {
            failOnCritical = false;
            if (args == null) return;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-failOnCritical", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "--fail-on-critical", StringComparison.OrdinalIgnoreCase))
                {
                    failOnCritical = true;
                }
            }
        }

        public static string RunMcpAuditJson()
        {
            PipelinePrerequisiteAuditResult pipeReport = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
            MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
            ResidentDrawerAuditReport sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                renderers,
                go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic),
                GpuResidentDrawerAuditor.IsShaderDotsInstancingCompatible);

            List<RenderPassMigrationFinding> passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
            string json = RenderGraphDoctorMcpBridge.ExportFullMcpReportToJson(sceneReport, passFindings, pipeReport);

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            string jsonPath = Path.Combine(logDir, "RenderGraphDoctorMcpReport.json");
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
            Debug.Log($"[PcRenderGraphDoctor MCP] Exported structured JSON audit to {jsonPath}");
            return json;
        }
    }
}
