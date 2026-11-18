# Unity PC Render Graph & GPU Resident Drawer Doctor

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Solves the two biggest architectural pain points in **Unity 6 PC development**:

1. **GPU Resident Drawer (`BatchRendererGroup` / Compute Indirect) Silent Fallbacks & Desktop Overdraw Traps**
   - When migrating dense PC scenes to Unity 6's **GPU Resident Drawer**, objects silently drop out of indirect GPU batching whenever a renderer has a `MaterialPropertyBlock`, has `StaticBatching` enabled, uses a material without GPU Instancing, or has negative transform scale.
   - Worse, dense high-poly meshes without an `LODGroup` or Depth Priming cause severe **quad overdraw bottlenecks** on desktop GPUs when merged into large indirect draw calls.
2. **Mandatory Render Graph (`RecordRenderGraph`) Migration**
   - Unity 6 deprecates legacy `ScriptableRenderPass.Execute(ScriptableRenderContext, ref RenderingData)` in favor of the declarative `RecordRenderGraph` API. A single third-party or custom pass missing `RecordRenderGraph` breaks rendering or forces the entire pipeline into Compatibility Mode.

---

## Core Capabilities

- **Scene GPU Resident Drawer Eligibility Auditor (`GpuResidentDrawerAuditor`)**
  - Scans all `MeshRenderer` components in the scene and pinpoints exact `ResidentDrawerDisqualifier` flags (`HasMaterialPropertyBlock`, `InstancingDisabledOnMaterial`, `StaticBatchingFlagConflict`, `NegativeTransformScale`, `HighPolyOverdrawRiskWithoutLOD`).
- **One-Click Scene Auto-Fixer**
  - Automatically clears legacy `MaterialPropertyBlock` instances, enables `Material.enableInstancing`, and removes conflicting `StaticEditorFlags.BatchingStatic` flags so `BatchRendererGroup` can take over.
- **Reflection-Based `ScriptableRenderPass` Assembly Scanner (`RenderGraphAssemblyAuditor`)**
  - Inspects all loaded project & package assemblies to classify every `ScriptableRenderPass` as **Native Render Graph (`RecordRenderGraph`)**, **Dual-Path Compatible**, or **Legacy Compatibility Only**.

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-pc-rendergraph-doctor.git
```

---

## Usage

Open **Window -> Analysis -> PC Render Graph & GPU Resident Drawer Doctor**:
- **Tab 1 (`GPU Resident Drawer (Scene)`)**: Click **Audit Scene for GPU Resident Drawer** to list disqualified objects, or click **Auto-Fix Eligible Material Instancing & Static Flags**.
- **Tab 2 (`Render Graph Passes (Assemblies)`)**: Click **Scan Compiled Assemblies for ScriptableRenderPass** to find passes that haven't migrated to `RecordRenderGraph`.

## License

MIT License — see [LICENSE](LICENSE).
