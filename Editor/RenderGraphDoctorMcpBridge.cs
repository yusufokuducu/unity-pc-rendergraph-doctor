using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    [Serializable]
    public struct ResidentDrawerHeatmapSummary
    {
        public int totalRenderers;
        public int eligibleRenderers;
        public int disqualifiedRenderers;
        public float eligibilityRatio;
        public float disqualifiedRatio;
        public int propertyBlockViolations;
        public int instancingDisabledViolations;
        public int staticBatchingConflicts;
        public int negativeScaleViolations;
        public int lppvViolations;
        public int probeAnchorViolations;
        public int customVertexStreamViolations;
        public int lodCrossFadeViolations;
        public int shaderIncompatibilityViolations;
        public int desktopOverdrawRiskMeshes;
        public int totalRenderPasses;
        public int nativeRenderGraphPasses;
        public int dualPathPasses;
        public int legacyOnlyPasses;
        public float renderGraphMigrationRatio;
    }

    [Serializable]
    public sealed class SceneAutoFixReport
    {
        public int totalInspected;
        public int renderersFixed;
        public int propertyBlocksCleared;
        public int materialsInstancingEnabled;
        public int lppvConvertedToBlendProbes;
        public int probeAnchorsCleared;
        public int staticBatchingFlagsCleared;
        public List<string> appliedFixes = new List<string>();
    }

    [Serializable]
    public sealed class RenderPassSourceAutoFixReport
    {
        public string filePath;
        public bool modified;
        public int hoistedCapturedFieldCount;
        public int skippedUnsafeCaptureLambdaCount;
        public string patchedSource;
        public string unifiedDiff;
        public List<string> appliedFixes = new List<string>();
        public RenderGraphSourceAuditResult beforeAudit;
        public RenderGraphSourceAuditResult afterAudit;
    }

    /// <summary>
    /// Model Context Protocol (MCP) & AI Agent JSON-RPC 2.0 Bridge for PC Unity 6 Render Graph & GPU Resident Drawer Doctor (v2.0).
    /// Features capture-safe SetRenderFunc static lambda auto-fixer (automatically hoisting captured fields into PassData
    /// or preventing CS8820 errors), URP 17 Forward+/Deferred+ pipeline doctor, prefab auditing, and full MCP lifecycle.
    /// </summary>
    public static class RenderGraphDoctorMcpBridge
    {
        public const string ServerName = "unity-pc-rendergraph-doctor";
        public const string PackageVersion = "2.0.1";
        public const string McpProtocolVersion = "2024-11-05";
        public const string LatestReportResourceUri = "mcp://unity-pc-rendergraph-doctor/latest-report";

        private static readonly Regex NonStaticSetRenderFuncLambdaRegex = new Regex(
            @"(\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*)(?!static\b)(\(([^)]*)\)\s*=>|([A-Za-z_]\w*)\s*=>|delegate\s*(?:\(([^)]*)\))?)",
            RegexOptions.Compiled);

        private static readonly Regex SetRenderFuncMethodGroupCallRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*([A-Za-z_]\w*)\s*\)",
            RegexOptions.Compiled);

        private static readonly Regex PassDataClassFullRegex = new Regex(
            @"(\bclass\s+(\w*PassData\w*)\s*\{)([\s\S]*?)(\})",
            RegexOptions.Compiled);

        private static readonly Regex TypedInstanceFieldDeclRegex = new Regex(
            @"^\s*(?:private|protected|public|internal)?\s*(?:readonly\s+)?(?!static\b)(?!const\b)([A-Za-z_][\w<>.\[\]]*)\s+([A-Za-z_]\w*)\s*(?:=|;)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex OutPassDataVarRegex = new Regex(
            @"\bout\s+(?:var|[A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*\)",
            RegexOptions.Compiled);

        public static ResidentDrawerHeatmapSummary ComputeHeatmapSummary(
            ResidentDrawerAuditReport sceneReport,
            IReadOnlyList<RenderPassMigrationFinding> passFindings = null)
        {
            var h = new ResidentDrawerHeatmapSummary();
            if (sceneReport != null)
            {
                h.totalRenderers = sceneReport.totalMeshRenderers;
                h.eligibleRenderers = sceneReport.eligibleRenderers;
                h.disqualifiedRenderers = sceneReport.disqualifiedRenderers;
                h.propertyBlockViolations = sceneReport.propertyBlockViolations;
                h.instancingDisabledViolations = sceneReport.instancingDisabledViolations;
                h.staticBatchingConflicts = sceneReport.staticBatchingConflicts;
                h.negativeScaleViolations = sceneReport.negativeScaleViolations;
                h.lppvViolations = sceneReport.lppvViolations;
                h.probeAnchorViolations = sceneReport.probeAnchorViolations;
                h.customVertexStreamViolations = sceneReport.customVertexStreamViolations;
                h.lodCrossFadeViolations = sceneReport.lodCrossFadeViolations;
                h.shaderIncompatibilityViolations = sceneReport.shaderIncompatibilityViolations;
                h.desktopOverdrawRiskMeshes = sceneReport.desktopOverdrawRiskMeshes;

                if (h.totalRenderers > 0)
                {
                    h.eligibilityRatio = (float)h.eligibleRenderers / h.totalRenderers;
                    h.disqualifiedRatio = (float)h.disqualifiedRenderers / h.totalRenderers;
                }
            }

            if (passFindings != null && passFindings.Count > 0)
            {
                h.totalRenderPasses = passFindings.Count;
                for (int i = 0; i < passFindings.Count; i++)
                {
                    RenderPassMigrationFinding f = passFindings[i];
                    if (f.implementsRecordRenderGraph && !f.implementsLegacyExecute)
                        h.nativeRenderGraphPasses++;
                    else if (f.implementsRecordRenderGraph && f.implementsLegacyExecute)
                        h.dualPathPasses++;
                    else if (f.implementsLegacyExecute)
                        h.legacyOnlyPasses++;
                }

                int migrated = h.nativeRenderGraphPasses + h.dualPathPasses;
                h.renderGraphMigrationRatio = (float)migrated / h.totalRenderPasses;
            }
            else
            {
                h.renderGraphMigrationRatio = 1f;
            }

            return h;
        }

        public static bool IsImmutableBuiltInOrPackageMaterial(Material mat)
        {
            if (mat == null)
                return true;

            if (string.Equals(mat.name, "Default-Material", StringComparison.Ordinal) ||
                string.Equals(mat.name, "Default-Diffuse", StringComparison.Ordinal) ||
                string.Equals(mat.name, "Sprites-Default", StringComparison.Ordinal))
            {
                string builtInPath = AssetDatabase.GetAssetPath(mat);
                if (string.IsNullOrEmpty(builtInPath) ||
                    builtInPath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) ||
                    builtInPath.StartsWith("Library/", StringComparison.OrdinalIgnoreCase))
                {
                    if (EditorUtility.IsPersistent(mat))
                        return true;
                }
            }

            string assetPath = AssetDatabase.GetAssetPath(mat);
            if (!string.IsNullOrEmpty(assetPath) &&
                (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                 assetPath.StartsWith("Resources/unity_builtin_extra", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Automatically fixes Unity 6 GPU Resident Drawer disqualifiers across all sub-meshes of scene MeshRenderers.
        /// Safely skips read-only UPM package and Unity built-in Default-Material assets.
        /// </summary>
        public static SceneAutoFixReport AutoFixSceneRenderers(
            IReadOnlyList<MeshRenderer> renderers,
            Func<GameObject, bool> isStaticBatchingEnabled = null,
            Action<GameObject> clearStaticBatchingFlag = null)
        {
            var report = new SceneAutoFixReport();
            if (renderers == null || renderers.Count == 0)
                return report;

            Func<GameObject, bool> checkStatic = isStaticBatchingEnabled ??
                (go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));

            Action<GameObject> clearStatic = clearStaticBatchingFlag ?? (go =>
            {
                Undo.RecordObject(go, "Clear BatchingStatic for GPU Resident Drawer");
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
                flags &= ~StaticEditorFlags.BatchingStatic;
                GameObjectUtility.SetStaticEditorFlags(go, flags);
            });

            for (int i = 0; i < renderers.Count; i++)
            {
                MeshRenderer mr = renderers[i];
                if (mr == null) continue;

                Undo.RecordObject(mr, "Auto-Fix GPU Resident Drawer Renderer");
                report.totalInspected++;
                bool fixedAny = false;
                string goName = mr.gameObject != null ? mr.gameObject.name : $"MeshRenderer[{i}]";

                if (mr.HasPropertyBlock())
                {
                    mr.SetPropertyBlock(null);
                    report.propertyBlocksCleared++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Cleared MaterialPropertyBlock override.");
                }

                Material[] mats = mr.sharedMaterials;
                if (mats != null && mats.Length > 0)
                {
                    for (int m = 0; m < mats.Length; m++)
                    {
                        Material mat = mats[m];
                        if (mat != null && !mat.enableInstancing && !IsImmutableBuiltInOrPackageMaterial(mat))
                        {
                            Undo.RecordObject(mat, "Enable GPU Instancing");
                            mat.enableInstancing = true;
                            EditorUtility.SetDirty(mat);
                            report.materialsInstancingEnabled++;
                            fixedAny = true;
                            report.appliedFixes.Add($"{goName}: Enabled GPU Instancing on material '{mat.name}'.");
                        }
                    }
                }
                else if (mr.sharedMaterial != null && !mr.sharedMaterial.enableInstancing && !IsImmutableBuiltInOrPackageMaterial(mr.sharedMaterial))
                {
                    Undo.RecordObject(mr.sharedMaterial, "Enable GPU Instancing");
                    mr.sharedMaterial.enableInstancing = true;
                    EditorUtility.SetDirty(mr.sharedMaterial);
                    report.materialsInstancingEnabled++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Enabled GPU Instancing on material '{mr.sharedMaterial.name}'.");
                }

                if (mr.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                {
                    mr.lightProbeUsage = LightProbeUsage.BlendProbes;
                    report.lppvConvertedToBlendProbes++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Switched LightProbeUsage from UseProxyVolume (LPPV) to BlendProbes.");
                }

                if (mr.lightProbeUsage == LightProbeUsage.BlendProbes && mr.probeAnchor != null)
                {
                    mr.probeAnchor = null;
                    report.probeAnchorsCleared++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Cleared MeshRenderer.probeAnchor (BlendProbes + probeAnchor is excluded from GRD).");
                }

                if (mr.gameObject != null && checkStatic(mr.gameObject))
                {
                    clearStatic(mr.gameObject);
                    report.staticBatchingFlagsCleared++;
                    fixedAny = true;
                    report.appliedFixes.Add($"{goName}: Cleared conflicting StaticEditorFlags.BatchingStatic flag.");
                }

                if (fixedAny)
                {
                    EditorUtility.SetDirty(mr);
                    report.renderersFixed++;
                }
            }

            return report;
        }

        /// <summary>
        /// Capture-safe auto-fixer for Unity 6 Render Graph C# passes:
        /// 1. If a non-static SetRenderFunc lambda has zero captures, upgrades directly to 'static'.
        /// 2. If a non-static SetRenderFunc lambda captures outer class fields AND PassData + out passData exist,
        ///    automatically hoists the captured fields into PassData, wires `passData.field = field;`, rewrites
        ///    the lambda body to `data.field`, and marks the lambda `static`!
        /// 3. If the lambda captures `this.` or unhoistable instance methods, refuses to emit broken CS8820 code
        ///    and logs actionable PassData migration guidance instead.
        /// </summary>
        public static RenderPassSourceAutoFixReport AutoFixRenderPassCSharpSource(
            string rawCSharpSource,
            string filePath = "<Memory>")
        {
            string safePath = string.IsNullOrEmpty(filePath) ? "<Memory>" : filePath;
            RenderGraphSourceAuditResult before = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(rawCSharpSource, safePath);

            var report = new RenderPassSourceAutoFixReport
            {
                filePath = safePath,
                patchedSource = rawCSharpSource ?? string.Empty,
                unifiedDiff = string.Empty,
                beforeAudit = before
            };

            if (string.IsNullOrWhiteSpace(rawCSharpSource))
            {
                report.afterAudit = before;
                return report;
            }

            string working = rawCSharpSource;
            Dictionary<string, string> outerFields = ExtractOuterInstanceFieldTypes(working);

            bool[] mask = BuildCSharpCommentAndStringMask(working);
            MatchCollection lambdaMatches = NonStaticSetRenderFuncLambdaRegex.Matches(working);
            int upgradedLambdas = 0;
            var fieldsToAddToPassData = new List<(string type, string name)>();

            // Process from bottom to top so string offsets stay valid
            for (int m = lambdaMatches.Count - 1; m >= 0; m--)
            {
                Match match = lambdaMatches[m];
                if (match.Index < mask.Length && mask[match.Index])
                    continue;

                string paramList = match.Groups[3].Success ? match.Groups[3].Value
                    : (match.Groups[4].Success ? match.Groups[4].Value
                    : (match.Groups[5].Success ? match.Groups[5].Value : string.Empty));
                string dataParam = ExtractFirstParamName(paramList);

                if (!TryGetLambdaBodySpan(working, match.Index + match.Length, out int bodyStart, out int bodyLen))
                    continue;

                string body = working.Substring(bodyStart, bodyLen);
                string sanitizedBody = RenderGraphAssemblyAuditor.SanitizeCSharpCommentsAndStrings(body);

                bool capturesThisExplicitly = Regex.IsMatch(sanitizedBody, @"\bthis\s*\.");
                var capturedFields = new List<(string type, string name)>();
                foreach (var kvp in outerFields)
                {
                    if (string.Equals(kvp.Key, dataParam, StringComparison.Ordinal))
                        continue;
                    if (Regex.IsMatch(sanitizedBody, $@"(?<!\.\s*)\b{Regex.Escape(kvp.Key)}\b"))
                        capturedFields.Add((kvp.Value, kvp.Key));
                }

                if (capturesThisExplicitly)
                {
                    report.skippedUnsafeCaptureLambdaCount++;
                    report.appliedFixes.Add(
                        "Skipped adding 'static' to SetRenderFunc lambda capturing 'this.' directly (prevented CS8820 compile error); move 'this' member state into PassData.");
                    continue;
                }

                if (capturedFields.Count > 0)
                {
                    // Attempt automatic PassData field hoisting if PassData class & `out var passData` exist
                    Match outVarMatch = FindNearestPrecedingOutPassDataVar(working, match.Index);
                    bool hasLocalPassDataClass = PassDataClassFullRegex.IsMatch(working) && !string.IsNullOrEmpty(dataParam);

                    if (!hasLocalPassDataClass || outVarMatch == null)
                    {
                        report.skippedUnsafeCaptureLambdaCount++;
                        report.appliedFixes.Add(
                            $"Skipped adding 'static' to SetRenderFunc lambda capturing [{string.Join(", ", capturedFields.ConvertAll(f => f.name))}] because local PassData class/out variable was not found (prevented CS8820).");
                        continue;
                    }

                    string passDataVar = outVarMatch.Groups[1].Value;
                    string rewrittenBody = body;
                    for (int f = 0; f < capturedFields.Count; f++)
                    {
                        string fName = capturedFields[f].name;
                        rewrittenBody = Regex.Replace(rewrittenBody, $@"(?<!\.\s*)\b{Regex.Escape(fName)}\b", $"{dataParam}.{fName}");
                        if (!fieldsToAddToPassData.Exists(x => x.name == fName))
                            fieldsToAddToPassData.Add(capturedFields[f]);
                    }

                    // Find start of the builder.SetRenderFunc statement line to insert `passData.field = field;` cleanly
                    int stmtLineStart = match.Index;
                    while (stmtLineStart > 0 && working[stmtLineStart - 1] != '\n')
                        stmtLineStart--;
                    int indentEnd = stmtLineStart;
                    while (indentEnd < match.Index && (working[indentEnd] == ' ' || working[indentEnd] == '\t'))
                        indentEnd++;
                    string indent = working.Substring(stmtLineStart, indentEnd - stmtLineStart);

                    var assignSb = new StringBuilder();
                    for (int f = 0; f < capturedFields.Count; f++)
                    {
                        string fName = capturedFields[f].name;
                        assignSb.Append(indent).Append(passDataVar).Append('.').Append(fName).Append(" = ").Append(fName).AppendLine(";");
                        report.hoistedCapturedFieldCount++;
                    }

                    // 1) Replace body first (higher index)
                    working = working.Substring(0, bodyStart) + rewrittenBody + working.Substring(bodyStart + bodyLen);
                    // 2) Upgrade lambda header to `static `
                    string upgradedHeader = match.Groups[1].Value + "static " + match.Groups[2].Value;
                    working = working.Substring(0, match.Index) + upgradedHeader + working.Substring(match.Index + match.Length);
                    // 3) Insert `passData.field = field;` before statement line
                    working = working.Substring(0, stmtLineStart) + assignSb + working.Substring(stmtLineStart);
                    upgradedLambdas++;
                    continue;
                }

                // Zero captures: safe direct `static ` upgrade
                string directStatic = match.Groups[1].Value + "static " + match.Groups[2].Value;
                working = working.Substring(0, match.Index) + directStatic + working.Substring(match.Index + match.Length);
                upgradedLambdas++;
            }

            if (fieldsToAddToPassData.Count > 0)
            {
                working = PassDataClassFullRegex.Replace(working, m =>
                {
                    string existingBody = m.Groups[3].Value;
                    var addSb = new StringBuilder(existingBody);
                    if (!existingBody.EndsWith("\n", StringComparison.Ordinal))
                        addSb.AppendLine();

                    for (int i = 0; i < fieldsToAddToPassData.Count; i++)
                    {
                        var fld = fieldsToAddToPassData[i];
                        if (!Regex.IsMatch(existingBody, $@"\b{Regex.Escape(fld.name)}\b"))
                        {
                            addSb.Append("        public ").Append(fld.type).Append(' ').Append(fld.name).AppendLine(";");
                        }
                    }
                    return m.Groups[1].Value + addSb + "    " + m.Groups[4].Value;
                }, 1);

                report.appliedFixes.Add(
                    $"Hoisted {fieldsToAddToPassData.Count} captured instance field(s) ({string.Join(", ", fieldsToAddToPassData.ConvertAll(f => f.name))}) into PassData for CS8820-safe static execution.");
            }

            if (upgradedLambdas > 0)
            {
                report.appliedFixes.Add(
                    $"Upgraded {upgradedLambdas} non-static SetRenderFunc lambda/delegate callback(s) to 'static' to eliminate per-frame GC closure allocations.");
            }

            // Also upgrade capture-free instance method groups passed to SetRenderFunc(MethodName) -> static void MethodName(...)
            bool[] methodMask = BuildCSharpCommentAndStringMask(working);
            MatchCollection methodGroups = SetRenderFuncMethodGroupCallRegex.Matches(working);
            var candidateMethods = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < methodGroups.Count; i++)
            {
                Match m = methodGroups[i];
                if (m.Index < methodMask.Length && methodMask[m.Index])
                    continue;
                string methodName = m.Groups[1].Value;
                if (!string.Equals(methodName, "static", StringComparison.Ordinal) &&
                    !string.Equals(methodName, "delegate", StringComparison.Ordinal))
                {
                    candidateMethods.Add(methodName);
                }
            }

            int upgradedMethods = 0;
            foreach (string methodName in candidateMethods)
            {
                var declRegex = new Regex(
                    $@"(\b(?:private|protected|public|internal)[ \t]+)?(?<!\bstatic[ \t]+)(\bvoid[ \t]+{Regex.Escape(methodName)}[ \t]*\()",
                    RegexOptions.Compiled);
                bool[] curMask = BuildCSharpCommentAndStringMask(working);
                working = declRegex.Replace(working, m =>
                {
                    if (m.Index < curMask.Length && curMask[m.Index])
                        return m.Value;
                    int checkStart = Math.Max(0, m.Index - 24);
                    string prefixCheck = working.Substring(checkStart, m.Index - checkStart);
                    if (Regex.IsMatch(prefixCheck, @"\bstatic\s*$"))
                        return m.Value;

                    // Verify method body does not reference `this.` or outer instance fields before adding `static`
                    if (TryGetMethodBodyAfterHeader(working, m.Index + m.Length, out string mBody))
                    {
                        string cleanMBody = RenderGraphAssemblyAuditor.SanitizeCSharpCommentsAndStrings(mBody);
                        if (Regex.IsMatch(cleanMBody, @"\bthis\s*\."))
                            return m.Value;
                        foreach (var kvp in outerFields)
                        {
                            if (Regex.IsMatch(cleanMBody, $@"(?<!\.\s*)\b{Regex.Escape(kvp.Key)}\b"))
                                return m.Value;
                        }
                    }

                    upgradedMethods++;
                    string accessMod = m.Groups[1].Success ? m.Groups[1].Value : string.Empty;
                    return accessMod + "static " + m.Groups[2].Value;
                });
            }

            if (upgradedMethods > 0)
            {
                report.appliedFixes.Add(
                    $"Upgraded {upgradedMethods} capture-free instance SetRenderFunc method group target(s) to 'static void'.");
            }

            RenderGraphSourceAuditResult after = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(working, safePath);
            report.patchedSource = working;
            report.modified = !string.Equals(rawCSharpSource, working, StringComparison.Ordinal);
            report.unifiedDiff = report.modified
                ? RenderGraphAssemblyAuditor.GenerateUnifiedDiff(rawCSharpSource, working, safePath)
                : string.Empty;
            report.afterAudit = after;

            return report;
        }

        private static Dictionary<string, string> ExtractOuterInstanceFieldTypes(string source)
        {
            string clean = RenderGraphAssemblyAuditor.SanitizeCSharpCommentsAndStrings(source);
            string withoutPassData = PassDataClassFullRegex.Replace(clean, string.Empty);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            MatchCollection matches = TypedInstanceFieldDeclRegex.Matches(withoutPassData);
            for (int i = 0; i < matches.Count; i++)
            {
                string fType = matches[i].Groups[1].Value.Trim();
                string fName = matches[i].Groups[2].Value.Trim();
                if (string.Equals(fType, "return", StringComparison.Ordinal) ||
                    string.Equals(fType, "using", StringComparison.Ordinal) ||
                    string.Equals(fType, "namespace", StringComparison.Ordinal) ||
                    string.Equals(fType, "class", StringComparison.Ordinal) ||
                    string.Equals(fType, "struct", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(fName) && !map.ContainsKey(fName))
                    map[fName] = fType;
            }
            return map;
        }

        private static string ExtractFirstParamName(string paramList)
        {
            if (string.IsNullOrWhiteSpace(paramList)) return string.Empty;
            string first = paramList.Split(',')[0].Trim();
            string[] toks = first.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return toks.Length > 0 ? toks[toks.Length - 1].Trim() : string.Empty;
        }

        private static Match FindNearestPrecedingOutPassDataVar(string code, int beforeIdx)
        {
            MatchCollection all = OutPassDataVarRegex.Matches(code);
            Match best = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Index < beforeIdx)
                    best = all[i];
            }
            return best;
        }

        private static bool TryGetLambdaBodySpan(string code, int startAfterArrowOrDelegate, out int bodyStart, out int bodyLen)
        {
            bodyStart = 0;
            bodyLen = 0;
            int idx = startAfterArrowOrDelegate;
            while (idx < code.Length && char.IsWhiteSpace(code[idx]))
                idx++;
            if (idx >= code.Length) return false;

            if (code[idx] == '{')
            {
                bodyStart = idx + 1;
                int depth = 1;
                idx++;
                while (idx < code.Length && depth > 0)
                {
                    char c = code[idx++];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                bodyLen = Math.Max(0, idx - bodyStart - 1);
                return true;
            }

            bodyStart = idx;
            int pDepth = 0;
            while (idx < code.Length)
            {
                char c = code[idx];
                if (c == '(') pDepth++;
                else if (c == ')')
                {
                    if (pDepth == 0) break;
                    pDepth--;
                }
                idx++;
            }
            bodyLen = Math.Max(0, idx - bodyStart);
            return bodyLen > 0;
        }

        private static bool TryGetMethodBodyAfterHeader(string code, int afterOpenParen, out string body)
        {
            body = string.Empty;
            int idx = afterOpenParen;
            int pDepth = 1;
            while (idx < code.Length && pDepth > 0)
            {
                char c = code[idx++];
                if (c == '(') pDepth++;
                else if (c == ')') pDepth--;
            }
            while (idx < code.Length && code[idx] != '{' && code[idx] != ';')
                idx++;
            if (idx < code.Length && code[idx] == '{')
            {
                int bStart = idx + 1;
                int bDepth = 1;
                idx++;
                while (idx < code.Length && bDepth > 0)
                {
                    char c = code[idx++];
                    if (c == '{') bDepth++;
                    else if (c == '}') bDepth--;
                }
                body = code.Substring(bStart, Math.Max(0, idx - bStart - 1));
                return true;
            }
            return false;
        }

        public static string SanitizeProjectRelativePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return "Assets";

            string normalized = rawPath.Trim().Replace('\\', '/').Trim('/');
            if (normalized.Contains("..") || Path.IsPathRooted(rawPath.Trim()))
                throw new ArgumentException("Path traversal or rooted paths outside the Unity project are forbidden.");

            if (!normalized.Equals("Assets", StringComparison.Ordinal) &&
                !normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
                !normalized.Equals("Packages", StringComparison.Ordinal) &&
                !normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Target directory must reside under 'Assets' or 'Packages'.");
            }

            return normalized;
        }

        internal static bool[] BuildCSharpCommentAndStringMask(string source)
        {
            int len = source != null ? source.Length : 0;
            var mask = new bool[len];
            int i = 0;
            while (i < len)
            {
                char c = source[i];
                if (c == '/' && i + 1 < len && source[i + 1] == '*')
                {
                    mask[i++] = true;
                    mask[i++] = true;
                    while (i < len)
                    {
                        if (source[i] == '*' && i + 1 < len && source[i + 1] == '/')
                        {
                            mask[i++] = true;
                            mask[i++] = true;
                            break;
                        }
                        mask[i++] = true;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < len && source[i + 1] == '/')
                {
                    while (i < len && source[i] != '\n' && source[i] != '\r')
                        mask[i++] = true;
                    continue;
                }

                if (c == '@' && i + 1 < len && source[i + 1] == '"')
                {
                    mask[i++] = true;
                    mask[i++] = true;
                    while (i < len)
                    {
                        if (source[i] == '"')
                        {
                            mask[i++] = true;
                            if (i < len && source[i] == '"')
                            {
                                mask[i++] = true;
                                continue;
                            }
                            break;
                        }
                        mask[i++] = true;
                    }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    mask[i++] = true;
                    while (i < len)
                    {
                        char sc = source[i];
                        mask[i++] = true;
                        if (sc == '\\' && i < len)
                            mask[i++] = true;
                        else if (sc == quote || sc == '\n' || sc == '\r')
                            break;
                    }
                    continue;
                }

                i++;
            }
            return mask;
        }

        public static string ExportFullMcpReportToJson(
            ResidentDrawerAuditReport sceneReport,
            IReadOnlyList<RenderPassMigrationFinding> passFindings,
            PipelinePrerequisiteAuditResult pipelineReport = null)
        {
            ResidentDrawerHeatmapSummary heatmap = ComputeHeatmapSummary(sceneReport, passFindings);
            PipelinePrerequisiteAuditResult pipe = pipelineReport ?? UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
            var sb = new StringBuilder(2560);
            sb.Append('{');
            AppendJsonProp(sb, "tool", ServerName, true);
            AppendJsonProp(sb, "version", PackageVersion, true);
            sb.Append("\"pipelinePrerequisites\":");
            sb.Append(SerializePipelinePrerequisiteJson(pipe));
            sb.Append(",\"heatmap\":");
            AppendHeatmapJson(sb, heatmap);
            sb.Append(",\"sceneAudit\":");
            AppendSceneReportJson(sb, sceneReport ?? new ResidentDrawerAuditReport());
            sb.Append(",\"renderPassAudit\":[");
            if (passFindings != null)
            {
                for (int i = 0; i < passFindings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RenderPassMigrationFinding f = passFindings[i];
                    sb.Append('{');
                    AppendJsonProp(sb, "typeFullName", f.typeFullName, true);
                    AppendJsonProp(sb, "assemblyName", f.assemblyName, true);
                    AppendJsonProp(sb, "implementsRecordRenderGraph", f.implementsRecordRenderGraph, true);
                    AppendJsonProp(sb, "implementsLegacyExecute", f.implementsLegacyExecute, true);
                    AppendJsonProp(sb, "migrationStatus", f.migrationStatus, false);
                    sb.Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string HandleJsonRpcRequest(string jsonRpcRequest)
        {
            if (string.IsNullOrWhiteSpace(jsonRpcRequest))
                return BuildJsonRpcError("null", -32700, "Parse error: Empty JSON-RPC request.");

            string trimmed = jsonRpcRequest.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                return BuildJsonRpcError("null", -32700, "Parse error: Malformed JSON object.");

            bool hasId = TryGetDirectJsonPropertyValue(trimmed, "id", out string rawIdVal) && !string.IsNullOrEmpty(rawIdVal);
            string idRaw = hasId ? rawIdVal : "null";
            string method = ExtractJsonStringField(trimmed, "method");

            if (string.IsNullOrEmpty(method))
                return BuildJsonRpcError(idRaw, -32600, "Invalid Request: Missing 'method' field.");

            if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal))
                return string.Empty;

            if (string.Equals(method, "ping", StringComparison.Ordinal))
                return BuildJsonRpcResult(idRaw, "{}");

            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                var res = new StringBuilder();
                res.Append("{\"protocolVersion\":\"").Append(McpProtocolVersion).Append("\",");
                res.Append("\"capabilities\":{\"tools\":{\"listChanged\":false},\"resources\":{\"subscribe\":false,\"listChanged\":false}},");
                res.Append("\"serverInfo\":{\"name\":\"").Append(ServerName).Append("\",\"version\":\"").Append(PackageVersion).Append("\"}}");
                return BuildJsonRpcResult(idRaw, res.ToString());
            }

            if (string.Equals(method, "resources/list", StringComparison.Ordinal))
            {
                string resList = "{\"resources\":[{\"uri\":\"" + LatestReportResourceUri +
                                 "\",\"name\":\"Latest PC RenderGraph & GPU Resident Drawer Audit\",\"mimeType\":\"application/json\"}]}";
                return BuildJsonRpcResult(idRaw, resList);
            }

            if (string.Equals(method, "resources/read", StringComparison.Ordinal))
            {
                MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
                ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(
                    renderers,
                    go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));
                List<RenderPassMigrationFinding> passes = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                string reportJson = ExportFullMcpReportToJson(report, passes);
                string resRead = "{\"contents\":[{\"uri\":\"" + LatestReportResourceUri +
                                 "\",\"mimeType\":\"application/json\",\"text\":\"" + EscapeJson(reportJson) + "\"}]}";
                return BuildJsonRpcResult(idRaw, resRead);
            }

            if (string.Equals(method, "tools/list", StringComparison.Ordinal))
            {
                return BuildJsonRpcResult(idRaw, BuildToolsListJson());
            }

            if (string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                string paramsObj = ExtractJsonObjectField(trimmed, "params") ?? trimmed;
                string toolName = ExtractJsonStringField(paramsObj, "name");
                if (string.IsNullOrEmpty(toolName))
                    return BuildJsonRpcError(idRaw, -32602, "Invalid params: Missing tool 'name' in tools/call.");

                string argsObj = ExtractJsonObjectField(paramsObj, "arguments") ?? paramsObj;

                try
                {
                    if (string.Equals(toolName, "rendergraph_doctor.audit_scene_renderers", StringComparison.Ordinal))
                    {
                        MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
                        ResidentDrawerAuditReport report = GpuResidentDrawerAuditor.AuditSceneRenderers(
                            renderers,
                            go => GameObjectUtility.AreStaticEditorFlagsSet(go, StaticEditorFlags.BatchingStatic));
                        List<RenderPassMigrationFinding> passes = RenderGraphAssemblyAuditor.AuditAllLoadedRenderPasses();
                        string payload = ExportFullMcpReportToJson(report, passes);
                        return BuildJsonRpcToolCallResult(idRaw, payload);
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.autofix_scene_renderers", StringComparison.Ordinal))
                    {
                        MeshRenderer[] renderers = GpuResidentDrawerAuditor.FindAllActiveMeshRenderers();
                        SceneAutoFixReport fixReport = AutoFixSceneRenderers(renderers);
                        return BuildJsonRpcToolCallResult(idRaw, SerializeSceneAutoFixJson(fixReport));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.audit_pipeline_prerequisites", StringComparison.Ordinal))
                    {
                        PipelinePrerequisiteAuditResult pipe = UrpPipelinePrerequisiteDoctor.AuditPipelinePrerequisites();
                        return BuildJsonRpcToolCallResult(idRaw, SerializePipelinePrerequisiteJson(pipe));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.autoconfigure_pipeline_prerequisites", StringComparison.Ordinal))
                    {
                        PipelinePrerequisiteAuditResult pipe = UrpPipelinePrerequisiteDoctor.AutoConfigurePipelineForResidentDrawer();
                        return BuildJsonRpcToolCallResult(idRaw, SerializePipelinePrerequisiteJson(pipe));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.audit_project_prefabs", StringComparison.Ordinal))
                    {
                        string folder = SanitizeProjectRelativePath(ExtractJsonStringField(argsObj, "rootDirectory") ?? "Assets");
                        ResidentDrawerAuditReport prefabReport = UrpPipelinePrerequisiteDoctor.AuditProjectPrefabsForResidentDrawer(folder);
                        return BuildJsonRpcToolCallResult(idRaw, SerializeSceneReportJson(prefabReport));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.audit_shader_brg", StringComparison.Ordinal))
                    {
                        string source = ExtractJsonStringField(argsObj, "source");
                        string assetPath = ExtractJsonStringField(argsObj, "assetPath") ?? "<McpInlineShader.shader>";
                        ShaderResidentDrawerAudit shaderAudit = GpuResidentDrawerAuditor.AuditShaderSourceForResidentDrawer(source ?? string.Empty, assetPath);
                        return BuildJsonRpcToolCallResult(idRaw, SerializeShaderBrgAuditJson(shaderAudit));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.audit_renderpass_csharp", StringComparison.Ordinal))
                    {
                        string source = ExtractJsonStringField(argsObj, "source");
                        string filePath = ExtractJsonStringField(argsObj, "filePath") ?? "<McpInlinePass.cs>";
                        RenderGraphSourceAuditResult passAudit = RenderGraphAssemblyAuditor.AnalyzeRenderPassCSharpSource(source ?? string.Empty, filePath);
                        return BuildJsonRpcToolCallResult(idRaw, SerializePassSourceAuditJson(passAudit));
                    }

                    if (string.Equals(toolName, "rendergraph_doctor.autofix_renderpass_csharp", StringComparison.Ordinal))
                    {
                        string source = ExtractJsonStringField(argsObj, "source");
                        string filePath = ExtractJsonStringField(argsObj, "filePath") ?? "<McpInlinePass.cs>";
                        RenderPassSourceAutoFixReport fixReport = AutoFixRenderPassCSharpSource(source ?? string.Empty, filePath);
                        return BuildJsonRpcToolCallResult(idRaw, SerializePassAutoFixJson(fixReport));
                    }
                }
                catch (ArgumentException ex)
                {
                    return BuildJsonRpcError(idRaw, -32602, $"Invalid params: {ex.Message}");
                }

                return BuildJsonRpcError(idRaw, -32602, $"Unknown tool '{EscapeJson(toolName)}'.");
            }

            return BuildJsonRpcError(idRaw, -32601, $"Method not found: '{EscapeJson(method)}'.");
        }

        public static string SerializePipelinePrerequisiteJson(PipelinePrerequisiteAuditResult p)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonProp(sb, "hasActiveSrpAsset", p.hasActiveSrpAsset, true);
            AppendJsonProp(sb, "pipelineAssetName", p.pipelineAssetName, true);
            AppendJsonProp(sb, "pipelineTypeName", p.pipelineTypeName, true);
            AppendJsonProp(sb, "srpBatcherEnabled", p.srpBatcherEnabled, true);
            AppendJsonProp(sb, "gpuResidentDrawerEnabled", p.gpuResidentDrawerEnabled, true);
            AppendJsonProp(sb, "rendererRenderingMode", p.rendererRenderingMode, true);
            AppendJsonProp(sb, "isForwardPlusOrDeferredPlus", p.isForwardPlusOrDeferredPlus, true);
            AppendJsonProp(sb, "brgKeepAllStrippingConfigured", p.brgKeepAllStrippingConfigured, true);
            AppendJsonProp(sb, "isFullyReadyForResidentDrawer", p.isFullyReadyForResidentDrawer, true);
            sb.Append("\"issues\":[");
            if (p.issues != null)
            {
                for (int i = 0; i < p.issues.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(p.issues[i])).Append('"');
                }
            }
            sb.Append("],\"appliedFixes\":[");
            if (p.appliedFixes != null)
            {
                for (int i = 0; i < p.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(p.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializeSceneReportJson(ResidentDrawerAuditReport r)
        {
            var sb = new StringBuilder(1024);
            AppendSceneReportJson(sb, r ?? new ResidentDrawerAuditReport());
            return sb.ToString();
        }

        public static string SerializeSceneAutoFixJson(SceneAutoFixReport r)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendJsonProp(sb, "totalInspected", r.totalInspected, true);
            AppendJsonProp(sb, "renderersFixed", r.renderersFixed, true);
            AppendJsonProp(sb, "propertyBlocksCleared", r.propertyBlocksCleared, true);
            AppendJsonProp(sb, "materialsInstancingEnabled", r.materialsInstancingEnabled, true);
            AppendJsonProp(sb, "lppvConvertedToBlendProbes", r.lppvConvertedToBlendProbes, true);
            AppendJsonProp(sb, "probeAnchorsCleared", r.probeAnchorsCleared, true);
            AppendJsonProp(sb, "staticBatchingFlagsCleared", r.staticBatchingFlagsCleared, true);
            sb.Append("\"appliedFixes\":[");
            if (r.appliedFixes != null)
            {
                for (int i = 0; i < r.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializeShaderBrgAuditJson(ShaderResidentDrawerAudit a)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonProp(sb, "shaderName", a.shaderName, true);
            AppendJsonProp(sb, "assetPath", a.assetPath, true);
            AppendJsonProp(sb, "hasTarget45OrHigher", a.hasTarget45OrHigher, true);
            AppendJsonProp(sb, "hasDotsInstancingPragma", a.hasDotsInstancingPragma, true);
            AppendJsonProp(sb, "hasSrpBatcherCBuffer", a.hasSrpBatcherCBuffer, true);
            AppendJsonProp(sb, "hasDotsInstancedProps", a.hasDotsInstancedProps, true);
            AppendJsonProp(sb, "isCompatibleWithResidentDrawer", a.isCompatibleWithResidentDrawer, true);
            AppendJsonProp(sb, "diagnosticMessage", a.diagnosticMessage, false);
            sb.Append('}');
            return sb.ToString();
        }

        public static string SerializePassSourceAuditJson(RenderGraphSourceAuditResult a)
        {
            var sb = new StringBuilder(768);
            sb.Append('{');
            AppendJsonProp(sb, "filePath", a.filePath, true);
            AppendJsonProp(sb, "usesRecordRenderGraph", a.usesRecordRenderGraph, true);
            AppendJsonProp(sb, "usesAddRasterRenderPass", a.usesAddRasterRenderPass, true);
            AppendJsonProp(sb, "usesAddUnsafePass", a.usesAddUnsafePass, true);
            AppendJsonProp(sb, "usesStaticRenderFuncLambda", a.usesStaticRenderFuncLambda, true);
            AppendJsonProp(sb, "hasNonStaticRenderFuncClosureRisk", a.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "hasInstanceCaptureInRenderFunc", a.hasInstanceCaptureInRenderFunc, true);
            AppendJsonProp(sb, "hasStalePooledPassDataHandleRisk", a.hasStalePooledPassDataHandleRisk, true);
            AppendJsonProp(sb, "hasSimultaneousReadWriteTextureHazard", a.hasSimultaneousReadWriteTextureHazard, true);
            AppendJsonProp(sb, "hasUnusedOrUnboundMaterialHazard", a.hasUnusedOrUnboundMaterialHazard, true);
            AppendJsonProp(sb, "hasManualTextureDescHazard", a.hasManualTextureDescHazard, true);
            AppendJsonProp(sb, "canSimplifyToCopyHelper", a.canSimplifyToCopyHelper, true);
            AppendJsonProp(sb, "canSimplifyFullscreenBlitPass", a.canSimplifyFullscreenBlitPass, true);
            AppendJsonProp(sb, "hasUnsafeGlobalTextureHazard", a.hasUnsafeGlobalTextureHazard, true);
            AppendJsonProp(sb, "hasMissingConfigureInputDeclaration", a.hasMissingConfigureInputDeclaration, true);
            AppendJsonProp(sb, "usesLegacyCommandBufferPoolInGraph", a.usesLegacyCommandBufferPoolInGraph, true);
            AppendJsonProp(sb, "usesLegacyGetTemporaryRtInGraph", a.usesLegacyGetTemporaryRtInGraph, true);
            sb.Append("\"findings\":[");
            if (a.findings != null)
            {
                for (int i = 0; i < a.findings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(a.findings[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string SerializePassAutoFixJson(RenderPassSourceAutoFixReport r)
        {
            var sb = new StringBuilder(1280);
            sb.Append('{');
            AppendJsonProp(sb, "filePath", r.filePath, true);
            AppendJsonProp(sb, "modified", r.modified, true);
            AppendJsonProp(sb, "hoistedCapturedFieldCount", r.hoistedCapturedFieldCount, true);
            AppendJsonProp(sb, "skippedUnsafeCaptureLambdaCount", r.skippedUnsafeCaptureLambdaCount, true);
            AppendJsonProp(sb, "hadClosureRiskBefore", r.beforeAudit != null && r.beforeAudit.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "hasClosureRiskAfter", r.afterAudit != null && r.afterAudit.hasNonStaticRenderFuncClosureRisk, true);
            AppendJsonProp(sb, "patchedSource", r.patchedSource, true);
            AppendJsonProp(sb, "unifiedDiff", r.unifiedDiff, true);
            sb.Append("\"appliedFixes\":[");
            if (r.appliedFixes != null)
            {
                for (int i = 0; i < r.appliedFixes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(r.appliedFixes[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildToolsListJson()
        {
            return "{\"tools\":[" +
                "{\"name\":\"rendergraph_doctor.audit_scene_renderers\",\"description\":\"Audits scene MeshRenderers across all sub-meshes and Unity 6 GRDExclusionReason rules, plus pipeline prerequisites and user ScriptableRenderPass assemblies.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.autofix_scene_renderers\",\"description\":\"Fixes disqualified scene MeshRenderers (clears MaterialPropertyBlocks & probeAnchor conflicts, enables GPU Instancing on sub-mesh materials, converts LPPV to BlendProbes, clears BatchingStatic).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.audit_pipeline_prerequisites\",\"description\":\"Audits active URP 17 / HDRP asset for Forward+ / Deferred+ rendering mode, SRP Batcher, InstancedDrawing GRD mode, and KeepAll BRG shader stripping.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.autoconfigure_pipeline_prerequisites\",\"description\":\"Undo-safely configures active URP 17 asset (Forward+/Deferred+, SRP Batcher, InstancedDrawing) and KeepAll BRG shader stripping.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}," +
                "{\"name\":\"rendergraph_doctor.audit_project_prefabs\",\"description\":\"Scans all Prefab assets under Assets/ for Unity 6 GPU Resident Drawer disqualifiers.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"rootDirectory\":{\"type\":\"string\"}}}}," +
                "{\"name\":\"rendergraph_doctor.audit_shader_brg\",\"description\":\"Audits ShaderLab/HLSL source code for Unity 6 GPU Resident Drawer requirements (#pragma target 4.5, DOTS_INSTANCING_ON, UnityPerMaterial CBUFFER).\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"assetPath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}," +
                "{\"name\":\"rendergraph_doctor.audit_renderpass_csharp\",\"description\":\"Statically analyzes C# ScriptableRenderPass/ScriptableRendererFeature source code against all 8 official Unity 6 URP Render Graph rules.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"filePath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}," +
                "{\"name\":\"rendergraph_doctor.autofix_renderpass_csharp\",\"description\":\"Capture-safely upgrades SetRenderFunc lambdas to static (automatically hoisting captured fields into PassData or preventing CS8820 compile errors) and generates a unified diff.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"filePath\":{\"type\":\"string\"}},\"required\":[\"source\"]}}" +
                "]}";
        }

        private static void AppendHeatmapJson(StringBuilder sb, ResidentDrawerHeatmapSummary h)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalRenderers", h.totalRenderers, true);
            AppendJsonProp(sb, "eligibleRenderers", h.eligibleRenderers, true);
            AppendJsonProp(sb, "disqualifiedRenderers", h.disqualifiedRenderers, true);
            AppendJsonProp(sb, "eligibilityRatio", h.eligibilityRatio, true);
            AppendJsonProp(sb, "disqualifiedRatio", h.disqualifiedRatio, true);
            AppendJsonProp(sb, "propertyBlockViolations", h.propertyBlockViolations, true);
            AppendJsonProp(sb, "instancingDisabledViolations", h.instancingDisabledViolations, true);
            AppendJsonProp(sb, "staticBatchingConflicts", h.staticBatchingConflicts, true);
            AppendJsonProp(sb, "negativeScaleViolations", h.negativeScaleViolations, true);
            AppendJsonProp(sb, "lppvViolations", h.lppvViolations, true);
            AppendJsonProp(sb, "probeAnchorViolations", h.probeAnchorViolations, true);
            AppendJsonProp(sb, "customVertexStreamViolations", h.customVertexStreamViolations, true);
            AppendJsonProp(sb, "lodCrossFadeViolations", h.lodCrossFadeViolations, true);
            AppendJsonProp(sb, "shaderIncompatibilityViolations", h.shaderIncompatibilityViolations, true);
            AppendJsonProp(sb, "desktopOverdrawRiskMeshes", h.desktopOverdrawRiskMeshes, true);
            AppendJsonProp(sb, "totalRenderPasses", h.totalRenderPasses, true);
            AppendJsonProp(sb, "nativeRenderGraphPasses", h.nativeRenderGraphPasses, true);
            AppendJsonProp(sb, "dualPathPasses", h.dualPathPasses, true);
            AppendJsonProp(sb, "legacyOnlyPasses", h.legacyOnlyPasses, true);
            AppendJsonProp(sb, "renderGraphMigrationRatio", h.renderGraphMigrationRatio, false);
            sb.Append('}');
        }

        private static void AppendSceneReportJson(StringBuilder sb, ResidentDrawerAuditReport r)
        {
            sb.Append('{');
            AppendJsonProp(sb, "totalMeshRenderers", r.totalMeshRenderers, true);
            AppendJsonProp(sb, "eligibleRenderers", r.eligibleRenderers, true);
            AppendJsonProp(sb, "disqualifiedRenderers", r.disqualifiedRenderers, true);
            sb.Append("\"records\":[");
            if (r.records != null)
            {
                for (int i = 0; i < r.records.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    RendererEligibilityRecord rec = r.records[i];
#if UNITY_6000_4_OR_NEWER
                    string idToken = rec.entityId.ToString();
#else
                    string idToken = rec.instanceId.ToString(CultureInfo.InvariantCulture);
#endif
                    sb.Append('{');
                    AppendJsonProp(sb, "gameObjectName", rec.gameObjectName, true);
                    AppendJsonProp(sb, "assetOrScenePath", rec.assetOrScenePath, true);
                    AppendJsonProp(sb, "entityId", idToken, true);
                    AppendJsonProp(sb, "materialName", rec.materialName, true);
                    AppendJsonProp(sb, "shaderName", rec.shaderName, true);
                    AppendJsonProp(sb, "subMeshCount", rec.subMeshCount, true);
                    AppendJsonProp(sb, "triangleCount", rec.triangleCount, true);
                    AppendJsonProp(sb, "isEligible", rec.IsEligible, true);
                    AppendJsonProp(sb, "disqualifiers", rec.disqualifiers.ToString(), true);
                    AppendJsonProp(sb, "recommendation", rec.recommendation, false);
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private static string BuildJsonRpcToolCallResult(string idRaw, string structuredJsonPayload)
        {
            var sb = new StringBuilder(structuredJsonPayload.Length + 256);
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":\"")
              .Append(EscapeJson(structuredJsonPayload))
              .Append("\"}],\"structuredContent\":")
              .Append(structuredJsonPayload)
              .Append(",\"isError\":false}");
            return BuildJsonRpcResult(idRaw, sb.ToString());
        }

        private static string BuildJsonRpcResult(string idRaw, string resultJson)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}";
        }

        private static string BuildJsonRpcError(string idRaw, int code, string message)
        {
            return $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":{code},\"message\":\"{EscapeJson(message)}\"}}}}";
        }

        private static void AppendJsonProp(StringBuilder sb, string key, string val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":\"").Append(EscapeJson(val ?? string.Empty)).Append('"');
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, int val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val);
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, float val, bool trailingComma)
        {
            float safe = (float.IsNaN(val) || float.IsInfinity(val)) ? 0f : val;
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(safe.ToString("F3", CultureInfo.InvariantCulture));
            if (trailingComma) sb.Append(',');
        }

        private static void AppendJsonProp(StringBuilder sb, string key, bool val, bool trailingComma)
        {
            sb.Append('"').Append(EscapeJson(key)).Append("\":").Append(val ? "true" : "false");
            if (trailingComma) sb.Append(',');
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string ExtractJsonObjectField(string json, string fieldName)
        {
            if (TryGetDirectJsonPropertyValue(json, fieldName, out string rawVal) &&
                !string.IsNullOrEmpty(rawVal) &&
                rawVal[0] == '{')
            {
                return rawVal;
            }
            return null;
        }

        internal static string ExtractJsonStringField(string json, string fieldName)
        {
            if (TryGetDirectJsonPropertyValue(json, fieldName, out string rawVal) &&
                !string.IsNullOrEmpty(rawVal) &&
                rawVal.Length >= 2 &&
                rawVal[0] == '"' &&
                rawVal[rawVal.Length - 1] == '"')
            {
                return UnescapeJsonString(rawVal.Substring(1, rawVal.Length - 2));
            }
            return null;
        }

        internal static bool TryGetDirectJsonPropertyValue(string jsonObject, string propertyName, out string rawValue)
        {
            rawValue = null;
            if (string.IsNullOrWhiteSpace(jsonObject) || string.IsNullOrEmpty(propertyName))
                return false;

            string s = jsonObject.Trim();
            if (s.Length < 2 || s[0] != '{')
                return false;

            int i = 1;
            int len = s.Length;
            while (i < len)
            {
                while (i < len && (char.IsWhiteSpace(s[i]) || s[i] == ','))
                    i++;
                if (i >= len || s[i] == '}')
                    break;

                if (s[i] != '"')
                    break;

                int keyStart = i + 1;
                i = SkipJsonStringLiteral(s, i);
                int keyEnd = Math.Max(keyStart, i - 1);
                string key = UnescapeJsonString(s.Substring(keyStart, keyEnd - keyStart));

                while (i < len && char.IsWhiteSpace(s[i]))
                    i++;
                if (i >= len || s[i] != ':')
                    break;
                i++;

                while (i < len && char.IsWhiteSpace(s[i]))
                    i++;
                if (i >= len)
                    break;

                int valStart = i;
                i = SkipJsonValue(s, i);
                int valEnd = i;

                if (string.Equals(key, propertyName, StringComparison.Ordinal))
                {
                    rawValue = s.Substring(valStart, valEnd - valStart).Trim();
                    return true;
                }
            }
            return false;
        }

        private static int SkipJsonStringLiteral(string s, int quoteStartIdx)
        {
            int i = quoteStartIdx + 1;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                    i++;
                else if (c == '"')
                    break;
            }
            return i;
        }

        private static int SkipJsonValue(string s, int valStartIdx)
        {
            int i = valStartIdx;
            if (i >= s.Length)
                return i;

            char c = s[i];
            if (c == '"')
                return SkipJsonStringLiteral(s, i);

            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char ch = s[i];
                    if (ch == '"')
                    {
                        i = SkipJsonStringLiteral(s, i);
                        continue;
                    }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                            return i + 1;
                    }
                    i++;
                }
                return i;
            }

            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i]))
                i++;
            return i;
        }

        private static string UnescapeJsonString(string escaped)
        {
            if (string.IsNullOrEmpty(escaped)) return string.Empty;
            var sb = new StringBuilder(escaped.Length);
            for (int i = 0; i < escaped.Length; i++)
            {
                char c = escaped[i];
                if (c == '\\' && i + 1 < escaped.Length)
                {
                    char next = escaped[++i];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < escaped.Length &&
                                int.TryParse(escaped.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(next); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
