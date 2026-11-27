using UnityEngine;

namespace YusufOkuducu.PcRenderGraphDoctor.Samples
{
    /// <summary>
    /// Sample runtime scene validator logging Unity 6 GPU Resident Drawer (BatchRendererGroup)
    /// eligibility across all active MeshRenderers and sub-meshes on startup.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ResidentDrawerSceneValidator : MonoBehaviour
    {
        [SerializeField] private bool logDisqualifiedDetails = true;

        private void Start()
        {
            MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
            ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(renderers);

            Debug.Log(
                $"[RenderGraphDoctorDemo] GPU Resident Drawer Scene Check: " +
                $"{report.eligibleRenderers}/{report.totalMeshRenderers} MeshRenderers eligible " +
                $"({report.disqualifiedRenderers} disqualified).");

            if (logDisqualifiedDetails && report.disqualifiedRenderers > 0)
            {
                for (int i = 0; i < report.records.Count; i++)
                {
                    RendererEligibilityRecord rec = report.records[i];
                    if (!rec.IsEligible)
                    {
                        Debug.LogWarning($"[RenderGraphDoctorDemo] '{rec.gameObjectName}' excluded [{rec.disqualifiers}]: {rec.recommendation}");
                    }
                }
            }
        }
    }
}
