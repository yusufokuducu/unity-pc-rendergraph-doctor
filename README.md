# Unity PC Render Graph & GPU Resident Drawer Doctor

[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Solves the two biggest architectural pain points in **Unity 6 PC development**:

1. **GPU Resident Drawer (`BatchRendererGroup` / Compute Indirect) Silent Fallbacks & Desktop Overdraw Traps**
   - When migrating dense PC scenes to Unity 6's **GPU Resident Drawer**, objects silently drop out of indirect GPU batching whenever a renderer has a `MaterialPropertyBlock`, has `StaticBatching` enabled, uses `LightProbeUsage.UseProxyVolume` (LPPV), uses a material without GPU Instancing, has negative transform scale, or uses a custom shader lacking `#pragma target 4.5` and `#pragma multi_compile _ DOTS_INSTANCING_ON`.
   - Worse, dense high-poly meshes without an `LODGroup` or Depth Priming cause severe **quad overdraw bottlenecks** on desktop GPUs when merged into large indirect draw calls.
2. **Mandatory Render Graph (`RecordRenderGraph`) Migration & Per-Frame GC Closure Hazards**
   - Unity 6 deprecates legacy `ScriptableRenderPass.Execute(ScriptableRenderContext, ref RenderingData)` in favor of the declarative `RecordRenderGraph` API.
   - Even in migrated passes, non-static lambdas passed to `builder.SetRenderFunc(...)` allocate GC closures every frame, and overusing `AddUnsafePass` or `RenderTexture.GetTemporary` prevents Render Graph pass coalescing and texture aliasing.

---

## Core Capabilities

- **Scene GPU Resident Drawer Eligibility Auditor (`GpuResidentDrawerAuditor.AuditSceneRenderers`)**
  - Scans all `MeshRenderer` components in the scene and pinpoints exact `ResidentDrawerDisqualifier` flags (`HasMaterialPropertyBlock`, `InstancingDisabledOnMaterial`, `StaticBatchingFlagConflict`, `NegativeTransformScale`, `LightProbeProxyVolumeUsed`, `HighPolyOverdrawRiskWithoutLOD`).
- **Shader DOTS Instancing & BRG Source Auditor (`GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer`)**
  - Inspects custom `.shader` source files to verify `#pragma target 4.5`, `#pragma multi_compile _ DOTS_INSTANCING_ON`, `CBUFFER_START(UnityPerMaterial)`, and `UNITY_DOTS_INSTANCED_PROP` compliance.
- **One-Click Scene Auto-Fixer**
  - Automatically clears legacy `MaterialPropertyBlock` instances, enables `Material.enableInstancing`, and removes conflicting `StaticEditorFlags.BatchingStatic` flags so `BatchRendererGroup` can take over.
- **Reflection-Based `ScriptableRenderPass` Assembly Scanner & C# Source Static Analyzer (`RenderGraphAssemblyAuditor`)**
  - Inspects all loaded project & package assemblies to classify every `ScriptableRenderPass` as **Native Render Graph (`RecordRenderGraph`)**, **Dual-Path Compatible**, or **Legacy Compatibility Only**.
  - Statically audits C# pass source code (`AnalyzeRenderPassCSharpSource`) for non-static `SetRenderFunc` closure allocations, `AddUnsafePass` overhead, `CommandBufferPool.Get`, and `RenderTexture.GetTemporary` usage inside `RecordRenderGraph`.

- **Headless CI / Batchmode Support (`RenderGraphDoctorCli`)**
  ```bash
  unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunCiAudit
  ```

---

## Installation (Unity Package Manager)

Add via Git URL in **Window -> Package Manager -> + -> Add package from git URL...**:

```text
https://github.com/yusufokuducu/unity-pc-rendergraph-doctor.git
```

---

## Running Standalone Unit Tests (.NET CLI)

```bash
dotnet test DotNetTests~/PcRenderGraphDoctor.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).
