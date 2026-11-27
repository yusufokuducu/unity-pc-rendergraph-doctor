# PC Render Graph & GPU Resident Drawer Validator Sample

1. Open **Window > Analysis > PC Render Graph & GPU Resident Drawer Doctor** and click **Auto-Fix Pipeline & Scene** to configure URP 17 (`Forward+` / `Deferred+`), SRP Batcher, `InstancedDrawing`, and `BatchRendererGroupStrippingMode.KeepAll`.
2. Attach `ResidentDrawerSceneValidator` to a GameObject in your scene to verify runtime `DOTS_INSTANCING_ON` and `GRDExclusionReason` compliance across all active `MeshRenderer` sub-meshes.
