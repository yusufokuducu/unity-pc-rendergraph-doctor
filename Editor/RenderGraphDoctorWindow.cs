using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    /// <summary>
    /// Zero-GC Painter2D stacked horizontal bar chart for GPU Resident Drawer scene/prefab eligibility.
    /// </summary>
    public sealed class ResidentDrawerEligibilityChartElement : VisualElement
    {
        private ResidentDrawerHeatmapSummary _summary;

        public ResidentDrawerEligibilityChartElement()
        {
            name = "eligibility-painter-chart";
            AddToClassList("eligibility-painter-bar");
            generateVisualContent += OnGenerateVisualContent;
        }

        public void SetSummary(ResidentDrawerHeatmapSummary summary)
        {
            _summary = summary;
            MarkDirtyRepaint();
        }

        private void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            float w = contentRect.width;
            float h = contentRect.height;
            if (w < 2f || h < 2f)
                return;

            Painter2D p = ctx.painter2D;
            DrawRect(p, 0f, 0f, w, h, new Color(0.12f, 0.12f, 0.14f, 1f));

            if (_summary.totalRenderers <= 0)
                return;

            float eligW = w * Mathf.Clamp01(_summary.eligibilityRatio);
            if (eligW > 0.1f)
                DrawRect(p, 0f, 0f, eligW, h, new Color(0.24f, 0.76f, 0.42f, 1f));
            if (w - eligW > 0.1f)
                DrawRect(p, eligW, 0f, w - eligW, h, new Color(0.88f, 0.24f, 0.24f, 1f));
        }

        private static void DrawRect(Painter2D p, float x, float y, float w, float h, Color col)
        {
            p.fillColor = col;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }
    }

    public sealed class RenderGraphDoctorWindow : EditorWindow
    {
        private ResidentDrawerAuditReport _sceneReport;
        private List<RenderPassMigrationFinding> _passFindings;
        private PipelinePrerequisiteAuditResult _pipelineStatus;
        private readonly List<RendererEligibilityRecord> _filteredSceneCache = new List<RendererEligibilityRecord>();
        private readonly List<RenderPassMigrationFinding> _filteredPassCache = new List<RenderPassMigrationFinding>();
        private Vector2 _scrollScene;
        private int _selectedTab;
        private string _searchFilter = string.Empty;
        private bool _includeEligibleRows;
        private static readonly string[] Tabs = { "GPU Resident Drawer (Scene & Prefabs)", "Render Graph Passes (User Assemblies)" };

        private VisualElement _pipelineBannerContainer;
        private VisualElement _heatmapContainer;
        private VisualElement _tableHostContainer;
        private MultiColumnListView _sceneTable;
        private MultiColumnListView _passTable;
        private ResidentDrawerEligibilityChartElement _eligibilityChart;
        private Label _mcpStatusLabel;

        public ResidentDrawerAuditReport SceneReport => _sceneReport;
        public IReadOnlyList<RenderPassMigrationFinding> PassFindings => _passFindings;
        public int SelectedTab => _selectedTab;
        public string SearchFilter => _searchFilter;
        public MultiColumnListView VirtualizedSceneTable => _sceneTable;
        public MultiColumnListView VirtualizedPassTable => _passTable;

        [MenuItem("Window/Analysis/PC Render Graph & GPU Resident Drawer Doctor")]
        public static void ShowWindow()
        {
            var win = GetWindow<RenderGraphDoctorWindow>("PC RenderGraph Doctor");
            win.minSize = new Vector2(800f, 580f);
            win.Show();
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            if (root == null)
                return;

            root.Clear();
            ApplyThemeStyleSheet(root);
            root.AddToClassList("doctor-root");
            root.AddToClassList(EditorGUIUtility.isProSkin ? "theme-dark" : "theme-light");

            var titleLabel = new Label("PC Unity 6 Render Graph & GPU Resident Drawer Doctor (v2.0)")
            {
                name = "header-title"
            };
            titleLabel.AddToClassList("header-title");
            root.Add(titleLabel);

            var infoBox = new HelpBox(
                "Audits URP 17 Forward+/Deferred+ & BRG KeepAll prerequisites, scene/prefab MeshRenderers across all sub-meshes & GRDExclusionReason rules, and custom ScriptableRenderPass implementations against Unity's 8-item Render Graph checklist.",
                HelpBoxMessageType.Info);
            root.Add(infoBox);

            // Tab Switcher Row
            var tabRow = new VisualElement { name = "tab-row" };
            tabRow.AddToClassList("toolbar-row");

            var tabSceneBtn = new Button(() =>
            {
                _selectedTab = 0;
                RefreshUiToolkitView();
            })
            {
                name = "btn-tab-scene",
                text = Tabs[0]
            };
            tabSceneBtn.AddToClassList("toolbar-btn");

            var tabPassBtn = new Button(() =>
            {
                _selectedTab = 1;
                RefreshUiToolkitView();
            })
            {
                name = "btn-tab-passes",
                text = Tabs[1]
            };
            tabPassBtn.AddToClassList("toolbar-btn-last");

            tabRow.Add(tabSceneBtn);
            tabRow.Add(tabPassBtn);
            root.Add(tabRow);

            // Action Toolbar Row
            var actionRow = new VisualElement { name = "action-toolbar" };
            actionRow.AddToClassList("toolbar-row");

            var auditSceneBtn = new Button(() =>
            {
                RunSceneAudit();
                _pipelineStatus = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
                RefreshUiToolkitView();
            })
            {
                name = "btn-audit-scene",
                text = "Audit Scene & Pipeline"
            };
            auditSceneBtn.AddToClassList("toolbar-btn");

            var auditPrefabBtn = new Button(() =>
            {
                _sceneReport = UrpPipelinePrerequisiteDoctor.AuditProjectPrefabsForResidentDrawer("Assets");
                _selectedTab = 0;
                RefreshUiToolkitView();
            })
            {
                name = "btn-audit-prefabs",
                text = "Audit Project Prefabs"
            };
            auditPrefabBtn.AddToClassList("toolbar-btn");

            var autoFixBtn = new Button(() =>
            {
                UrpPipelinePrerequisiteDoctor.AutoConfigurePipelineForResidentDrawer();
                AutoFixSceneRenderers();
                RunSceneAudit();
                _pipelineStatus = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
                RefreshUiToolkitView();
            })
            {
                name = "btn-autofix-scene",
                text = "Auto-Fix Pipeline & Scene"
            };
            autoFixBtn.AddToClassList("toolbar-btn");

            var scanAsmBtn = new Button(() =>
            {
                _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                _selectedTab = 1;
                RefreshUiToolkitView();
            })
            {
                name = "btn-scan-passes",
                text = "Scan User RenderPasses"
            };
            scanAsmBtn.AddToClassList("toolbar-btn");

            var mcpBtn = new Button(() =>
            {
                RenderGraphDoctorMcpLiveServer.ToggleSharedServerMenu();
                UpdateMcpStatusBadge();
            })
            {
                name = "btn-toggle-mcp",
                text = "Toggle Live MCP"
            };
            mcpBtn.AddToClassList("toolbar-btn");

            var exportBtn = new Button(ExportReports)
            {
                name = "btn-export",
                text = "Export MD / JSON"
            };
            exportBtn.AddToClassList("toolbar-btn-last");

            actionRow.Add(auditSceneBtn);
            actionRow.Add(auditPrefabBtn);
            actionRow.Add(autoFixBtn);
            actionRow.Add(scanAsmBtn);
            actionRow.Add(mcpBtn);
            actionRow.Add(exportBtn);
            root.Add(actionRow);

            _mcpStatusLabel = new Label { name = "mcp-status-label" };
            _mcpStatusLabel.AddToClassList("meta-label");
            root.Add(_mcpStatusLabel);
            UpdateMcpStatusBadge();

            var searchField = new TextField("Search GameObject / Material / Shader / RenderPass")
            {
                name = "search-field",
                value = _searchFilter
            };
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchFilter = evt.newValue ?? string.Empty;
                RefreshUiToolkitView();
            });
            root.Add(searchField);

            _pipelineBannerContainer = new VisualElement { name = "pipeline-banner-container" };
            root.Add(_pipelineBannerContainer);

            _heatmapContainer = new VisualElement { name = "heatmap-container" };
            root.Add(_heatmapContainer);

            _tableHostContainer = new VisualElement { name = "table-host-container" };
            _tableHostContainer.AddToClassList("virtualized-table");
            root.Add(_tableHostContainer);

            _sceneTable = BuildVirtualizedSceneTable();
            _passTable = BuildVirtualizedPassTable();

            RefreshUiToolkitView();
        }

        private MultiColumnListView BuildVirtualizedSceneTable()
        {
            var table = new MultiColumnListView
            {
                name = "scene-eligibility-table",
                fixedItemHeight = 26f,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly
            };
            table.AddToClassList("virtualized-table");

            AddTextColumn(table, "go", "GameObject", 165f, true, i => _filteredSceneCache[i].gameObjectName);
            AddTextColumn(table, "tris", "SubM / Tris", 86f, false, i => $"{Math.Max(1, _filteredSceneCache[i].subMeshCount)} / {_filteredSceneCache[i].triangleCount}");
            AddTextColumn(table, "mat", "Material (Shader)", 190f, true, i => $"{_filteredSceneCache[i].materialName} ({_filteredSceneCache[i].shaderName})");
            AddTextColumn(table, "disq", "Disqualifiers", 185f, false, i => _filteredSceneCache[i].disqualifiers.ToString());
            AddTextColumn(table, "rec", "Fix Recommendation", 220f, true, i => _filteredSceneCache[i].recommendation);

            var selectCol = new Column
            {
                name = "select",
                title = "Ping",
                width = 64f,
                minWidth = 56f,
                stretchable = false,
                makeCell = () =>
                {
                    var btn = new Button { text = "Select" };
                    btn.clicked += () =>
                    {
                        if (btn.userData is int rowIdx && rowIdx >= 0 && rowIdx < _filteredSceneCache.Count)
                        {
                            SelectRecordTarget(_filteredSceneCache[rowIdx]);
                        }
                    };
                    return btn;
                },
                bindCell = (el, idx) =>
                {
                    if (el is Button btn)
                        btn.userData = idx;
                }
            };
            table.columns.Add(selectCol);

            table.itemsSource = _filteredSceneCache;
            return table;
        }

        private MultiColumnListView BuildVirtualizedPassTable()
        {
            var table = new MultiColumnListView
            {
                name = "renderpass-migration-table",
                fixedItemHeight = 26f,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly
            };
            table.AddToClassList("virtualized-table");

            AddTextColumn(table, "type", "ScriptableRenderPass Type", 240f, true, i => _filteredPassCache[i].typeFullName);
            AddTextColumn(table, "asm", "User Assembly", 160f, false, i => _filteredPassCache[i].assemblyName);
            AddTextColumn(table, "rg", "RecordRenderGraph", 118f, false, i => _filteredPassCache[i].implementsRecordRenderGraph ? "Yes" : "NO");
            AddTextColumn(table, "exec", "Legacy Execute", 105f, false, i => _filteredPassCache[i].implementsLegacyExecute ? "Yes" : "No");
            AddTextColumn(table, "status", "Unity 6 Status", 240f, true, i => _filteredPassCache[i].migrationStatus);

            table.itemsSource = _filteredPassCache;
            return table;
        }

        private static void AddTextColumn(
            MultiColumnListView table,
            string colName,
            string title,
            float width,
            bool stretchable,
            Func<int, string> selector)
        {
            table.columns.Add(new Column
            {
                name = colName,
                title = title,
                width = width,
                minWidth = 50f,
                stretchable = stretchable,
                makeCell = () =>
                {
                    var l = new Label();
                    l.AddToClassList("cell-label");
                    return l;
                },
                bindCell = (el, idx) =>
                {
                    if (el is Label l && idx >= 0 && table.itemsSource != null && idx < table.itemsSource.Count)
                        l.text = selector(idx) ?? string.Empty;
                }
            });
        }

        private static void SelectRecordTarget(RendererEligibilityRecord rec)
        {
            if (rec.targetGameObject != null)
            {
                Selection.activeObject = rec.targetGameObject;
                return;
            }
#if UNITY_6000_4_OR_NEWER
            UnityEngine.Object obj = EditorUtility.EntityIdToObject(rec.entityId);
#else
            UnityEngine.Object obj = EditorUtility.InstanceIDToObject(rec.instanceId);
#endif
            if (obj != null)
                Selection.activeObject = obj;
        }

        private void UpdateMcpStatusBadge()
        {
            if (_mcpStatusLabel == null) return;
            RenderGraphDoctorMcpLiveServer srv = RenderGraphDoctorMcpLiveServer.Shared;
            _mcpStatusLabel.text = srv.IsRunning
                ? $"Live Loopback MCP Server: ONLINE at {srv.EndpointUrl}"
                : "Live Loopback MCP Server: Stopped (Click 'Toggle Live MCP' or use Unity 6 `unity mcp`)";
        }

        private static void ApplyThemeStyleSheet(VisualElement root)
        {
            string[] guids = AssetDatabase.FindAssets("RenderGraphDoctorTheme t:StyleSheet");
            if (guids != null && guids.Length > 0)
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (sheet != null)
                    root.styleSheets.Add(sheet);
            }
        }

        public void SetCustomAuditData(
            ResidentDrawerAuditReport sceneReport,
            List<RenderPassMigrationFinding> passFindings = null,
            PipelinePrerequisiteAuditResult pipelineStatus = null)
        {
            _sceneReport = sceneReport;
            if (passFindings != null)
                _passFindings = passFindings;
            if (pipelineStatus != null)
                _pipelineStatus = pipelineStatus;
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

        public void SetIncludeEligibleRows(bool includeEligible)
        {
            _includeEligibleRows = includeEligible;
            RefreshUiToolkitView();
        }

        public List<RendererEligibilityRecord> GetFilteredSceneRecords(bool? includeEligibleOverride = null)
        {
            var list = new List<RendererEligibilityRecord>();
            if (_sceneReport == null || _sceneReport.records == null)
                return list;

            bool showEligible = includeEligibleOverride ?? _includeEligibleRows;
            string q = (_searchFilter ?? string.Empty).Trim();
            for (int i = 0; i < _sceneReport.records.Count; i++)
            {
                RendererEligibilityRecord r = _sceneReport.records[i];
                if (!showEligible && r.IsEligible)
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
            if (_heatmapContainer == null || _tableHostContainer == null)
                return;

            _pipelineBannerContainer?.Clear();
            if (_pipelineBannerContainer != null && _pipelineStatus != null && !_pipelineStatus.isFullyReadyForResidentDrawer)
            {
                var warnBox = new HelpBox(
                    "PIPELINE PREREQUISITE ISSUE: " + string.Join(" ", _pipelineStatus.issues),
                    HelpBoxMessageType.Warning);
                _pipelineBannerContainer.Add(warnBox);
            }

            _heatmapContainer.Clear();
            ResidentDrawerHeatmapSummary heatmap = RenderGraphDoctorMcpBridge.ComputeHeatmapSummary(_sceneReport, _passFindings);
            _heatmapContainer.Add(BuildHeatmapCard(heatmap, out _eligibilityChart));

            _tableHostContainer.Clear();
            if (_selectedTab == 0)
            {
                _filteredSceneCache.Clear();
                _filteredSceneCache.AddRange(GetFilteredSceneRecords());
                if (_sceneTable != null)
                {
                    _sceneTable.itemsSource = _filteredSceneCache;
                    _sceneTable.Rebuild();
                    _tableHostContainer.Add(_sceneTable);
                }
            }
            else
            {
                _filteredPassCache.Clear();
                _filteredPassCache.AddRange(GetFilteredPassFindings());
                if (_passTable != null)
                {
                    _passTable.itemsSource = _filteredPassCache;
                    _passTable.Rebuild();
                    _tableHostContainer.Add(_passTable);
                }
            }
        }

        public static VisualElement BuildHeatmapCard(
            ResidentDrawerHeatmapSummary h,
            out ResidentDrawerEligibilityChartElement chart)
        {
            var card = new VisualElement { name = "resident-drawer-heatmap-card" };
            card.AddToClassList("summary-card");
            card.AddToClassList(h.disqualifiedRenderers > 0 ? "border-warn" : "border-good");

            var title = new Label(
                $"GPU Resident Drawer & Render Graph Heatmap — MeshRenderers: {h.totalRenderers} (Eligible: {h.eligibleRenderers} | Disqualified: {h.disqualifiedRenderers})");
            card.Add(title);

            chart = new ResidentDrawerEligibilityChartElement();
            chart.SetSummary(h);
            card.Add(chart);

            var disqualifierBreakdown = new Label(
                $"MPB: {h.propertyBlockViolations} | Instancing Off: {h.instancingDisabledViolations} | " +
                $"StaticBatch: {h.staticBatchingConflicts} | NegScale: {h.negativeScaleViolations} | " +
                $"LPPV: {h.lppvViolations} | ProbeAnchor: {h.probeAnchorViolations} | " +
                $"VertexStreams: {h.customVertexStreamViolations} | LODFade: {h.lodCrossFadeViolations} | " +
                $"Missing DOTS Kw: {h.shaderIncompatibilityViolations}");
            disqualifierBreakdown.AddToClassList("meta-label");
            card.Add(disqualifierBreakdown);

            var passBar = new ProgressBar
            {
                name = "rendergraph-migration-gauge",
                lowValue = 0f,
                highValue = 100f,
                value = h.renderGraphMigrationRatio * 100f,
                title = $"User RenderPass Migration: {(h.renderGraphMigrationRatio * 100f):F0}% (Native: {h.nativeRenderGraphPasses} | Dual-Path: {h.dualPathPasses} | Legacy-Only: {h.legacyOnlyPasses})"
            };
            card.Add(passBar);

            return card;
        }

        private void OnGUI()
        {
            if (rootVisualElement != null && rootVisualElement.childCount > 0)
                return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("PC Unity 6 Render Graph & GPU Resident Drawer Doctor", EditorStyles.boldLabel);
            _scrollScene = EditorGUILayout.BeginScrollView(_scrollScene);
            foreach (var r in GetFilteredSceneRecords())
            {
                EditorGUILayout.LabelField($"{r.gameObjectName} — [{r.disqualifiers}]");
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunSceneAudit()
        {
            MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
            _sceneReport = GpuResidentDrawerAuditor.AuditSceneRenderers(
                renderers,
                go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic),
                GpuResidentDrawerAuditor.IsShaderDotsInstancingCompatible);
        }

        private void ExportReports()
        {
            if (_sceneReport == null)
                RunSceneAudit();
            if (_passFindings == null)
                _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
            if (_pipelineStatus == null)
                _pipelineStatus = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();

            string path = EditorUtility.SaveFilePanel("Export Render Graph Doctor Report", "", "RenderGraphDoctorReport.md", "md");
            if (!string.IsNullOrEmpty(path))
            {
                var sb = new StringBuilder();
                sb.AppendLine(_sceneReport.ExportToMarkdown());
                sb.AppendLine();
                sb.AppendLine(RenderGraphAssemblyAuditor.ExportPassFindingsToMarkdown(_passFindings));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                string jsonPath = Path.ChangeExtension(path, ".json");
                File.WriteAllText(jsonPath, RenderGraphDoctorMcpBridge.ExportFullMcpReportToJson(_sceneReport, _passFindings, _pipelineStatus), Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
        }

        private static void AutoFixSceneRenderers()
        {
            MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
            RenderGraphDoctorMcpBridge.AutoFixSceneRenderers(renderers);
        }
    }
}
