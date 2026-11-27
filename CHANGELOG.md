# Changelog

All notable changes to `com.yusufokuducu.pc-rendergraph-doctor` are documented in this file.

## [2.0.0] - 2026-11-26

### Added
- **URP 17 Pipeline Prerequisite Doctor & Prefab Scanner (`UrpPipelinePrerequisiteDoctor`)**:
  - Verifies URP 17 `Forward+` (`ForwardPlus`) / `Deferred+` (`DeferredPlus`) rendering mode (flagging unsupported standard `Forward`/`Deferred`), SRP Batcher, `InstancedDrawing` GRD mode, and `EditorGraphicsSettings.batchRendererGroupShaderStrippingMode == KeepAll`, with Undo-safe one-click auto-configuration and full project `Assets/` Prefab scanning.
- **Full Unity 6 `GRDExclusionReason` Parity & Multi-Submesh `DOTS_INSTANCING_ON` Verification (`GpuResidentDrawerAuditor`)**:
  - Automatically checks `shader.keywordSpace.FindKeyword("DOTS_INSTANCING_ON").isValid` across all `sharedMaterials` (`.shader` & `.shadergraph`), plus `BlendProbesWithAnchorUnsupported`, `CustomVertexStreamsUsed`, and `LodAnimatedCrossFadeConflict`.
- **8-Item Official URP Render Graph C# Static Analyzer & Capture-Safe Auto-Fixer (`RenderGraphAssemblyAuditor` & `RenderGraphDoctorMcpBridge`)**:
  - Detects unused/unbound `Material` fields, simultaneous `UseTexture`+`SetRenderAttachment` read/write hazards, pooled `PassData` stale `TextureHandle` leaks, manual `new TextureDesc`/`cameraTargetDescriptor` misuse, copy/fullscreen-blit simplification opportunities, unsafe `cmd.SetGlobalTexture`/`UseAllGlobalTextures(true)`, and missing `ConfigureInput(ScriptableRenderPassInput.*)`.
  - Capture-safe `SetRenderFunc` static auto-fixer automatically hoists captured outer fields into `PassData` (`passData.field = field;` + `data.field`) and refuses `CS8820`-breaking `this.` mutations, outputting unified Before/After diffs.
- **Dual-Channel Live MCP Server, Virtualized UI Toolkit & Unity 6000.6 `.meta` Support**:
  - `[CliCommand]` bindings (`RenderGraphDoctorPipelineCommands`) + loopback `127.0.0.1` HTTP server (`RenderGraphDoctorMcpLiveServer`), 20,000+ row `MultiColumnListView`, Light/Dark `RenderGraphDoctorTheme.uss`, `Painter2D` `ResidentDrawerEligibilityChartElement`, `-failOnCritical` CI gate, `Samples~/RenderGraphDoctorDemo`, and complete `.meta` files.

### Fixed
- Fixed Unity `6000.6.0f1` `CS0619` hard errors on `Object.GetInstanceID()` / `EditorUtility.InstanceIDToObject(int)` by migrating to `GetEntityId()` / `EntityIdToObject(EntityId)` under `UNITY_6000_4_OR_NEWER`.
- Filtered internal `Unity.*` / `UnityEditor.*` assemblies out of `AuditAllLoadedRenderPasses` so URP's internal passes no longer pollute user migration ratios.
- Prevented built-in `Default-Material` / read-only `Packages/` material mutation during `AutoFixSceneRenderers` and EditMode tests.

## [1.2.0] - 2026-11-18
- Added UI Toolkit eligibility heatmaps, MCP JSON-RPC 2.0 auto-fix bridge, and GitHub Actions CI.

## [1.0.0] - 2026-11-18
- Initial release.
