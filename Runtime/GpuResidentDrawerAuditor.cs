using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace YusufOkuducu.PcRenderGraphDoctor
{
    [Flags]
    public enum ResidentDrawerDisqualifier
    {
        None = 0,
        MissingMeshFilterOrMesh = 1 << 0,
        HasMaterialPropertyBlock = 1 << 1,
        InstancingDisabledOnMaterial = 1 << 2,
        NegativeTransformScale = 1 << 3,
        StaticBatchingFlagConflict = 1 << 4,
        TransparentOrNonOpaqueQueue = 1 << 5,
        HighPolyOverdrawRiskWithoutLOD = 1 << 6,
        LightProbeProxyVolumeUsed = 1 << 7,
        ShaderMissingDotsInstancing = 1 << 8
    }

    [Serializable]
    public struct ShaderResidentDrawerAudit
    {
        public string shaderName;
        public string assetPath;
        public bool hasTarget45OrHigher;
        public bool hasDotsInstancingPragma;
        public bool hasSrpBatcherCBuffer;
        public bool hasDotsInstancedProps;
        public bool isCompatibleWithResidentDrawer;
        public string diagnosticMessage;
    }

    [Serializable]
    public struct RendererEligibilityRecord
    {
        public string gameObjectName;
        public int instanceId;
        public string materialName;
        public string shaderName;
        public int triangleCount;
        public ResidentDrawerDisqualifier disqualifiers;
        public string recommendation;

        public bool IsEligible => disqualifiers == ResidentDrawerDisqualifier.None;
    }

    [Serializable]
    public sealed class ResidentDrawerAuditReport
    {
        public int totalMeshRenderers;
        public int eligibleRenderers;
        public int disqualifiedRenderers;
        public int propertyBlockViolations;
        public int instancingDisabledViolations;
        public int negativeScaleViolations;
        public int staticBatchingConflicts;
        public int lppvViolations;
        public int desktopOverdrawRiskMeshes;
        public List<RendererEligibilityRecord> records = new List<RendererEligibilityRecord>();

        public string ExportToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Unity 6 GPU Resident Drawer — Scene Eligibility Report");
            sb.AppendLine();
            sb.AppendLine($"- **Total MeshRenderers**: {totalMeshRenderers}");
            sb.AppendLine($"- **Eligible**: {eligibleRenderers} | **Disqualified**: {disqualifiedRenderers}");
            sb.AppendLine($"- **MaterialPropertyBlock Breaks**: {propertyBlockViolations} | **Instancing Disabled**: {instancingDisabledViolations}");
            sb.AppendLine($"- **StaticBatching Conflicts**: {staticBatchingConflicts} | **Negative Scale**: {negativeScaleViolations} | **LPPV Used**: {lppvViolations}");
            sb.AppendLine();
            sb.AppendLine("| GameObject | Triangles | Material (Shader) | Disqualifiers | Recommendation |");
            sb.AppendLine("|---|---:|---|---|---|");

            for (int i = 0; i < records.Count; i++)
            {
                RendererEligibilityRecord r = records[i];
                sb.Append("| `").Append(r.gameObjectName).Append("` | ")
                  .Append(r.triangleCount).Append(" | `")
                  .Append(r.materialName).Append("` (`").Append(r.shaderName).Append("`) | `")
                  .Append(r.disqualifiers).Append("` | ")
                  .Append(r.recommendation).AppendLine(" |");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Audits MeshRenderers and Shader source code in PC scenes for Unity 6 GPU Resident Drawer
    /// (BatchRendererGroup / Compute Indirect) and SRP Batcher eligibility, detecting hidden disqualifiers
    /// and desktop GPU quad overdraw traps.
    /// </summary>
    public static class GpuResidentDrawerAuditor
    {
        private static readonly Regex ShaderNameRegex = new Regex(@"Shader\s+""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex Target45PlusRegex = new Regex(@"^\s*#pragma\s+target\s+(?:4\.5|4\.6|5\.0)\b", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex DotsInstancingPragmaRegex = new Regex(@"^\s*#pragma\s+multi_compile(?:_local)?\b[^\r\n]*\bDOTS_INSTANCING_ON\b", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex CBufferPerMaterialRegex = new Regex(@"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)", RegexOptions.Compiled);
        private static readonly Regex DotsInstancedPropRegex = new Regex(@"\bUNITY_DOTS_INSTANCED_PROP\b", RegexOptions.Compiled);
        private static readonly Regex BlockCommentRegex = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
        private static readonly Regex LineCommentRegex = new Regex(@"//.*?$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static ResidentDrawerAuditReport AuditSceneRenderers(
            IReadOnlyList<MeshRenderer> renderers,
            Func<GameObject, bool> isStaticBatchingEnabledPredicate = null)
        {
            var report = new ResidentDrawerAuditReport();
            if (renderers == null) return report;

            for (int i = 0; i < renderers.Count; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null) continue;

                report.totalMeshRenderers++;
                bool hasStaticBatchFlag = isStaticBatchingEnabledPredicate != null &&
                                          isStaticBatchingEnabledPredicate(mr.gameObject);

                RendererEligibilityRecord rec = AnalyzeRenderer(mr, hasStaticBatchFlag);
                if (rec.IsEligible)
                {
                    report.eligibleRenderers++;
                }
                else
                {
                    report.disqualifiedRenderers++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0)
                        report.propertyBlockViolations++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.InstancingDisabledOnMaterial) != 0)
                        report.instancingDisabledViolations++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.NegativeTransformScale) != 0)
                        report.negativeScaleViolations++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0)
                        report.staticBatchingConflicts++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed) != 0)
                        report.lppvViolations++;
                    if ((rec.disqualifiers & ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD) != 0)
                        report.desktopOverdrawRiskMeshes++;
                }

                report.records.Add(rec);
            }

            report.records.Sort((a, b) => b.disqualifiers.CompareTo(a.disqualifiers));
            return report;
        }

        public static RendererEligibilityRecord AnalyzeRenderer(MeshRenderer mr, bool hasStaticBatchingFlag = false)
        {
            var flags = ResidentDrawerDisqualifier.None;
            var mf = mr.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            int tris = mesh != null ? (int)(mesh.GetIndexCount(0) / 3) : 0;

            if (mesh == null)
                flags |= ResidentDrawerDisqualifier.MissingMeshFilterOrMesh;

            if (mr.HasPropertyBlock())
                flags |= ResidentDrawerDisqualifier.HasMaterialPropertyBlock;

            if (mr.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                flags |= ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed;

            Material mat = mr.sharedMaterial;
            if (mat != null)
            {
                if (!mat.enableInstancing)
                    flags |= ResidentDrawerDisqualifier.InstancingDisabledOnMaterial;

                if (mat.renderQueue >= 2500)
                    flags |= ResidentDrawerDisqualifier.TransparentOrNonOpaqueQueue;
            }

            Vector3 scale = mr.transform.lossyScale;
            if ((scale.x * scale.y * scale.z) < 0f)
                flags |= ResidentDrawerDisqualifier.NegativeTransformScale;

            if (hasStaticBatchingFlag)
                flags |= ResidentDrawerDisqualifier.StaticBatchingFlagConflict;

            if (tris > 15000 && mr.GetComponentInParent<LODGroup>() == null)
                flags |= ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD;

            return new RendererEligibilityRecord
            {
                gameObjectName = mr.gameObject.name,
                instanceId = mr.gameObject.GetInstanceID(),
                materialName = mat != null ? mat.name : "<None>",
                shaderName = (mat != null && mat.shader != null) ? mat.shader.name : "<None>",
                triangleCount = tris,
                disqualifiers = flags,
                recommendation = BuildRecommendation(flags)
            };
        }

        /// <summary>
        /// Inspects ShaderLab/HLSL source code to verify whether a custom shader meets Unity 6
        /// GPU Resident Drawer (BatchRendererGroup) requirements: #pragma target 4.5,
        /// #pragma multi_compile _ DOTS_INSTANCING_ON, and CBUFFER_START(UnityPerMaterial).
        /// </summary>
        public static ShaderResidentDrawerAudit AuditShaderSourceForResidentDrawer(string rawShaderSource, string assetPath = "<Memory>")
        {
            var audit = new ShaderResidentDrawerAudit
            {
                assetPath = assetPath ?? "<Memory>",
                shaderName = "<Unknown>"
            };

            if (string.IsNullOrWhiteSpace(rawShaderSource))
            {
                audit.diagnosticMessage = "Empty shader source.";
                return audit;
            }

            string clean = BlockCommentRegex.Replace(rawShaderSource, " ");
            clean = LineCommentRegex.Replace(clean, string.Empty);

            Match nameMatch = ShaderNameRegex.Match(clean);
            if (nameMatch.Success)
                audit.shaderName = nameMatch.Groups[1].Value.Trim();

            audit.hasTarget45OrHigher = Target45PlusRegex.IsMatch(clean);
            audit.hasDotsInstancingPragma = DotsInstancingPragmaRegex.IsMatch(clean);
            audit.hasSrpBatcherCBuffer = CBufferPerMaterialRegex.IsMatch(clean);
            audit.hasDotsInstancedProps = DotsInstancedPropRegex.IsMatch(clean);

            audit.isCompatibleWithResidentDrawer =
                audit.hasTarget45OrHigher &&
                audit.hasDotsInstancingPragma &&
                audit.hasSrpBatcherCBuffer;

            if (audit.isCompatibleWithResidentDrawer)
            {
                audit.diagnosticMessage = "Shader supports Unity 6 GPU Resident Drawer (target 4.5+, DOTS_INSTANCING_ON, UnityPerMaterial CBUFFER).";
            }
            else
            {
                var missing = new List<string>(3);
                if (!audit.hasTarget45OrHigher) missing.Add("#pragma target 4.5");
                if (!audit.hasDotsInstancingPragma) missing.Add("#pragma multi_compile _ DOTS_INSTANCING_ON");
                if (!audit.hasSrpBatcherCBuffer) missing.Add("CBUFFER_START(UnityPerMaterial)");
                audit.diagnosticMessage = "Missing GPU Resident Drawer requirements: " + string.Join(", ", missing);
            }

            return audit;
        }

        public static string BuildRecommendation(ResidentDrawerDisqualifier flags)
        {
            if (flags == ResidentDrawerDisqualifier.None)
                return "Eligible for Unity 6 GPU Resident Drawer & SRP Batcher.";

            var parts = new List<string>(6);
            if ((flags & ResidentDrawerDisqualifier.MissingMeshFilterOrMesh) != 0)
                parts.Add("Assign valid Mesh to MeshFilter");
            if ((flags & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0)
                parts.Add("Clear MaterialPropertyBlock (breaks SRP Batcher & GPU Resident Drawer)");
            if ((flags & ResidentDrawerDisqualifier.InstancingDisabledOnMaterial) != 0)
                parts.Add("Enable GPU Instancing on Material");
            if ((flags & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0)
                parts.Add("Disable Static Batching flag (conflicts with BatchRendererGroup instancing)");
            if ((flags & ResidentDrawerDisqualifier.NegativeTransformScale) != 0)
                parts.Add("Fix negative transform scale (causes winding-order batch splits)");
            if ((flags & ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed) != 0)
                parts.Add("Switch LightProbeUsage away from UseProxyVolume (LPPV is unsupported by GPU Resident Drawer)");
            if ((flags & ResidentDrawerDisqualifier.TransparentOrNonOpaqueQueue) != 0)
                parts.Add("Use Opaque/AlphaTest queue for Resident Drawer indirect culling");
            if ((flags & ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD) != 0)
                parts.Add("Add LODGroup or enable Depth Priming (dense mesh risks PC GPU quad overdraw)");
            if ((flags & ResidentDrawerDisqualifier.ShaderMissingDotsInstancing) != 0)
                parts.Add("Add #pragma target 4.5 and #pragma multi_compile _ DOTS_INSTANCING_ON to Shader");

            return string.Join(" | ", parts);
        }
    }
}
