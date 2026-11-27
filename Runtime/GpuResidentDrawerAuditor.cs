using System;
using System.Collections.Generic;
using System.IO;
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
        ShaderMissingDotsInstancing = 1 << 8,
        BlendProbesWithAnchorUnsupported = 1 << 9,
        CustomVertexStreamsUsed = 1 << 10,
        LodAnimatedCrossFadeConflict = 1 << 11
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
        public string assetOrScenePath;
        public GameObject targetGameObject;
#if UNITY_6000_4_OR_NEWER
        public EntityId entityId;
#else
        public int instanceId;
#endif
        public string materialName;
        public string shaderName;
        public int subMeshCount;
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
        public int probeAnchorViolations;
        public int customVertexStreamViolations;
        public int lodCrossFadeViolations;
        public int shaderIncompatibilityViolations;
        public int desktopOverdrawRiskMeshes;
        public List<RendererEligibilityRecord> records = new List<RendererEligibilityRecord>();

        public string ExportToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Unity 6 GPU Resident Drawer — Scene & Prefab Eligibility Report");
            sb.AppendLine();
            sb.AppendLine($"- **Total MeshRenderers**: {totalMeshRenderers}");
            sb.AppendLine($"- **Eligible**: {eligibleRenderers} | **Disqualified**: {disqualifiedRenderers}");
            sb.AppendLine($"- **MaterialPropertyBlock Breaks**: {propertyBlockViolations} | **Instancing Disabled**: {instancingDisabledViolations}");
            sb.AppendLine($"- **StaticBatching Conflicts**: {staticBatchingConflicts} | **Negative Scale**: {negativeScaleViolations} | **LPPV Used**: {lppvViolations}");
            sb.AppendLine($"- **ProbeAnchor w/ BlendProbes**: {probeAnchorViolations} | **Custom Vertex Streams**: {customVertexStreamViolations} | **Animated LOD CrossFade**: {lodCrossFadeViolations}");
            sb.AppendLine($"- **Shader Missing DOTS_INSTANCING_ON**: {shaderIncompatibilityViolations} | **High-Poly Overdraw Risk**: {desktopOverdrawRiskMeshes}");
            sb.AppendLine();
            sb.AppendLine("| GameObject | SubMeshes / Tris | Material (Shader) | Disqualifiers | Recommendation |");
            sb.AppendLine("|---|---:|---|---|---|");

            for (int i = 0; i < records.Count; i++)
            {
                RendererEligibilityRecord r = records[i];
                sb.Append("| `").Append(r.gameObjectName).Append("` | ")
                  .Append(Math.Max(1, r.subMeshCount)).Append(" / ").Append(r.triangleCount).Append(" | `")
                  .Append(r.materialName).Append("` (`").Append(r.shaderName).Append("`) | `")
                  .Append(r.disqualifiers).Append("` | ")
                  .Append(r.recommendation).AppendLine(" |");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Audits MeshRenderers (across all sub-meshes and Unity 6 GRDExclusionReason rules) and Shader assets/sources
    /// for Unity 6 GPU Resident Drawer (BatchRendererGroup / Compute Indirect) and SRP Batcher eligibility.
    /// </summary>
    public static class GpuResidentDrawerAuditor
    {
        private static readonly Regex ShaderNameRegex = new Regex(@"Shader\s+""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex Target45PlusRegex = new Regex(@"^\s*#pragma\s+target\s+(?:4\.5|4\.6|5\.0)\b", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex DotsInstancingPragmaRegex = new Regex(@"^\s*#pragma\s+multi_compile(?:_local|_vertex|_fragment)?\b[^\r\n]*\bDOTS_INSTANCING_ON\b", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex CBufferPerMaterialRegex = new Regex(@"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)", RegexOptions.Compiled);
        private static readonly Regex DotsInstancedPropRegex = new Regex(@"\bUNITY_DOTS_INSTANCED_PROP\b", RegexOptions.Compiled);

        public static MeshRenderer[] FindAllActiveMeshRenderers()
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<MeshRenderer>();
#else
            return UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
#endif
        }

        public static ResidentDrawerAuditReport AuditSceneRenderers(
            IReadOnlyList<MeshRenderer> renderers,
            Func<GameObject, bool> isStaticBatchingEnabledPredicate = null,
            Func<Shader, bool> isShaderCompatiblePredicate = null,
            string sourceContextPath = "")
        {
            var report = new ResidentDrawerAuditReport();
            if (renderers == null) return report;

            Func<Shader, bool> resolvedShaderCheck = isShaderCompatiblePredicate ?? IsShaderDotsInstancingCompatible;

            for (int i = 0; i < renderers.Count; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null) continue;

                report.totalMeshRenderers++;
                bool hasStaticBatchFlag = isStaticBatchingEnabledPredicate != null &&
                                          mr.gameObject != null &&
                                          isStaticBatchingEnabledPredicate(mr.gameObject);

                RendererEligibilityRecord rec = AnalyzeRendererMultiMaterial(mr, hasStaticBatchFlag, resolvedShaderCheck, sourceContextPath);
                AccumulateRecordIntoReport(report, rec);
            }

            report.records.Sort((a, b) => b.disqualifiers.CompareTo(a.disqualifiers));
            return report;
        }

        public static void AccumulateRecordIntoReport(ResidentDrawerAuditReport report, RendererEligibilityRecord rec)
        {
            if (report == null) return;
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
                if ((rec.disqualifiers & ResidentDrawerDisqualifier.BlendProbesWithAnchorUnsupported) != 0)
                    report.probeAnchorViolations++;
                if ((rec.disqualifiers & ResidentDrawerDisqualifier.CustomVertexStreamsUsed) != 0)
                    report.customVertexStreamViolations++;
                if ((rec.disqualifiers & ResidentDrawerDisqualifier.LodAnimatedCrossFadeConflict) != 0)
                    report.lodCrossFadeViolations++;
                if ((rec.disqualifiers & ResidentDrawerDisqualifier.ShaderMissingDotsInstancing) != 0)
                    report.shaderIncompatibilityViolations++;
                if ((rec.disqualifiers & ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD) != 0)
                    report.desktopOverdrawRiskMeshes++;
            }

            report.records.Add(rec);
        }

        /// <summary>
        /// Checks whether a compiled Shader or ShaderGraph declares the `DOTS_INSTANCING_ON` keyword
        /// in its `shader.keywordSpace`, working accurately across both `.shader` and `.shadergraph` assets.
        /// </summary>
        public static bool IsShaderDotsInstancingCompatible(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                return false;

            // Built-in InternalErrorShader is never BRG-compatible
            if (string.Equals(shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal))
                return false;

            return shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid;
        }

        public static RendererEligibilityRecord AnalyzeRenderer(
            MeshRenderer mr,
            bool hasStaticBatchingFlag = false,
            bool isShaderCompatibleWithResidentDrawer = true)
        {
            return AnalyzeRendererMultiMaterial(
                mr,
                hasStaticBatchingFlag,
                _ => isShaderCompatibleWithResidentDrawer,
                string.Empty);
        }

        public static RendererEligibilityRecord AnalyzeRendererMultiMaterial(
            MeshRenderer mr,
            bool hasStaticBatchingFlag,
            Func<Shader, bool> shaderCompatibilityCheck,
            string sourcePath = "")
        {
            var flags = ResidentDrawerDisqualifier.None;
            var mf = mr.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            int subMeshCount = mesh != null ? Math.Max(1, mesh.subMeshCount) : 0;
            int tris = CalculateTotalTriangles(mesh);

            if (mesh == null)
                flags |= ResidentDrawerDisqualifier.MissingMeshFilterOrMesh;

            if (mr.HasPropertyBlock())
                flags |= ResidentDrawerDisqualifier.HasMaterialPropertyBlock;

            if (mr.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                flags |= ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed;

            if (mr.lightProbeUsage == LightProbeUsage.BlendProbes && mr.probeAnchor != null)
                flags |= ResidentDrawerDisqualifier.BlendProbesWithAnchorUnsupported;

            if (mr.additionalVertexStreams != null || mr.enlightenVertexStream != null)
                flags |= ResidentDrawerDisqualifier.CustomVertexStreamsUsed;

            Material[] sharedMats = mr.sharedMaterials;
            Material primaryMat = (sharedMats != null && sharedMats.Length > 0) ? sharedMats[0] : mr.sharedMaterial;

            if (sharedMats != null && sharedMats.Length > 0)
            {
                for (int m = 0; m < sharedMats.Length; m++)
                {
                    Material mat = sharedMats[m];
                    if (mat == null) continue;
                    if (primaryMat == null) primaryMat = mat;

                    if (!mat.enableInstancing)
                        flags |= ResidentDrawerDisqualifier.InstancingDisabledOnMaterial;

                    if (mat.renderQueue >= 2500)
                        flags |= ResidentDrawerDisqualifier.TransparentOrNonOpaqueQueue;

                    if (shaderCompatibilityCheck != null && mat.shader != null && !shaderCompatibilityCheck(mat.shader))
                        flags |= ResidentDrawerDisqualifier.ShaderMissingDotsInstancing;
                }
            }
            else if (primaryMat != null)
            {
                if (!primaryMat.enableInstancing)
                    flags |= ResidentDrawerDisqualifier.InstancingDisabledOnMaterial;
                if (primaryMat.renderQueue >= 2500)
                    flags |= ResidentDrawerDisqualifier.TransparentOrNonOpaqueQueue;
                if (shaderCompatibilityCheck != null && primaryMat.shader != null && !shaderCompatibilityCheck(primaryMat.shader))
                    flags |= ResidentDrawerDisqualifier.ShaderMissingDotsInstancing;
            }

            Vector3 scale = mr.transform.lossyScale;
            if ((scale.x * scale.y * scale.z) < 0f)
                flags |= ResidentDrawerDisqualifier.NegativeTransformScale;

            if (hasStaticBatchingFlag)
                flags |= ResidentDrawerDisqualifier.StaticBatchingFlagConflict;

            LODGroup parentLod = mr.GetComponentInParent<LODGroup>();
            if (parentLod != null && parentLod.fadeMode == LODFadeMode.CrossFade && parentLod.animateCrossFading)
            {
                bool supportsCrossFadeKw = primaryMat != null && primaryMat.shader != null &&
                                           primaryMat.shader.keywordSpace.FindKeyword("LOD_FADE_CROSSFADE").isValid;
                if (!supportsCrossFadeKw)
                    flags |= ResidentDrawerDisqualifier.LodAnimatedCrossFadeConflict;
            }

            if (tris > 15000 && parentLod == null)
                flags |= ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD;

            var record = new RendererEligibilityRecord
            {
                gameObjectName = mr.gameObject != null ? mr.gameObject.name : "<MeshRenderer>",
                assetOrScenePath = sourcePath ?? string.Empty,
                targetGameObject = mr.gameObject,
#if UNITY_6000_4_OR_NEWER
                entityId = mr.gameObject != null ? mr.gameObject.GetEntityId() : default,
#else
                instanceId = mr.gameObject != null ? mr.gameObject.GetInstanceID() : 0,
#endif
                materialName = primaryMat != null ? primaryMat.name : "<None>",
                shaderName = (primaryMat != null && primaryMat.shader != null) ? primaryMat.shader.name : "<None>",
                subMeshCount = subMeshCount,
                triangleCount = tris,
                disqualifiers = flags,
                recommendation = BuildRecommendation(flags)
            };

            return record;
        }

        private static int CalculateTotalTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int subCount = Math.Max(1, mesh.subMeshCount);
            long totalIndices = 0;
            for (int s = 0; s < subCount; s++)
            {
                totalIndices += mesh.GetIndexCount(s);
            }
            return (int)(totalIndices / 3L);
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

            SanitizeShaderCommentsAndStrings(rawShaderSource, out string uncommentedWithStrings, out string uncommentedNoStrings);
            string clean = StripDisabledPreprocessorBlocks(uncommentedNoStrings);

            Match nameMatch = ShaderNameRegex.Match(uncommentedWithStrings);
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

        private static void SanitizeShaderCommentsAndStrings(
            string rawSource,
            out string uncommentedWithStrings,
            out string uncommentedNoStrings)
        {
            int len = rawSource.Length;
            var sbWithStrings = new StringBuilder(len);
            var sbNoStrings = new StringBuilder(len);
            int i = 0;

            while (i < len)
            {
                char c = rawSource[i];
                if (c == '/' && i + 1 < len && rawSource[i + 1] == '*')
                {
                    sbWithStrings.Append(' ');
                    sbNoStrings.Append(' ');
                    i += 2;
                    while (i < len)
                    {
                        if (rawSource[i] == '*' && i + 1 < len && rawSource[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }
                        if (rawSource[i] == '\n')
                        {
                            sbWithStrings.Append('\n');
                            sbNoStrings.Append('\n');
                        }
                        i++;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < len && rawSource[i + 1] == '/')
                {
                    i += 2;
                    while (i < len && rawSource[i] != '\n' && rawSource[i] != '\r')
                        i++;
                    continue;
                }

                if (c == '"')
                {
                    sbWithStrings.Append('"');
                    sbNoStrings.Append("\"\"");
                    i++;
                    while (i < len)
                    {
                        char sc = rawSource[i++];
                        sbWithStrings.Append(sc);
                        if (sc == '\\' && i < len)
                            sbWithStrings.Append(rawSource[i++]);
                        else if (sc == '"' || sc == '\n' || sc == '\r')
                            break;
                    }
                    continue;
                }

                sbWithStrings.Append(c);
                sbNoStrings.Append(c);
                i++;
            }

            uncommentedWithStrings = sbWithStrings.ToString();
            uncommentedNoStrings = sbNoStrings.ToString();
        }

        private static string StripDisabledPreprocessorBlocks(string source)
        {
            var sb = new StringBuilder(source.Length);
            var stack = new Stack<(bool parentActive, bool branchActive, bool seenActive)>();
            bool currentlyActive = true;

            using (var reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        string directive = trimmed.Substring(1).TrimStart();
                        if (directive.StartsWith("ifdef", StringComparison.Ordinal) ||
                            directive.StartsWith("ifndef", StringComparison.Ordinal))
                        {
                            stack.Push((currentlyActive, currentlyActive, false));
                            sb.AppendLine();
                            continue;
                        }
                        if (directive.StartsWith("if", StringComparison.Ordinal) &&
                            (directive.Length == 2 || char.IsWhiteSpace(directive[2]) || directive[2] == '('))
                        {
                            string expr = directive.Substring(2).Trim().Trim('(', ')').Trim();
                            bool isFalse = string.Equals(expr, "0", StringComparison.Ordinal) ||
                                           string.Equals(expr, "false", StringComparison.OrdinalIgnoreCase);
                            bool isTrue = string.Equals(expr, "1", StringComparison.Ordinal) ||
                                          string.Equals(expr, "true", StringComparison.OrdinalIgnoreCase);
                            bool active = currentlyActive && !isFalse;
                            stack.Push((currentlyActive, active, isTrue && active));
                            currentlyActive = active;
                            sb.AppendLine();
                            continue;
                        }
                        if (directive.StartsWith("else", StringComparison.Ordinal) &&
                            (directive.Length == 4 || char.IsWhiteSpace(directive[4])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                bool active = top.parentActive && !top.seenActive;
                                stack.Push((top.parentActive, active, true));
                                currentlyActive = active;
                            }
                            sb.AppendLine();
                            continue;
                        }
                        if (directive.StartsWith("endif", StringComparison.Ordinal) &&
                            (directive.Length == 5 || char.IsWhiteSpace(directive[5])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                currentlyActive = top.parentActive;
                            }
                            sb.AppendLine();
                            continue;
                        }
                    }

                    if (currentlyActive)
                        sb.AppendLine(line);
                    else
                        sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public static string BuildRecommendation(ResidentDrawerDisqualifier flags)
        {
            if (flags == ResidentDrawerDisqualifier.None)
                return "Eligible for Unity 6 GPU Resident Drawer & SRP Batcher.";

            var parts = new List<string>(8);
            if ((flags & ResidentDrawerDisqualifier.MissingMeshFilterOrMesh) != 0)
                parts.Add("Assign valid Mesh to MeshFilter");
            if ((flags & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0)
                parts.Add("Clear MaterialPropertyBlock (breaks SRP Batcher & GPU Resident Drawer)");
            if ((flags & ResidentDrawerDisqualifier.InstancingDisabledOnMaterial) != 0)
                parts.Add("Enable GPU Instancing on all sub-mesh Materials");
            if ((flags & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0)
                parts.Add("Disable Static Batching flag (conflicts with BatchRendererGroup instancing)");
            if ((flags & ResidentDrawerDisqualifier.NegativeTransformScale) != 0)
                parts.Add("Fix negative transform scale (forces flip-winding batch split)");
            if ((flags & ResidentDrawerDisqualifier.LightProbeProxyVolumeUsed) != 0)
                parts.Add("Switch LightProbeUsage away from UseProxyVolume (LPPV is unsupported by GRD)");
            if ((flags & ResidentDrawerDisqualifier.BlendProbesWithAnchorUnsupported) != 0)
                parts.Add("Clear MeshRenderer.probeAnchor or switch to Adaptive Probe Volumes (BlendProbes+probeAnchor is excluded from GRD)");
            if ((flags & ResidentDrawerDisqualifier.CustomVertexStreamsUsed) != 0)
                parts.Add("Remove additionalVertexStreams / enlightenVertexStream (unsupported by GRD)");
            if ((flags & ResidentDrawerDisqualifier.LodAnimatedCrossFadeConflict) != 0)
                parts.Add("Add #pragma multi_compile _ LOD_FADE_CROSSFADE to shader or disable LODGroup.animateCrossFading");
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
