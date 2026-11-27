using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    [Serializable]
    public sealed class PipelinePrerequisiteAuditResult
    {
        public bool hasActiveSrpAsset;
        public string pipelineAssetName;
        public string pipelineTypeName;
        public bool srpBatcherEnabled;
        public bool gpuResidentDrawerEnabled;
        public string rendererRenderingMode;
        public bool isForwardPlusOrDeferredPlus;
        public bool brgKeepAllStrippingConfigured;
        public bool isFullyReadyForResidentDrawer;
        public List<string> issues = new List<string>();
        public List<string> appliedFixes = new List<string>();
    }

    /// <summary>
    /// Audits and one-click configures all pipeline-level prerequisites required for Unity 6 GPU Resident Drawer
    /// in URP 17 (`Forward+` / `Deferred+` rendering mode, SRP Batcher, `InstancedDrawing` GRD mode,
    /// and `EditorGraphicsSettings.batchRendererGroupShaderStrippingMode == KeepAll`) as well as scanning project Prefabs.
    /// </summary>
    public static class UrpPipelinePrerequisiteDoctor
    {
        private static BatchRendererGroupStrippingMode? _testStrippingModeOverride;

        public static void SetBrgStrippingModeOverrideForTesting(BatchRendererGroupStrippingMode? mode)
        {
            _testStrippingModeOverride = mode;
        }

        public static BatchRendererGroupStrippingMode GetCurrentBrgStrippingMode()
        {
            if (_testStrippingModeOverride.HasValue)
                return _testStrippingModeOverride.Value;
#if UNITY_6000_0_OR_NEWER || YUSUF_DOTNET_TESTS
            return EditorGraphicsSettings.batchRendererGroupShaderStrippingMode;
#else
            return BatchRendererGroupStrippingMode.KeepAll;
#endif
        }

        private static bool TrySetBrgStrippingKeepAll()
        {
            if (_testStrippingModeOverride.HasValue)
            {
                _testStrippingModeOverride = BatchRendererGroupStrippingMode.KeepAll;
                return true;
            }

            UnityEngine.Object gsObj = GraphicsSettings.GetGraphicsSettings();
            if (gsObj == null)
                return false;

            using (var so = new SerializedObject(gsObj))
            {
                SerializedProperty prop = so.FindProperty("m_BrgStripping") ??
                                          so.FindProperty("m_BatchRendererGroupShaderStrippingMode");
                if (prop != null)
                {
                    prop.intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
                    so.ApplyModifiedProperties();
                    return true;
                }
            }
            return false;
        }

        public static PipelinePrerequisiteAuditResult AuditPipelinePrerequisites(ScriptableObject overridePipelineAsset = null)
        {
            var result = new PipelinePrerequisiteAuditResult
            {
                pipelineAssetName = "<None>",
                pipelineTypeName = "<BuiltInRenderPipeline>",
                rendererRenderingMode = "Unknown"
            };

            ScriptableObject asset = overridePipelineAsset != null
                ? overridePipelineAsset
                : GraphicsSettings.currentRenderPipeline;

            result.brgKeepAllStrippingConfigured =
                GetCurrentBrgStrippingMode() == BatchRendererGroupStrippingMode.KeepAll;

            if (!result.brgKeepAllStrippingConfigured)
            {
                result.issues.Add(
                    "BRG SHADER STRIPPING NOT KEEPALL: EditorGraphicsSettings.batchRendererGroupShaderStrippingMode is not KeepAll; DOTS_INSTANCING_ON variants may be stripped from player builds.");
            }

            if (asset == null)
            {
                result.issues.Add("NO SRP ASSET ASSIGNED: GraphicsSettings.currentRenderPipeline is null. Assign a URP 17 (Forward+/Deferred+) or HDRP asset.");
                return result;
            }

            result.hasActiveSrpAsset = true;
            result.pipelineAssetName = asset.name;
            Type t = asset.GetType();
            result.pipelineTypeName = t.Name;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            result.srpBatcherEnabled = ReadBool(asset, t, flags, "supportsSRPBatcher", "m_UseSRPBatcher", GraphicsSettings.useScriptableRenderPipelineBatching);
            if (!result.srpBatcherEnabled)
            {
                result.issues.Add("SRP BATCHER DISABLED: GPU Resident Drawer requires the SRP Batcher to be enabled on the active RenderPipelineAsset.");
            }

            int grdMode = ReadInt(asset, t, flags, "gpuResidentDrawerMode", "m_GPUResidentDrawerMode", 0);
            result.gpuResidentDrawerEnabled = grdMode != 0;
            if (!result.gpuResidentDrawerEnabled)
            {
                result.issues.Add("GPU RESIDENT DRAWER DISABLED: gpuResidentDrawerMode is Disabled (0) on the active RenderPipelineAsset. Set to InstancedDrawing (1).");
            }

            if (t.Name.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0)
            {
                InspectUrpRendererModes(asset, t, flags, out result.rendererRenderingMode, out result.isForwardPlusOrDeferredPlus);
                if (!result.isForwardPlusOrDeferredPlus)
                {
                    result.issues.Add(
                        $"UNSUPPORTED URP RENDERING MODE ({result.rendererRenderingMode}): In URP 17, GPU Resident Drawer does NOT run on standard Forward or standard Deferred! Switch UniversalRendererData to Forward+ (ForwardPlus) or Deferred+ (DeferredPlus).");
                }
            }
            else if (t.Name.IndexOf("HDRenderPipelineAsset", StringComparison.Ordinal) >= 0)
            {
                result.rendererRenderingMode = "HDRP";
                result.isForwardPlusOrDeferredPlus = true;
            }
            else
            {
                result.rendererRenderingMode = "CustomSRP";
                result.isForwardPlusOrDeferredPlus = true;
            }

            result.isFullyReadyForResidentDrawer =
                result.hasActiveSrpAsset &&
                result.srpBatcherEnabled &&
                result.gpuResidentDrawerEnabled &&
                result.isForwardPlusOrDeferredPlus &&
                result.brgKeepAllStrippingConfigured;

            return result;
        }

        public static PipelinePrerequisiteAuditResult AutoConfigurePipelineForResidentDrawer(ScriptableObject overridePipelineAsset = null)
        {
            ScriptableObject asset = overridePipelineAsset != null
                ? overridePipelineAsset
                : GraphicsSettings.currentRenderPipeline;

            var applied = new List<string>();

            if (GetCurrentBrgStrippingMode() != BatchRendererGroupStrippingMode.KeepAll)
            {
                if (TrySetBrgStrippingKeepAll())
                {
                    applied.Add("Configured GraphicsSettings BatchRendererGroup shader stripping mode to KeepAll.");
                }
            }

            if (!GraphicsSettings.useScriptableRenderPipelineBatching)
            {
                GraphicsSettings.useScriptableRenderPipelineBatching = true;
                applied.Add("Enabled GraphicsSettings.useScriptableRenderPipelineBatching.");
            }

            if (asset != null)
            {
                Undo.RecordObject(asset, "Configure Pipeline for Unity 6 GPU Resident Drawer");
                Type t = asset.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                if (!ReadBool(asset, t, flags, "supportsSRPBatcher", "m_UseSRPBatcher", true))
                {
                    WriteBool(asset, t, flags, "supportsSRPBatcher", "m_UseSRPBatcher", true);
                    applied.Add($"Enabled SRP Batcher on '{asset.name}'.");
                }

                if (ReadInt(asset, t, flags, "gpuResidentDrawerMode", "m_GPUResidentDrawerMode", 0) == 0)
                {
                    WriteIntOrEnum(asset, t, flags, "gpuResidentDrawerMode", "m_GPUResidentDrawerMode", 1); // InstancedDrawing
                    applied.Add($"Set gpuResidentDrawerMode = InstancedDrawing on '{asset.name}'.");
                }

                if (t.Name.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0)
                {
                    FieldInfo rendererListField = t.GetField("m_RendererDataList", flags);
                    if (rendererListField?.GetValue(asset) is IList rendererList)
                    {
                        for (int r = 0; r < rendererList.Count; r++)
                        {
                            object rd = rendererList[r];
                            if (rd == null) continue;
                            if (rd is UnityEngine.Object rdObj)
                                Undo.RecordObject(rdObj, "Upgrade URP Renderer to Forward+/Deferred+");

                            Type rdType = rd.GetType();
                            int mode = ReadInt(rd, rdType, flags, "renderingMode", "m_RenderingMode", 0);
                            // URP RenderingMode: 0 = Forward, 1 = Deferred, 2 = ForwardPlus, 3 = DeferredPlus
                            if (mode == 0)
                            {
                                WriteIntOrEnum(rd, rdType, flags, "renderingMode", "m_RenderingMode", 2); // ForwardPlus
                                if (rd is UnityEngine.Object uo) EditorUtility.SetDirty(uo);
                                applied.Add("Upgraded UniversalRendererData renderingMode from Forward (0) to ForwardPlus (2).");
                            }
                            else if (mode == 1)
                            {
                                // Check if enum supports DeferredPlus (3), otherwise fallback to ForwardPlus (2)
                                int targetMode = SupportsEnumValue(rdType, flags, "renderingMode", "m_RenderingMode", 3) ? 3 : 2;
                                WriteIntOrEnum(rd, rdType, flags, "renderingMode", "m_RenderingMode", targetMode);
                                if (rd is UnityEngine.Object uo) EditorUtility.SetDirty(uo);
                                applied.Add($"Upgraded UniversalRendererData renderingMode from Deferred (1) to {(targetMode == 3 ? "DeferredPlus (3)" : "ForwardPlus (2)")}.");
                            }
                        }
                    }
                }

                EditorUtility.SetDirty(asset);
            }

            PipelinePrerequisiteAuditResult after = AuditPipelinePrerequisites(overridePipelineAsset);
            after.appliedFixes = applied;
            return after;
        }

        /// <summary>
        /// Scans all Prefab assets under the given project folder (`Assets/...`) for GPU Resident Drawer disqualifiers.
        /// </summary>
        public static ResidentDrawerAuditReport AuditProjectPrefabsForResidentDrawer(string rootFolder = "Assets")
        {
            var combined = new ResidentDrawerAuditReport();
            if (string.IsNullOrWhiteSpace(rootFolder))
                rootFolder = "Assets";

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { rootFolder });
            if (guids == null || guids.Length == 0)
                return combined;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefabRoot == null) continue;

                MeshRenderer[] renderers = prefabRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers == null || renderers.Length == 0) continue;

                for (int r = 0; r < renderers.Length; r++)
                {
                    MeshRenderer mr = renderers[r];
                    if (mr == null) continue;

                    combined.totalMeshRenderers++;
                    bool hasStaticBatch = mr.gameObject != null &&
                                          GameObjectUtility.AreStaticEditorFlagsSet(mr.gameObject, StaticEditorFlags.BatchingStatic);

                    RendererEligibilityRecord rec = GpuResidentDrawerAuditor.AnalyzeRendererMultiMaterial(
                        mr,
                        hasStaticBatch,
                        GpuResidentDrawerAuditor.IsShaderDotsInstancingCompatible,
                        path);
                    GpuResidentDrawerAuditor.AccumulateRecordIntoReport(combined, rec);
                }
            }

            combined.records.Sort((a, b) => b.disqualifiers.CompareTo(a.disqualifiers));
            return combined;
        }

        private static void InspectUrpRendererModes(
            ScriptableObject asset,
            Type t,
            BindingFlags flags,
            out string modeSummary,
            out bool isClusterPlus)
        {
            FieldInfo rendererListField = t.GetField("m_RendererDataList", flags);
            if (rendererListField?.GetValue(asset) is IList rendererList && rendererList.Count > 0)
            {
                bool allValid = true;
                bool anyInspected = false;
                var names = new List<string>(rendererList.Count);
                for (int i = 0; i < rendererList.Count; i++)
                {
                    object rd = rendererList[i];
                    if (rd == null) continue;
                    anyInspected = true;
                    int mode = ReadInt(rd, rd.GetType(), flags, "renderingMode", "m_RenderingMode", 0);
                    string modeName = MapUrpRenderingModeName(mode);
                    names.Add(modeName);
                    if (mode != 2 && mode != 3)
                        allValid = false;
                }

                if (anyInspected)
                {
                    modeSummary = string.Join(", ", names);
                    isClusterPlus = allValid;
                    return;
                }
            }

            modeSummary = "Forward (Default)";
            isClusterPlus = false;
        }

        private static string MapUrpRenderingModeName(int mode)
        {
            switch (mode)
            {
                case 0: return "Forward";
                case 1: return "Deferred";
                case 2: return "ForwardPlus";
                case 3: return "DeferredPlus";
                default: return $"Mode({mode})";
            }
        }

        private static bool SupportsEnumValue(Type t, BindingFlags flags, string propName, string fieldName, int enumVal)
        {
            Type memberType = t.GetProperty(propName, flags)?.PropertyType ?? t.GetField(fieldName, flags)?.FieldType;
            if (memberType == null || !memberType.IsEnum)
                return true;
            return Enum.IsDefined(memberType, enumVal);
        }

        private static bool ReadBool(object obj, Type t, BindingFlags flags, string propName, string fieldName, bool fallback)
        {
            PropertyInfo p = t.GetProperty(propName, flags);
            if (p != null && p.PropertyType == typeof(bool))
                return (bool)p.GetValue(obj);
            FieldInfo f = t.GetField(fieldName, flags);
            if (f != null && f.FieldType == typeof(bool))
                return (bool)f.GetValue(obj);
            return fallback;
        }

        private static int ReadInt(object obj, Type t, BindingFlags flags, string propName, string fieldName, int fallback)
        {
            PropertyInfo p = t.GetProperty(propName, flags);
            if (p != null && (p.PropertyType == typeof(int) || p.PropertyType.IsEnum))
                return Convert.ToInt32(p.GetValue(obj));
            FieldInfo f = t.GetField(fieldName, flags);
            if (f != null && (f.FieldType == typeof(int) || f.FieldType.IsEnum))
                return Convert.ToInt32(f.GetValue(obj));
            return fallback;
        }

        private static void WriteBool(object obj, Type t, BindingFlags flags, string propName, string fieldName, bool val)
        {
            PropertyInfo p = t.GetProperty(propName, flags);
            if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
            {
                p.SetValue(obj, val);
                return;
            }
            FieldInfo f = t.GetField(fieldName, flags);
            if (f != null && f.FieldType == typeof(bool))
                f.SetValue(obj, val);
        }

        private static void WriteIntOrEnum(object obj, Type t, BindingFlags flags, string propName, string fieldName, int val)
        {
            PropertyInfo p = t.GetProperty(propName, flags);
            if (p != null && p.CanWrite)
            {
                object converted = p.PropertyType.IsEnum ? Enum.ToObject(p.PropertyType, val) : val;
                p.SetValue(obj, converted);
                return;
            }
            FieldInfo f = t.GetField(fieldName, flags);
            if (f != null)
            {
                object converted = f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, val) : val;
                f.SetValue(obj, converted);
            }
        }
    }
}
