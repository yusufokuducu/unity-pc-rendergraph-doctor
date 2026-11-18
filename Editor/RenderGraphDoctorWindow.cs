using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    public sealed class RenderGraphDoctorWindow : EditorWindow
    {
        private ResidentDrawerAuditReport _sceneReport;
        private List<RenderPassMigrationFinding> _passFindings;
        private Vector2 _scrollScene;
        private Vector2 _scrollPasses;
        private int _selectedTab;
        private static readonly string[] Tabs = { "GPU Resident Drawer (Scene)", "Render Graph Passes (Assemblies)" };

        [MenuItem("Window/Analysis/PC Render Graph & GPU Resident Drawer Doctor")]
        public static void ShowWindow()
        {
            var win = GetWindow<RenderGraphDoctorWindow>("PC RenderGraph Doctor");
            win.minSize = new Vector2(680f, 480f);
            win.Show();
        }

        private void OnGUI()
        {
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

            if (GUILayout.Button("Export Markdown Report", GUILayout.Height(28f)))
            {
                ExportSceneMarkdown();
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
            for (int i = 0; i < _sceneReport.records.Count; i++)
            {
                var r = _sceneReport.records[i];
                if (r.IsEligible) continue;

                EditorGUILayout.BeginVertical(GUI.skin.box);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{r.gameObjectName} ({r.triangleCount} tris) — [{r.disqualifiers}]", EditorStyles.boldLabel);
                if (GUILayout.Button("Select", GUILayout.Width(60f)))
                {
                    Object obj = EditorUtility.InstanceIDToObject(r.instanceId);
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

            if (GUILayout.Button("Export Pass Audit Markdown", GUILayout.Height(28f)))
            {
                if (_passFindings == null)
                    _passFindings = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                string path = EditorUtility.SaveFilePanel("Export RenderPass Audit", "", "RenderGraphPassAudit.md", "md");
                if (!string.IsNullOrEmpty(path))
                {
                    File.WriteAllText(path, RenderGraphAssemblyAuditor.ExportPassFindingsToMarkdown(_passFindings), Encoding.UTF8);
                    EditorUtility.RevealInFinder(path);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_passFindings == null) return;

            EditorGUILayout.Space(6f);
            _scrollPasses = EditorGUILayout.BeginScrollView(_scrollPasses);
            for (int i = 0; i < _passFindings.Count; i++)
            {
                var f = _passFindings[i];
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

        private void ExportSceneMarkdown()
        {
            if (_sceneReport == null)
                RunSceneAudit();

            string path = EditorUtility.SaveFilePanel("Export Resident Drawer Scene Report", "", "ResidentDrawerReport.md", "md");
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, _sceneReport.ExportToMarkdown(), Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
        }

        private static void AutoFixSceneRenderers()
        {
            MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null) continue;

                if (mr.HasPropertyBlock())
                {
                    mr.SetPropertyBlock(null);
                }

                if (mr.sharedMaterial != null && !mr.sharedMaterial.enableInstancing)
                {
                    Undo.RecordObject(mr.sharedMaterial, "Enable GPU Instancing");
                    mr.sharedMaterial.enableInstancing = true;
                    EditorUtility.SetDirty(mr.sharedMaterial);
                }

                if (GameObjectUtility.AreStaticEditorFlagsSet(mr.gameObject, StaticEditorFlags.BatchingStatic))
                {
                    Undo.RecordObject(mr.gameObject, "Clear BatchingStatic for GPU Resident Drawer");
                    StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(mr.gameObject);
                    flags &= ~StaticEditorFlags.BatchingStatic;
                    GameObjectUtility.SetStaticEditorFlags(mr.gameObject, flags);
                }
            }
        }
    }
}
