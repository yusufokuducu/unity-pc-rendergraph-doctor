using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    public sealed class RenderGraphDoctorWindow : EditorWindow
    {
        private ResidentDrawerAuditReport _sceneReport;
        private List<RenderPassMigrationFinding> _passFindings;
        private Vector2 _scrollScene;
        private Vector2 _scrollPasses;
        private int _selectedTab;
        private string _searchFilter = string.Empty;
        private static readonly string[] Tabs = { "GPU Resident Drawer (Scene)", "Render Graph Passes (Assemblies)" };

        // UI Toolkit containers
        private VisualElement _heatmapContainer;
        private ScrollView _contentScroll;

        public ResidentDrawerAuditReport SceneReport => _sceneReport;
        public IReadOnlyList<RenderPassMigrationFinding> PassFindings => _passFindings;
        public int SelectedTab => _selectedTab;
        public string SearchFilter => _searchFilter;

        [MenuItem("Window/Analysis/PC Render Graph & GPU Resident Drawer Doctor")]
        public static void ShowWindow()
        {
            var win = GetWindow<RenderGraphDoctorWindow>("PC RenderGraph Doctor");
            win.minSize = new Vector2(720f, 520f);
            win.Show();
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            if (root == null)
                return;

            root.Clear();
            root.style.paddingTop = 8f;
            root.style.paddingBottom = 8f;
            root.style.paddingLeft = 10f;
            root.style.paddingRight = 10f;

            var titleLabel = new Label("PC Unity 6 Render Graph & GPU Resident Drawer Doctor")
            {
                name = "header-title"
            };
            titleLabel.style.fontSize = 15f;
            titleLabel.style.marginBottom = 4f;
            root.Add(titleLabel);

            var infoBox = new HelpBox(
                "Audits scene MeshRenderers and custom shaders for GPU Resident Drawer (BatchRendererGroup) disqualifiers and inspects compiled ScriptableRenderPass classes for Unity 6 RecordRenderGraph compliance.",
                HelpBoxMessageType.Info);
            infoBox.style.marginBottom = 8f;
            root.Add(infoBox);

            // Tab Switcher Row
            var tabRow = new VisualElement { name = "tab-row" };
            tabRow.style.flexDirection = FlexDirection.Row;
            tabRow.style.marginBottom = 8f;

            var tabSceneBtn = new Button(() =>
            {
                _selectedTab = 0;
                RefreshUiToolkitView();
            })
            {
                name = "btn-tab-scene",
                text = Tabs[0]
            };
            tabSceneBtn.style.height = 26f;
            tabSceneBtn.style.flexGrow = 1f;
            tabSceneBtn.style.marginRight = 4f;

            var tabPassBtn = new Button(() =>
            {
                _selectedTab = 1;
                RefreshUiToolkitView();
            })
            {
                name = "btn-tab-passes",
                text = Tabs[1]
            };
            tabPassBtn.style.height = 26f;
            tabPassBtn.style.flexGrow = 1f;

            tabRow.Add(tabSceneBtn);
            tabRow.Add(tabPassBtn);
            root.Add(tabRow);

            // Action Toolbar Row
            var actionRow = new VisualElement { name = "action-toolbar" };
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.marginBottom = 8f;

            var auditSceneBtn = new Button(() =>
            {
                RunSceneAudit();
                RefreshUiToolkitView();
            })
            {
                name = "btn-audit-scene",
                text = "Audit Scene Renderers"
            };
            auditSceneBtn.style.height = 28f;
            auditSceneBtn.style.flexGrow = 1f;
            auditSceneBtn.style.marginRight = 4f;

            var autoFixBtn = new Button(() =>
            {
                AutoFixSceneRenderers();
                RunSceneAudit();
                RefreshUiToolkitView();
            })
            {
                name = "btn-autofix-scene",
                text = "Auto-Fix Disqualified Renderers"
            };
            autoFixBtn.style.height = 28f;
            autoFixBtn.style.flexGrow = 1f;
            autoFixBtn.style.marginRight = 4f;

            var scanAsmBtn = new Button(() =>
            {
                _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                RefreshUiToolkitView();
            })
            {
                name = "btn-scan-passes",
                text = "Scan ScriptableRenderPass Assemblies"
            };
            scanAsmBtn.style.height = 28f;
            scanAsmBtn.style.flexGrow = 1f;
            scanAsmBtn.style.marginRight = 4f;

            var exportBtn = new Button(ExportReports)
            {
                name = "btn-export",
                text = "Export MD / MCP JSON"
            };
            exportBtn.style.height = 28f;
            exportBtn.style.flexGrow = 1f;

            actionRow.Add(auditSceneBtn);
            actionRow.Add(autoFixBtn);
            actionRow.Add(scanAsmBtn);
            actionRow.Add(exportBtn);
            root.Add(actionRow);

            // Search Field
            var searchField = new TextField("Search GameObject / Material / RenderPass")
            {
                name = "search-field",
                value = _searchFilter
            };
            searchField.style.marginBottom = 8f;
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? string.Empty;
                RefreshUiToolkitView();
            });
            root.Add(searchField);

            _heatmapContainer = new VisualElement { name = "heatmap-container" };
            _heatmapContainer.style.marginBottom = 8f;
            root.Add(_heatmapContainer);

            _contentScroll = new ScrollView { name = "content-scroll" };
            _contentScroll.style.flexGrow = 1f;
            root.Add(_contentScroll);

            RefreshUiToolkitView();
        }

        public void SetCustomAuditData(
            ResidentDrawerAuditReport sceneReport,
            List<RenderPassMigrationFinding> passFindings = null)
        {
            _sceneReport = sceneReport;
            if (passFindings != null)
                _passFindings = passFindings;
            RefreshUiToolkitView();
        }

        public void SetSelectedTab(int tabIndex)
        {
            _selectedTab = tabIndex == 1 ? 1 : 0;
            RefreshUiToolkitView();
        }

        public void SetSearchFilter(string query)
        {
            _searchFilter = query ?? string.Empty;
            RefreshUiToolkitView();
        }

        public List<RendererEligibilityRecord> GetFilteredSceneRecords(bool includeEligible = false)
        {
            var list = new List<RendererEligibilityRecord>();
            if (_sceneReport == null || _sceneReport.records == null)
                return list;

            string q = (_searchFilter ?? string.Empty).Trim();
            for (int i = 0; i < _sceneReport.records.Count; i++)
            {
                RendererEligibilityRecord r = _sceneReport.records[i];
                if (!includeEligible && r.IsEligible)
                    continue;

                if (!string.IsNullOrEmpty(q))
                {
                    bool match =
                        (!string.IsNullOrEmpty(r.gameObjectName) && r.gameObjectName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.materialName) && r.materialName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.shaderName) && r.shaderName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        r.disqualifiers.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                        continue;
                }

                list.Add(r);
            }
            return list;
        }

        public List<RenderPassMigrationFinding> GetFilteredPassFindings()
        {
            var list = new List<RenderPassMigrationFinding>();
            if (_passFindings == null)
                return list;

            string q = (_searchFilter ?? string.Empty).Trim();
            for (int i = 0; i < _passFindings.Count; i++)
            {
                RenderPassMigrationFinding f = _passFindings[i];
                if (!string.IsNullOrEmpty(q))
                {
                    bool match =
                        (!string.IsNullOrEmpty(f.typeFullName) && f.typeFullName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(f.assemblyName) && f.assemblyName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(f.migrationStatus) && f.migrationStatus.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match)
                        continue;
                }

                list.Add(f);
            }
            return list;
        }

        public void RefreshUiToolkitView()
        {
            if (_heatmapContainer == null || _contentScroll == null)
                return;

            _heatmapContainer.Clear();
            _contentScroll.Clear();

            ResidentDrawerHeatmapSummary heatmap = RenderGraphDoctorMcpBridge.ComputeHeatmapSummary(_sceneReport, _passFindings);
            _heatmapContainer.Add(BuildHeatmapCard(heatmap));

            if (_selectedTab == 0)
            {
                List<RendererEligibilityRecord> filtered = GetFilteredSceneRecords(includeEligible: false);
                for (int i = 0; i < filtered.Count; i++)
                {
                    _contentScroll.Add(BuildRendererFindingCard(filtered[i]));
                }
            }
            else
            {
                List<RenderPassMigrationFinding> filteredPasses = GetFilteredPassFindings();
                for (int i = 0; i < filteredPasses.Count; i++)
                {
                    _contentScroll.Add(BuildRenderPassFindingCard(filteredPasses[i]));
                }
            }
        }

        public static VisualElement BuildHeatmapCard(ResidentDrawerHeatmapSummary h)
        {
            var card = new VisualElement { name = "resident-drawer-heatmap-card" };
            card.style.paddingTop = 8f;
            card.style.paddingBottom = 8f;
            card.style.paddingLeft = 10f;
            card.style.paddingRight = 10f;
            card.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);

            var title = new Label(
                $"GPU Resident Drawer & Render Graph Heatmap — MeshRenderers: {h.totalRenderers} (Eligible: {h.eligibleRenderers} | Disqualified: {h.disqualifiedRenderers})");
            title.style.marginBottom = 4f;
            card.Add(title);

            // Stacked horizontal Eligibility vs Disqualified bar
            var barTrack = new VisualElement { name = "eligibility-distribution-bar" };
            barTrack.style.flexDirection = FlexDirection.Row;
            barTrack.style.height = 14f;
            barTrack.style.marginBottom = 6f;
            barTrack.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f);

            if (h.totalRenderers > 0)
            {
                if (h.eligibilityRatio > 0f)
                {
                    var eligSeg = new VisualElement { name = "seg-eligible" };
                    eligSeg.style.flexGrow = h.eligibilityRatio;
                    eligSeg.style.height = 14f;
                    eligSeg.style.backgroundColor = new Color(0.24f, 0.76f, 0.42f, 1f);
                    barTrack.Add(eligSeg);
                }
                if (h.disqualifiedRatio > 0f)
                {
                    var disqSeg = new VisualElement { name = "seg-disqualified" };
                    disqSeg.style.flexGrow = h.disqualifiedRatio;
                    disqSeg.style.height = 14f;
                    disqSeg.style.backgroundColor = new Color(0.88f, 0.24f, 0.24f, 1f);
                    barTrack.Add(disqSeg);
                }
            }
            card.Add(barTrack);

            var disqualifierBreakdown = new Label(
                $"MPB Breaks: {h.propertyBlockViolations} | Instancing Off: {h.instancingDisabledViolations} | " +
                $"StaticBatch Conflicts: {h.staticBatchingConflicts} | Negative Scale: {h.negativeScaleViolations} | " +
                $"LPPV: {h.lppvViolations} | Shader Missing DOTS: {h.shaderIncompatibilityViolations} | Overdraw Risk: {h.desktopOverdrawRiskMeshes}");
            disqualifierBreakdown.style.fontSize = 11f;
            disqualifierBreakdown.style.marginBottom = 4f;
            card.Add(disqualifierBreakdown);

            var passBar = new ProgressBar
            {
                name = "rendergraph-migration-gauge",
                lowValue = 0f,
                highValue = 100f,
                value = h.renderGraphMigrationRatio * 100f,
                title = $"Render Graph Pass Migration: {(h.renderGraphMigrationRatio * 100f):F0}% (Native: {h.nativeRenderGraphPasses} | Dual-Path: {h.dualPathPasses} | Legacy-Only: {h.legacyOnlyPasses})"
            };
            card.Add(passBar);

            return card;
        }

        private static VisualElement BuildRendererFindingCard(RendererEligibilityRecord r)
        {
            var card = new VisualElement { name = $"renderer-card-{r.gameObjectName}" };
            card.style.marginBottom = 6f;
            card.style.paddingTop = 6f;
            card.style.paddingBottom = 6f;
            card.style.paddingLeft = 8f;
            card.style.paddingRight = 8f;
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = r.IsEligible
                ? new Color(0.24f, 0.76f, 0.42f, 1f)
                : new Color(0.88f, 0.24f, 0.24f, 1f);
            card.style.backgroundColor = new Color(0.21f, 0.21f, 0.23f, 1f);

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.justifyContent = Justify.SpaceBetween;
            headerRow.style.alignItems = Align.Center;

            var title = new Label($"{r.gameObjectName} ({r.triangleCount} tris) — [{r.disqualifiers}]");
            headerRow.Add(title);

            var selectBtn = new Button(() =>
            {
                UnityEngine.Object obj = EditorUtility.InstanceIDToObject(r.instanceId);
                if (obj != null) Selection.activeObject = obj;
            })
            {
                text = "Select"
            };
            selectBtn.style.width = 60f;
            headerRow.Add(selectBtn);
            card.Add(headerRow);

            var matLabel = new Label($"Material: {r.materialName} ({r.shaderName})");
            matLabel.style.fontSize = 11f;
            card.Add(matLabel);

            var fixLabel = new Label($"Fix: {r.recommendation}");
            fixLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Add(fixLabel);

            return card;
        }

        private static VisualElement BuildRenderPassFindingCard(RenderPassMigrationFinding f)
        {
            var card = new VisualElement { name = $"pass-card-{f.typeFullName}" };
            card.style.marginBottom = 6f;
            card.style.paddingTop = 6f;
            card.style.paddingBottom = 6f;
            card.style.paddingLeft = 8f;
            card.style.paddingRight = 8f;
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = f.implementsRecordRenderGraph
                ? new Color(0.24f, 0.76f, 0.42f, 1f)
                : new Color(0.92f, 0.62f, 0.16f, 1f);
            card.style.backgroundColor = new Color(0.21f, 0.21f, 0.23f, 1f);

            var title = new Label($"{f.typeFullName} [{f.assemblyName}]");
            card.Add(title);

            var flagsLabel = new Label($"RecordRenderGraph: {f.implementsRecordRenderGraph} | Legacy Execute: {f.implementsLegacyExecute}");
            flagsLabel.style.fontSize = 11f;
            card.Add(flagsLabel);

            var statusLabel = new Label(f.migrationStatus);
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            card.Add(statusLabel);

            return card;
        }

        private void OnGUI()
        {
            if (rootVisualElement != null && rootVisualElement.childCount > 0)
                return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("PC Unity 6 Render Graph & GPU Resident Drawer Doctor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Audits scene MeshRenderers and custom shaders for GPU Resident Drawer (BatchRendererGroup) disqualifiers and inspects compiled ScriptableRenderPass classes for Unity 6 RecordRenderGraph compliance.",
                MessageType.Info);

            _selectedTab = GUILayout.Toolbar(_selectedTab, Tabs, GUILayout.Height(26f));
            EditorGUILayout.Space(6f);

            if (_selectedTab == 0)
                DrawResidentDrawerTab();
            else
                DrawRenderGraphPassesTab();
        }

        private void DrawResidentDrawerTab()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Audit Scene for GPU Resident Drawer", GUILayout.Height(28f)))
            {
                RunSceneAudit();
            }

            if (GUILayout.Button("Auto-Fix Eligible Material Instancing & Static Flags", GUILayout.Height(28f)))
            {
                AutoFixSceneRenderers();
                RunSceneAudit();
            }

            if (GUILayout.Button("Export Markdown & MCP JSON Report", GUILayout.Height(28f)))
            {
                ExportReports();
            }
            EditorGUILayout.EndHorizontal();

            if (_sceneReport == null) return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                $"Total MeshRenderers: {_sceneReport.totalMeshRenderers} | " +
                $"Eligible: {_sceneReport.eligibleRenderers} | " +
                $"Disqualified: {_sceneReport.disqualifiedRenderers}",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"MaterialPropertyBlock Breaks: {_sceneReport.propertyBlockViolations} | " +
                $"Instancing Off: {_sceneReport.instancingDisabledViolations} | " +
                $"StaticBatch Conflicts: {_sceneReport.staticBatchingConflicts} | " +
                $"Negative Scale: {_sceneReport.negativeScaleViolations} | " +
                $"LPPV Used: {_sceneReport.lppvViolations}");

            _scrollScene = EditorGUILayout.BeginScrollView(_scrollScene);
            List<RendererEligibilityRecord> filtered = GetFilteredSceneRecords(includeEligible: false);
            for (int i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];

                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{r.gameObjectName} ({r.triangleCount} tris) — [{r.disqualifiers}]", EditorStyles.boldLabel);
                if (GUILayout.Button("Select", GUILayout.Width(60f)))
                {
                    UnityEngine.Object obj = EditorUtility.InstanceIDToObject(r.instanceId);
                    if (obj != null) Selection.activeObject = obj;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField($"Material: {r.materialName} ({r.shaderName})", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Fix: {r.recommendation}", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawRenderGraphPassesTab()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Compiled Assemblies for ScriptableRenderPass", GUILayout.Height(28f)))
            {
                _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
            }

            if (GUILayout.Button("Export Pass Audit Markdown & MCP JSON", GUILayout.Height(28f)))
            {
                ExportReports();
            }
            EditorGUILayout.EndHorizontal();

            if (_passFindings == null) return;

            EditorGUILayout.Space(6f);
            _scrollPasses = EditorGUILayout.BeginScrollView(_scrollPasses);
            List<RenderPassMigrationFinding> filtered = GetFilteredPassFindings();
            for (int i = 0; i < filtered.Count; i++)
            {
                var f = filtered[i];
                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.LabelField($"{f.typeFullName} [{f.assemblyName}]", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    $"RecordRenderGraph: {f.implementsRecordRenderGraph} | Legacy Execute: {f.implementsLegacyExecute}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(f.migrationStatus, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunSceneAudit()
        {
            MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            _sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                renderers,
                go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));
        }

        private void ExportReports()
        {
            if (_sceneReport == null)
                RunSceneAudit();
            if (_passFindings == null)
                _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();

            string path = EditorUtility.SaveFilePanel("Export Render Graph Doctor Report", "", "RenderGraphDoctorReport.md", "md");
            if (!string.IsNullOrEmpty(path))
            {
                var sb = new StringBuilder();
                sb.AppendLine(_sceneReport.ExportToMarkdown());
                sb.AppendLine();
                sb.AppendLine(RenderGraphAssemblyAuditor.ExportPassFindingsToMarkdown(_passFindings));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                string jsonPath = Path.ChangeExtension(path, ".json");
                File.WriteAllText(jsonPath, RenderGraphDoctorMcpBridge.ExportFullMcpReportToJson(_sceneReport, _passFindings), Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
        }

        private static void AutoFixSceneRenderers()
        {
            MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            RenderGraphDoctorMcpBridge.AutoFixSceneRenderers(renderers);
        }
    }
}
