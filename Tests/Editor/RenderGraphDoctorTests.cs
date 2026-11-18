using NUnit.Framework;
using UnityEngine;

namespace YusufOkuducu.PcRenderGraphDoctor.Tests
{
    public sealed class RenderGraphDoctorTests
    {
        [Test]
        public void ResidentDrawerAuditor_FlagsPropertyBlockAndNegativeScaleDisqualifiers()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                go.transform.localScale = new Vector3(-1f, 1f, 1f);
                var mr = go.GetComponent<MeshRenderer>();

                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_BaseColor", Color.red);
                mr.SetPropertyBlock(mpb);

                RendererEligibilityRecord rec = GpuResidentDrawerAuditor.AnalyzeRenderer(mr, hasStaticBatchingFlag: true);

                Assert.IsFalse(rec.IsEligible);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.HasMaterialPropertyBlock) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.NegativeTransformScale) != 0);
                Assert.IsTrue((rec.disqualifiers & ResidentDrawerDisqualifier.StaticBatchingFlagConflict) != 0);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
