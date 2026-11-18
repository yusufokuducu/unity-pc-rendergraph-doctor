# Unity PC Render Graph & GPU Resident Drawer Doctor

[![CI](https://github.com/yusufokuducu/unity-pc-rendergraph-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/yusufokuducu/unity-pc-rendergraph-doctor/actions/workflows/ci.yml)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206-black?logo=unity)](https://unity.com)
[![Version](https://img.shields.io/badge/version-1.2.0-brightgreen.svg)](CHANGELOG.md)
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
  - Scans all `MeshRenderer` components in the scene and pinpoints exact `ResidentDrawerDisqualifier` flags (`HasMaterialPropertyBlock`, `InstancingDisabledOnMaterial`, `StaticBatchingFlagConflict`, `NegativeTransformScale`, `LightProbeProxyVolumeUsed`, `HighPolyOverdrawRiskWithoutLOD`, `ShaderMissingDotsInstancing`).
- **Shader DOTS Instancing & BRG Source Auditor (`GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer`)**
  - Inspects custom `.shader` source files to verify `#pragma target 4.5`, `#pragma multi_compile _ DOTS_INSTANCING_ON`, `CBUFFER_START(UnityPerMaterial)`, and `UNITY_DOTS_INSTANCED_PROP` compliance.
- **Modern UI Toolkit (`CreateGUI`) Eligibility & Render Graph Migration Heatmap (`RenderGraphDoctorWindow`)**
  - Stacked horizontal Eligibility vs Disqualified heatmap bar, disqualifier breakdown metrics, Render Graph assembly migration gauge (`ProgressBar`), real-time search filter (`TextField`), and one-click scene auto-fixing.
- **MCP (Model Context Protocol) & AI Agent Auto-Fix Bridge (`RenderGraphDoctorMcpBridge`)**
  - Built-in JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) compatible with **Cursor**, **Claude Desktop**, **Antigravity**, and Unity MCP servers (`protocolVersion: "2024-11-05"`):
    - `rendergraph_doctor.audit_scene_renderers`: Audits scene `MeshRenderer` eligibility and loaded `ScriptableRenderPass` assemblies.
    - `rendergraph_doctor.autofix_scene_renderers`: Automatically clears `MaterialPropertyBlock` instances, enables `Material.enableInstancing`, converts `LightProbeUsage.UseProxyVolume` to `BlendProbes`, and removes conflicting `StaticEditorFlags.BatchingStatic` flags.
    - `rendergraph_doctor.audit_shader_brg`: Audits ShaderLab/HLSL source for `#pragma target 4.5` and `DOTS_INSTANCING_ON`.
    - `rendergraph_doctor.audit_renderpass_csharp`: Audits C# `ScriptableRenderPass` source code for `RecordRenderGraph` compliance and GC closure risks.
    - `rendergraph_doctor.autofix_renderpass_csharp`: Automatically upgrades non-static `SetRenderFunc` lambdas in C# source code to `static` lambdas to eliminate per-frame GC closure allocations.
- **Headless CI / Batchmode Support (`RenderGraphDoctorCli`)**
  ```bash
  unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunCiAudit
  unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunMcpAuditJson
  ```

---

## Installation (Unity Package Manager & OpenUPM)

### Via Git URL
Add in **Window -> Package Manager -> + -> Add package from git URL...**:
```text
https://github.com/yusufokuducu/unity-pc-rendergraph-doctor.git
```

### Via OpenUPM CLI
```bash
openupm add com.yusufokuducu.pc-rendergraph-doctor
```

---

## Running Standalone Unit Tests (.NET CLI)

```bash
dotnet test DotNetTests~/PcRenderGraphDoctor.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).
