using System;
using System.Collections.Generic;
using UnityEngine;

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
        HighPolyOverdrawRiskWithoutLOD = 1 << 6
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
        public int desktopOverdrawRiskMeshes;
        public List<RendererEligibilityRecord> records = new List<RendererEligibilityRecord>();
    }

    /// <summary>
    /// Audits MeshRenderers in PC scenes for Unity 6 GPU Resident Drawer (BatchRendererGroup)
    /// and SRP Batcher eligibility, detecting hidden disqualifiers and desktop GPU overdraw traps.
    /// </summary>
    public static class GpuResidentDrawerAuditor
    {
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

        private static string BuildRecommendation(ResidentDrawerDisqualifier flags)
        {
            if (flags == ResidentDrawerDisqualifier.None)
                return "Eligible for Unity 6 GPU Resident Drawer & SRP Batcher.";

            var parts = new List<string>(4);
            if ((flags & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0)
                parts.Add("Clear MaterialPropertyBlock (breaks SRP Batcher & GPU Resident Drawer)");
            if ((flags & ResidentDrawerDisqualifier.InstancingDisabledOnMaterial) != 0)
                parts.Add("Enable GPU Instancing on Material");
            if ((flags & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0)
                parts.Add("Disable Static Batching flag (conflicts with BatchRendererGroup instancing)");
            if ((flags & ResidentDrawerDisqualifier.NegativeTransformScale) != 0)
                parts.Add("Fix negative transform scale (causes winding-order batch splits)");
            if ((flags & ResidentDrawerDisqualifier.HighPolyOverdrawRiskWithoutLOD) != 0)
                parts.Add("Add LODGroup or enable Depth Priming (dense mesh risks PC GPU quad overdraw)");

            return string.Join(" | ", parts);
        }
    }
}
