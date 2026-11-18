using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    [Serializable]
    public struct ResidentDrawerHeatmapSummary
    {
        public int totalRenderers;
        public int eligibleRenderers;
        public int disqualifiedRenderers;
        public float eligibilityRatio;
        public float disqualifiedRatio;
        public int propertyBlockViolations;
        public int instancingDisabledViolations;
        public int staticBatchingConflicts;
        public int negativeScaleViolations;
        public int lppvViolations;
        public int shaderIncompatibilityViolations;
        public int desktopOverdrawRiskMeshes;
        public int totalRenderPasses;
        public int nativeRenderGraphPasses;
        public int dualPathPasses;
        public int legacyOnlyPasses;
        public float renderGraphMigrationRatio;
    }

    [Serializable]
    public sealed class SceneAutoFixReport
    {
        public int totalInspected;
        public int renderersFixed;
        public int propertyBlocksCleared;
        public int materialsInstancingEnabled;
        public int lppvConvertedToBlendProbes;
        public int staticBatchingFlagsCleared;
        public List<string> appliedFixes = new List<string>();
    }

    [Serializable]
    public sealed class RenderPassSourceAutoFixReport
    {
        public string filePath;
        public bool modified;
        public string patchedSource;
        public List<string> appliedFixes = new List<string>();
        public RenderGraphSourceAuditResult beforeAudit;
        public RenderGraphSourceAuditResult afterAudit;
    }

    /// <summary>
    /// Model Context Protocol (MCP) & AI Agent JSON-RPC 2.0 Bridge for PC Unity 6 Render Graph & GPU Resident Drawer Doctor.
    /// Allows AI coding assistants (Cursor, Claude, Antigravity, Unity MCP servers) and CI pipelines
    /// to query BatchRendererGroup / GPU Resident Drawer scene eligibility, audit custom shaders & Render Graph passes,
    /// and invoke automated scene/source remediation.
    /// </summary>
    public static class RenderGraphDoctorMcpBridge
    {
        public const string ServerName = "unity-pc-rendergraph-doctor";
        public const string PackageVersion = "1.2.0";
        public const string McpProtocolVersion = "2024-11-05";

        private static readonly Regex NonStaticSetRenderFuncLambdaRegex = new Regex(
            @"(\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*)(?!static\b)(\([^)]*\)\s*=>|[A-Za-z_]\w*\s*=>)",
            RegexOptions.Compiled);

        public static ResidentDrawerHeatmapSummary ComputeHeatmapSummary(
            ResidentDrawerAuditReport sceneReport,
            IReadOnlyList<RenderPassMigrationFinding> passFindings = null)
        {
            var h = new ResidentDrawerHeatmapSummary();
            if (sceneReport != null)
            {
                h.totalRenderers = sceneReport.totalMeshRenderers;
                h.eligibleRenderers = sceneReport.eligibleRenderers;
                h.disqualifiedRenderers = sceneReport.disqualifiedRenderers;
                h.propertyBlockViolations = sceneReport.propertyBlockViolations;
                h.instancingDisabledViolations = sceneReport.instancingDisabledViolations;
                h.staticBatchingConflicts = sceneReport.staticBatchingConflicts;
                h.negativeScaleViolations = sceneReport.negativeScaleViolations;
                h.lppvViolations = sceneReport.lppvViolations;
                h.shaderIncompatibilityViolations = sceneReport.shaderIncompatibilityViolations;
                h.desktopOverdrawRiskMeshes = sceneReport.desktopOverdrawRiskMeshes;

                if (h.totalRenderers > 0)
                {
                    h.eligibilityRatio = (float)h.eligibleRenderers / h.totalRenderers;
                    h.disqualifiedRatio = (float)h.disqualifiedRenderers / h.totalRenderers;
                }
            }

            if (passFindings != null && passFindings.Count > 0)
            {
                h.totalRenderPasses = passFindings.Count;
                for (int i = 0; i < passFindings.Count; i++)
                {
                    RenderPassMigrationFinding f = passFindings[i];
                    if (f.implementsRecordRenderGraph && !f.implementsLegacyExecute)
                        h.nativeRenderGraphPasses++;
                    else if (f.implementsRecordRenderGraph && f.implementsLegacyExecute)
                        h.dualPathPasses++;
                    else if (f.implementsLegacyExecute)
                        h.legacyOnlyPasses++;
                }

                int migrated = h.nativeRenderGraphPasses + h.dualPathPasses;
                h.renderGraphMigrationRatio = (float)migrated / h.totalRenderPasses;
            }
            else
            {
                h.renderGraphMigrationRatio = 1f;
            }

            return h;
        }

        /// <summary>
        /// Automatically fixes Unity 6 GPU Resident Drawer (BatchRendererGroup) disqualifiers on scene MeshRenderers:
        /// 1. Clears MaterialPropertyBlock overrides (SetPropertyBlock(null)).
        /// 2. Enables GPU Instancing on sharedMaterials.
        /// 3. Converts LightProbeUsage.UseProxyVolume (LPPV) to LightProbeUsage.BlendProbes.
        /// 4. Clears conflicting StaticEditorFlags.BatchingStatic flags.
        /// </summary>
        public static SceneAutoFixReport AutoFixSceneRenderers(
            IReadOnlyList<MeshRenderer> renderers,
            Func<GameObject, bool> isStaticBatchingEnabled = null,
            Action<GameObject> clearStaticBatchingFlag = null)
        {
            var report = new SceneAutoFixReport();
            if (renderers == null || renderers.Count == 0)
                return report;

            Func<GameObject, bool> checkStatic = isStaticBatchingEnabled ??
                (go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));

            Action<GameObject> clearStatic = clearStaticBatchingFlag ?? (go =>
            {
                Undo.RecordObject(go, "Clear BatchingStatic for GPU Resident Drawer");
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
                flags &= ~StaticEditorFlags.BatchingStatic;
                GameObjectUtility.SetStaticEditorFlags(go, flags);
            });

            for (int i = 0; i < renderers.Count; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null) continue;

                report.totalInspected++;
                bool fixedAny = false;
                string goName = mr.gameObject != null ? mr.gameObject.name : $"MeshRenderer[{i}]";

                if (mr.HasPropertyBlock())
                {
                    mr.SetPropertyBlock(null);
                    report.propertyBlocksCleared++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Cleared MaterialPropertyBlock override.");
                }

                if (mr.sharedMaterial != null && !mr.sharedMaterial.enableInstancing)
                {
                    Undo.RecordObject(mr.sharedMaterial, "Enable GPU Instancing");
                    mr.sharedMaterial.enableInstancing = true;
                    EditorUtility.SetDirty(mr.sharedMaterial);
                    report.materialsInstancingEnabled++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Enabled GPU Instancing on material '{mr.sharedMaterial.name}'.");
                }

                if (mr.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                {
                    mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                    report.lppvConvertedToBlendProbes++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Switched LightProbeUsage from UseProxyVolume (LPPV) to BlendProbes.");
                }

                if (mr.gameObject != null && checkStatic(mr.gameObject))
                {
                    clearStatic(mr.gameObject);
                    report.staticBatchingFlagsCleared++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Cleared conflicting StaticEditorFlags.BatchingStatic flag.");
                }

                if (fixedAny)
                    report.renderersFixed++;
            }

            return report;
        }

        /// <summary>
        /// Automatically upgrades non-static SetRenderFunc lambdas in Unity 6 Render Graph C# source code
        /// to 'static' lambdas to eliminate per-frame delegate/closure GC allocations.
        /// </summary>
        public static RenderPassSourceAutoFixReport AutoFixRenderPassCSharpSource(
            string rawCSharpSource,
            string filePath = "<Memory>")
        {
            string safePath = string.IsNullOrEmpty(filePath) ? "<Memory>" : filePath;
            RenderGraphSourceAuditResult before = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(rawCSharpSource, safePath);

            var report = new RenderPassSourceAutoFixReport
            {
                filePath = safePath,
                patchedSource = rawCSharpSource ?? string.Empty,
                beforeAudit = before
            };

            if (string.IsNullOrWhiteSpace(rawCSharpSource))
            {
                report.afterAudit = before;
                return report;
            }

            int upgradedLambdas = 0;
            string working = NonStaticSetRenderFuncLambdaRegex.Replace(rawCSharpSource, match =>
            {
                upgradedLambdas++;
                return match.Groups[1].Value + "static " + match.Groups[2].Value;
            });

            if (upgradedLambdas > 0)
            {
                report.appliedFixes.Add(
                    $"Upgraded {upgradedLambdas} non-static SetRenderFunc lambda(s) to 'static' to eliminate per-frame GC closure allocations.");
            }

            RenderGraphSourceAuditResult after = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(working, safePath);
            report.patchedSource = working;
            report.modified = !string.Equals(rawCSharpSource, working, StringComparison.Ordinal);
            report.afterAudit = after;

            return report;
        }

        public static string ExportFullMcpReportToJson(
            ResidentDrawerAuditReport sceneReport,
            IReadOnlyList<RenderPassMigrationFinding> passFindings)
        {
            ResidentDrawerHeatmapSummary heatmap = ComputeHeatmapSummary(sceneReport, passFindings);
            var sb = new StringBuilder(2048);
            sb.Append('{');
            AppendJsonProp(sb, "tool", ServerName, true);
            AppendJsonProp(sb, "version", PackageVersion, true);
            sb.Append("\"heatmap\":");
            AppendHeatmapJson(sb, heatmap);
            sb.Append(",\"sceneAudit\":");
            AppendSceneReportJson(sb, sceneReport ?? new ResidentDrawerAuditReport());
            sb.Append(",\"renderPassAudit\":[");
            if (passFindings != null)
            {
                for (int i = 0; i < passFindings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RenderPassMigrationFinding f = passFindings[i];
                    sb.Append('{');
                    AppendJsonProp(sb, "typeFullName", f.typeFullName, true);
                    AppendJsonProp(sb, "assemblyName", f.assemblyName, true);
                    AppendJsonProp(sb, "implementsRecordRenderGraph", f.implementsRecordRenderGraph, true);
                    AppendJsonProp(sb, "implementsLegacyExecute", f.implementsLegacyExecute, true);
                    AppendJsonProp(sb, "migrationStatus", f.migrationStatus, false);
                    sb.Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Processes a JSON-RPC 2.0 request according to the Model Context Protocol (MCP) specification.
        /// Supported methods: "initialize", "tools/list", "tools/call".
        /// </summary>
        public static string HandleJsonRpcRequest(string jsonRpcRequest)
        {
            if (string.IsNullOrWhiteSpace(jsonRpcRequest))
                return BuildJsonRpcError("null", -32700, "Parse error: Empty JSON-RPC request.");

            string trimmed = jsonRpcRequest.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return BuildJsonRpcError("null", -32700, "Parse error: Malformed JSON object.");

            string idRaw = ExtractJsonRpcIdRaw(trimmed);
            string method = ExtractJsonStringField(trimmed, "method");

            if (string.IsNullOrEmpty(method))
                return BuildJsonRpcError(idRaw, -32600, "Invalid Request: Missing 'method' field.");

            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                var res = new StringBuilder();
                res.Append("{\"protocolVersion\":\"").Append(McpProtocolVersion).Append("\",");
                res.Append("\"capabilities\":{\"tools\":{\"listChanged\":false}},");
                res.Append("\"serverInfo\":{\"name\":\"").Append(ServerName).Append("\",\"version\":\"").Append(PackageVersion).Append("\"}}");
                return BuildJsonRpcResult(idRaw, res.ToString());
            }

            if (string.Equals(method, "tools/list", StringComparison.Ordinal))
            {
                return BuildJsonRpcResult(idRaw, BuildToolsListJson());
            }

            if (string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                string toolName = ExtractJsonStringField(trimmed, "name");
                if (string.IsNullOrEmpty(toolName))
                    return BuildJsonRpcError(idRaw, -32602, "Invalid params: Missing tool 'name' in tools/call.");

                if (string.Equals(toolName, "rendergraph_doctor.audit_scene_renderers", StringComparison.Ordinal))
                {
                    MeshRenderer[] renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                    ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(
                        renderers,
                        go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));
                    List<RenderPassMigrationFinding> passes = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                    string payload = ExportFullMcpReportToJson(report, passes);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "rendergraph_doctor.autofix_scene_renderers", StringComparison.Ordinal))
                {
                    MeshRenderer[] renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
                    SceneAutoFixReport fixReport = AutoFixSceneRenderers(renderers);
                    string payload = SerializeSceneAutoFixJson(fixReport);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "rendergraph_doctor.audit_shader_brg", StringComparison.Ordinal))
                {
                    string source = ExtractJsonStringField(trimmed, "source");
                    string assetPath = ExtractJsonStringField(trimmed, "assetPath") ?? "<McpInlineShader.shader>";
                    ShaderResidentDrawerAudit shaderAudit = GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer(source ?? string.Empty, assetPath);
                    string payload = SerializeShaderBrgAuditJson(shaderAudit);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "rendergraph_doctor.audit_renderpass_csharp", StringComparison.Ordinal))
                {
                    string source = ExtractJsonStringField(trimmed, "source");
                    string filePath = ExtractJsonStringField(trimmed, "filePath") ?? "<McpInlinePass.cs>";
                    RenderGraphSourceAuditResult passAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(source ?? string.Empty, filePath);
                    string payload = SerializePassSourceAuditJson(passAudit);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                if (string.Equals(toolName, "rendergraph_doctor.autofix_renderpass_csharp", StringComparison.Ordinal))
                {
                    string source = ExtractJsonStringField(trimmed, "source");
                    string filePath = ExtractJsonStringField(trimmed, "filePath") ?? "<McpInlinePass.cs>";
                    RenderPassSourceAutoFixReport fixReport = AutoFixRenderPassCSharpSource(source ?? string.Empty, filePath);
                    string payload = SerializePassAutoFixJson(fixReport);
                    return BuildJsonRpcToolCallResult(idRaw, payload);
                }

                return BuildJsonRpcError(idRaw, -32602, $"Unknown tool '{EscapeJson(toolName)}'.");
            }

            return BuildJsonRpcError(idRaw, -32601, $"Method not found: '{EscapeJson(method)}'.");
        }

        public static string SerializeSceneAutoFixJson(SceneAutoFixReport r)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonProp(sb, "totalInspected", r.totalInspected, true);
            AppendJsonProp(sb, "renderersFixed", r.renderersFixed, true);
            AppendJsonProp(sb, "propertyBlocksCleared", r.propertyBlocksCleared, true);
            AppendJsonProp(sb, "materialsInstancingEnabled", r.materialsInstancingEnabled, true);
            AppendJsonProp(sb, "lppvConvertedToBlendProbes", r.lppvConvertedToBlendProbes, true);
            AppendJsonProp(sb, "staticBatchingFlagsCleared", r.staticBatchingFlagsCleared, true);
            sb.Append("\"appliedFixes\":[");
            if (r.appliedFixes != null)
            {
                for (int i = 0; i < r.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializeShaderBrgAuditJson(ShaderResidentDrawerAudit a)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonProp(sb, "shaderName", a.shaderName, true);
            AppendJsonProp(sb, "assetPath", a.assetPath, true);
            AppendJsonProp(sb, "hasTarget45OrHigher", a.hasTarget45OrHigher, true);
            AppendJsonProp(sb, "hasDotsInstancingPragma", a.hasDotsInstancingPragma, true);
            AppendJsonProp(sb, "hasSrpBatcherCBuffer", a.hasSrpBatcherCBuffer, true);
            AppendJsonProp(sb, "hasDotsInstancedProps", a.hasDotsInstancedProps, true);
            AppendJsonProp(sb, "isCompatibleWithResidentDrawer", a.isCompatibleWithResidentDrawer, true);
            AppendJsonProp(sb, "diagnosticMessage", a.diagnosticMessage, false);
            sb.Append('}');
            return sb.ToString();
        }

        public static string SerializePassSourceAuditJson(RenderGraphSourceAuditResult a)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonProp(sb, "filePath", a.filePath, true);
            AppendJsonProp(sb, "usesRecordRenderGraph", a.usesRecordRenderGraph, true);
            AppendJsonProp(sb, "usesAddRasterRenderPass", a.usesAddRasterRenderPass, true);
            AppendJsonProp(sb, "usesAddUnsafePass", a.usesAddUnsafePass, true);
            AppendJsonProp(sb, "usesStaticRenderFuncLambda", a.usesStaticRenderFuncLambda, true);
            AppendJsonProp(sb, "hasNonStaticRenderFuncClosureRisk", a.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "usesLegacyCommandBufferPoolInGraph", a.usesLegacyCommandBufferPoolInGraph, true);
            AppendJsonProp(sb, "usesLegacyGetTemporaryRtInGraph", a.usesLegacyGetTemporaryRtInGraph, true);
            sb.Append("\"findings\":[");
            if (a.findings != null)
            {
                for (int i = 0; i < a.findings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(a.findings[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializePassAutoFixJson(RenderPassSourceAutoFixReport r)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendJsonProp(sb, "filePath", r.filePath, true);
            AppendJsonProp(sb, "modified", r.modified, true);
            AppendJsonProp(sb, "hadClosureRiskBefore", r.beforeAudit != null && r.beforeAudit.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "hasClosureRiskAfter", r.afterAudit != null && r.afterAudit.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "patchedSource", r.patchedSource, true);
            sb.Append("\"appliedFixes\":[");
            if (r.appliedFixes != null)
            {
                for (int i = 0; i < r.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildToolsListJson()
        {
            return "{\"tools\":[" +
                "{\"name\":\"rendergraph_doctor.audit_scene_renderers\",\"description\":\"Audits scene MeshRenderers for Unity 6 GPU Resident Drawer (BatchRendererGroup) eligibility and inspects loaded assemblies for ScriptableRenderPass RecordRenderGraph compliance.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.autofix_scene_renderers\",\"description\":\"Automatically fixes disqualified scene MeshRenderers by clearing MaterialPropertyBlocks, enabling GPU Instancing, switching LPPV to BlendProbes, and clearing StaticBatching flags.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.audit_shader_brg\",\"description\":\"Audits ShaderLab/HLSL source code for Unity 6 GPU Resident Drawer requirements (#pragma target 4.5, DOTS_INSTANCING_ON, UnityPerMaterial CBUFFER).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"assetPath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}," +
                "{\"name\":\"rendergraph_doctor.audit_renderpass_csharp\",\"description\":\"Statically analyzes C# ScriptableRenderPass source code for Unity 6 Render Graph hazards (non-static SetRenderFunc closure allocations, AddUnsafePass, CommandBufferPool.Get, RenderTexture.GetTemporary).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"filePath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}," +
                "{\"name\":\"rendergraph_doctor.autofix_renderpass_csharp\",\"description\":\"Automatically upgrades non-static SetRenderFunc lambdas in Unity 6 Render Graph C# source code to static lambdas to eliminate per-frame GC closure allocations.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"filePath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}" +
                "]}";
        }

        private static void AppendHeatmapJson(StringBuilder sb, ResidentDrawerHeatmapSummary h)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalRenderers", h.totalRenderers, true);
            AppendJsonProp(sb, "eligibleRenderers", h.eligibleRenderers, true);
            AppendJsonProp(sb, "disqualifiedRenderers", h.disqualifiedRenderers, true);
            AppendJsonProp(sb, "eligibilityRatio", h.eligibilityRatio, true);
            AppendJsonProp(sb, "disqualifiedRatio", h.disqualifiedRatio, true);
            AppendJsonProp(sb, "propertyBlockViolations", h.propertyBlockViolations, true);
            AppendJsonProp(sb, "instancingDisabledViolations", h.instancingDisabledViolations, true);
            AppendJsonProp(sb, "staticBatchingConflicts", h.staticBatchingConflicts, true);
            AppendJsonProp(sb, "negativeScaleViolations", h.negativeScaleViolations, true);
            AppendJsonProp(sb, "lppvViolations", h.lppvViolations, true);
            AppendJsonProp(sb, "shaderIncompatibilityViolations", h.shaderIncompatibilityViolations, true);
            AppendJsonProp(sb, "desktopOverdrawRiskMeshes", h.desktopOverdrawRiskMeshes, true);
            AppendJsonProp(sb, "totalRenderPasses", h.totalRenderPasses, true);
            AppendJsonProp(sb, "nativeRenderGraphPasses", h.nativeRenderGraphPasses, true);
            AppendJsonProp(sb, "dualPathPasses", h.dualPathPasses, true);
            AppendJsonProp(sb, "legacyOnlyPasses", h.legacyOnlyPasses, true);
            AppendJsonProp(sb, "renderGraphMigrationRatio", h.renderGraphMigrationRatio, false);
            sb.Append('}');
        }

        private static void AppendSceneReportJson(StringBuilder sb, ResidentDrawerAuditReport r)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalMeshRenderers", r.totalMeshRenderers, true);
            AppendJsonProp(sb, "eligibleRenderers", r.eligibleRenderers, true);
            AppendJsonProp(sb, "disqualifiedRenderers", r.disqualifiedRenderers, true);
            sb.Append("\"records\":[");
            if (r.records != null)
            {
                for (int i = 0; i < r.records.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RendererEligibilityRecord rec = r.records[i];
                    sb.Append('{');
                    AppendJsonProp(sb, "gameObjectName", rec.gameObjectName, true);
                    AppendJsonProp(sb, "instanceId", rec.instanceId, true);
                    AppendJsonProp(sb, "materialName", rec.materialName, true);
                    AppendJsonProp(sb, "shaderName", rec.shaderName, true);
                    AppendJsonProp(sb, "triangleCount", rec.triangleCount, true);
                    AppendJsonProp(sb, "isEligible", rec.IsEligible, true);
                    AppendJsonProp(sb, "disqualifiers", rec.disqualifiers.ToString(), true);
                    AppendJsonProp(sb, "recommendation", rec.recommendation, false);
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private static string BuildJsonRpcToolCallResult(string idRaw, string structuredJsonPayload)
        {
            var sb = new StringBuilder(structuredJsonPayload.Length + 256);
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":\"")
              .Append(EscapeJson(structuredJsonPayload))
              .Append("\"}],\"structuredContent\":")
              .Append(structuredJsonPayload)
              .Append(",\"isError\":false}");
            return BuildJsonRpcResult(idRaw, sb.ToString());
        }

        private static string BuildJsonRpcResult(string idRaw, string resultJson)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";
        }

        private static string BuildJsonRpcError(string idRaw, int code, string message)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{code},\"message\":\"{EscapeJson(message)}\"}}}}";
        }

        private static void AppendJsonProp(StringBuilder sb, string key, string val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(val ?? string.Empty)).Append('"');
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, int val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val);
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, float val, bool trailingComma)
        {
            float safe = (float.IsNaN(val) || float.IsInfinity(val)) ? 0f : val;
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(safe.ToString("F3", CultureInfo.InvariantCulture));
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, bool val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val ? "true" : "false");
            if (trailingComma) sb.Append(',');
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string ExtractJsonRpcIdRaw(string json)
        {
            var m = Regex.Match(json, @"""id""\s*:\s*(""[^""\\]*(?:\\.[^""\\]*)*""|-?\d+|null)");
            return m.Success ? m.Groups[1].Value : "null";
        }

        internal static string ExtractJsonStringField(string json, string fieldName)
        {
            string pattern = $@"""{Regex.Escape(fieldName)}""\s*:\s*""((?:[^""\\]|\\.)*)""";
            Match m = Regex.Match(json, pattern);
            if (!m.Success) return null;
            return UnescapeJsonString(m.Groups[1].Value);
        }

        private static string UnescapeJsonString(string escaped)
        {
            if (string.IsNullOrEmpty(escaped)) return string.Empty;
            var sb = new StringBuilder(escaped.Length);
            for (int i = 0; i < escaped.Length; i++)
            {
                char c = escaped[i];
                if (c == '\\' && i + 1 < escaped.Length)
                {
                    char next = escaped[++i];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < escaped.Length &&
                                int.TryParse(escaped.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(next); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
