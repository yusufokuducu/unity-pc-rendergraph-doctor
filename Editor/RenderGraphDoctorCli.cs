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
        /// unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunCiAudit
        /// </summary>
        public static void RunCiAudit()
        {
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            ResidentDrawerAuditReport sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                renderers,
                go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));

            List<RenderPassMigrationFinding> passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();

            string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            var sb = new StringBuilder();
            sb.AppendLine(sceneReport.ExportToMarkdown());
            sb.AppendLine();
            sb.AppendLine(RenderGraphAssemblyAuditor.ExportPassFindingsToMarkdown(passFindings));

            string outPath = Path.Combine(logDir, "RenderGraphDoctorAudit.md");
            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);

            Debug.Log(
                $"[PcRenderGraphDoctor CI] Audited {sceneReport.totalMeshRenderers} MeshRenderers " +
                $"({sceneReport.disqualifiedRenderers} disqualified) and {passFindings.Count} ScriptableRenderPass classes. " +
                $"Report saved to {outPath}");
        }
    }
}
