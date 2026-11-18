# Changelog

All notable changes to `com.yusufokuducu.pc-rendergraph-doctor` are documented in this file.

## [1.2.0] - 2026-11-18

### Added
- **Modern UI Toolkit (`CreateGUI`) GPU Resident Drawer & Render Graph Heatmap Dashboard**:
  - Upgraded `RenderGraphDoctorWindow` with `UnityEngine.UIElements` (`VisualElement`, stacked horizontal Eligibility vs Disqualified heatmap bar, disqualifier breakdown metrics, Render Graph pass migration `ProgressBar`, interactive search filter, and tabbed scene/assembly views) while preserving `OnGUI` fallback compatibility.
- **MCP (Model Context Protocol) & AI Agent JSON-RPC 2.0 Bridge (`RenderGraphDoctorMcpBridge`)**:
  - Full JSON-RPC 2.0 server dispatcher (`HandleJsonRpcRequest`) supporting `initialize`, `tools/list`, and `tools/call` for AI coding assistants (Cursor, Claude, Antigravity).
  - Implemented `rendergraph_doctor.audit_scene_renderers`, `rendergraph_doctor.autofix_scene_renderers`, `rendergraph_doctor.audit_shader_brg`, `rendergraph_doctor.audit_renderpass_csharp`, and `rendergraph_doctor.autofix_renderpass_csharp`.
  - Added `AutoFixSceneRenderers` (clears `MaterialPropertyBlock` overrides, enables `sharedMaterial.enableInstancing`, converts `LightProbeUsage.UseProxyVolume` to `BlendProbes`, and clears conflicting `StaticEditorFlags.BatchingStatic` flags) and `AutoFixRenderPassCSharpSource` (upgrades non-static `SetRenderFunc` lambdas in C# source code to `static` lambdas to eliminate per-frame GC closure allocations).
  - Added `RenderGraphDoctorCli.RunMcpAuditJson()` batchmode entrypoint exporting `Logs/RenderGraphDoctorMcpReport.json`.
- **GitHub Actions CI & OpenUPM Metadata**:
  - Added `.github/workflows/ci.yml` running the standalone `.NET 8` NUnit test suite on every push and pull request.
  - Added OpenUPM-compliant metadata (`documentationUrl`, `changelogUrl`, `licensesUrl`) to `package.json`.

## [1.1.0] - 2026-11-18

### Added
- Scoped `RecordRenderGraph` body extraction and C# comment/string/preprocessor sanitization in `RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource` so dual-path passes implementing legacy `Execute()` are not falsely flagged.
- Static vs instance method-group verification for `builder.SetRenderFunc(MethodName)`.

## [1.0.0] - 2026-11-18

### Added
- Initial release of `GpuResidentDrawerAuditor`, `RenderGraphAssemblyAuditor`, `RenderGraphDoctorWindow`, and `RenderGraphDoctorCli`.
