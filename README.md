# Unity PC Render Graph & GPU Resident Drawer Doctor (`v2.0.0`)

[![CI](https://github.com/yusufokuducu/unity-pc-rendergraph-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/yusufokuducu/unity-pc-rendergraph-doctor/actions/workflows/ci.yml)
[![Unity 2022.3+ | Unity 6](https://img.shields.io/badge/Unity-2022.3%2B%20%7C%20Unity%206000.6-black?logo=unity)](https://unity.com)
[![Version](https://img.shields.io/badge/version-2.0.0-brightgreen.svg)](CHANGELOG.md)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Audits and auto-fixes **Unity 6 GPU Resident Drawer (`BatchRendererGroup`)** pipeline/scene/prefab eligibility (`URP 17 Forward+ / Deferred+`, `KeepAll` BRG shader stripping, multi-submesh `DOTS_INSTANCING_ON`, and full `GRDExclusionReason` rules) and **URP Render Graph C# `ScriptableRenderPass` implementations** across **Unity's 8-item official Render Graph checklist**, featuring a **Capture-Safe `PassData` field-hoisting auto-fixer**, **20,000+ row virtualized UI Toolkit + `Painter2D` dashboard**, and **dual-channel Live MCP Server**.

---

## What's New in v2.0.0

- **URP 17 Pipeline Prerequisite Doctor (`UrpPipelinePrerequisiteDoctor`)**:
  - Verifies `Forward+` (`ForwardPlus`) or `Deferred+` (`DeferredPlus`) rendering mode (catching standard `Forward`/`Deferred` where URP 17 disables GPU Resident Drawer), SRP Batcher, `InstancedDrawing`, and `BatchRendererGroupStrippingMode.KeepAll`, with one-click Undo-safe setup.
- **Full `GRDExclusionReason` & Multi-Submesh Prefab/Scene Auditor (`GpuResidentDrawerAuditor`)**:
  - Inspects `shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid` across all `sharedMaterials` (`.shader` and `.shadergraph`), `BlendProbesWithAnchorUnsupported`, `CustomVertexStreamsUsed`, `LodAnimatedCrossFadeConflict`, `MaterialPropertyBlock`, `LPPV`, and `BatchingStatic`.
- **8-Item Official URP Render Graph Static Analyzer & Capture-Safe Auto-Fixer**:
  - Catches pooled `PassData` stale `TextureHandle` leaks, simultaneous read/write wiring hazards, `new TextureDesc` / `cameraTargetDescriptor` misuse, unused `Material` fields, fullscreen copy/blit simplification opportunities, unsafe `cmd.SetGlobalTexture` / `UseAllGlobalTextures(true)`, and missing `ConfigureInput(ScriptableRenderPassInput.*)`.
  - Automatically hoists captured outer fields into `PassData` before marking `SetRenderFunc` lambdas `static` (preventing `CS8820` compile errors) and generates unified Before/After diffs.
- **Dual-Channel Live MCP Server (`unity mcp` `[CliCommand]` + Secure `127.0.0.1` HTTP)** and **Virtualized `MultiColumnListView` + `Painter2D` Charts**.

---

## Installation (UPM & OpenUPM)

### Via Git URL
```text
https://github.com/yusufokuducu/unity-pc-rendergraph-doctor.git
```

### Via OpenUPM
```bash
openupm add com.yusufokuducu.pc-rendergraph-doctor
```

---

## Headless CI Quality Gates & Standalone Tests

```bash
unity -batchmode -quit -projectPath . -executeMethod YusufOkuducu.PcRenderGraphDoctor.Editor.RenderGraphDoctorCli.RunCiAudit -failOnCritical
dotnet test DotNetTests~/PcRenderGraphDoctor.Tests.csproj
```

## License

MIT License — see [LICENSE](LICENSE).
