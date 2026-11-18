using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    [Serializable]
    public struct RenderPassMigrationFinding
    {
        public string typeFullName;
        public string assemblyName;
        public bool implementsRecordRenderGraph;
        public bool implementsLegacyExecute;
        public string migrationStatus;
    }

    [Serializable]
    public sealed class RenderGraphSourceAuditResult
    {
        public string filePath;
        public bool usesRecordRenderGraph;
        public bool usesAddRasterRenderPass;
        public bool usesAddUnsafePass;
        public bool usesStaticRenderFuncLambda;
        public bool hasNonStaticRenderFuncClosureRisk;
        public bool usesLegacyCommandBufferPoolInGraph;
        public bool usesLegacyGetTemporaryRtInGraph;
        public List<string> findings = new List<string>();
    }

    /// <summary>
    /// Scans loaded assemblies via reflection for custom ScriptableRenderPass implementations
    /// and statically analyzes C# Render Graph pass source files for Unity 6 best practices
    /// (static SetRenderFunc lambdas, AddRasterRenderPass vs AddUnsafePass, TextureHandle lifecycle).
    /// </summary>
    public static class RenderGraphAssemblyAuditor
    {
        private static readonly Regex RecordRenderGraphRegex = new Regex(@"\bvoid\s+RecordRenderGraph\s*\(", RegexOptions.Compiled);
        private static readonly Regex AddRasterPassRegex = new Regex(@"\.\s*AddRasterRenderPass\s*<", RegexOptions.Compiled);
        private static readonly Regex AddUnsafePassRegex = new Regex(@"\.\s*AddUnsafePass\s*<", RegexOptions.Compiled);
        private static readonly Regex SetRenderFuncLambdaRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*(static\b)?\s*(?:\([^)]*\)\s*=>|[A-Za-z_]\w*\s*=>|delegate\b)",
            RegexOptions.Compiled);
        private static readonly Regex SetRenderFuncMethodGroupRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex CommandBufferPoolRegex = new Regex(@"\bCommandBufferPool\s*\.\s*Get\b", RegexOptions.Compiled);
        private static readonly Regex GetTemporaryRtRegex = new Regex(@"\bRenderTexture\s*\.\s*GetTemporary\b", RegexOptions.Compiled);

        public static List<RenderPassMigrationFinding> AuditAllLoadedRenderPasses()
        {
            var findings = new List<RenderPassMigrationFinding>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int a = 0; a < assemblies.Length; a++)
            {
                Assembly asm = assemblies[a];
                string asmName = asm.GetName().Name;

                if (asmName.StartsWith("System", StringComparison.Ordinal) ||
                    asmName.StartsWith("mscorlib", StringComparison.Ordinal) ||
                    asmName.StartsWith("Mono.", StringComparison.Ordinal) ||
                    asmName.StartsWith("nunit.", StringComparison.Ordinal))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }

                if (types == null) continue;

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type == null || type.IsAbstract || !IsSubclassOfByName(type, "ScriptableRenderPass"))
                        continue;

                    findings.Add(AnalyzeRenderPassType(type));
                }
            }

            findings.Sort((x, y) => x.implementsRecordRenderGraph.CompareTo(y.implementsRecordRenderGraph));
            return findings;
        }

        public static RenderPassMigrationFinding AnalyzeRenderPassType(Type passType)
        {
            if (passType == null)
                throw new ArgumentNullException(nameof(passType));

            bool hasRecordRenderGraph = DeclaresMethodOverride(passType, "RecordRenderGraph");
            bool hasLegacyExecute = DeclaresMethodOverride(passType, "Execute");

            string status;
            if (hasRecordRenderGraph && !hasLegacyExecute)
            {
                status = "NATIVE RENDER GRAPH: Fully migrated to Unity 6 RecordRenderGraph.";
            }
            else if (hasRecordRenderGraph && hasLegacyExecute)
            {
                status = "DUAL-PATH COMPATIBLE: Implements both RecordRenderGraph (Unity 6) and Execute (2022 LTS).";
            }
            else if (hasLegacyExecute)
            {
                status = "LEGACY COMPATIBILITY ONLY: Missing RecordRenderGraph override! Will fail or force Render Graph Compatibility Mode in Unity 6.";
            }
            else
            {
                status = "NO PASS ENTRYPOINT OVERRIDDEN";
            }

            return new RenderPassMigrationFinding
            {
                typeFullName = passType.FullName ?? passType.Name,
                assemblyName = passType.Assembly.GetName().Name,
                implementsRecordRenderGraph = hasRecordRenderGraph,
                implementsLegacyExecute = hasLegacyExecute,
                migrationStatus = status
            };
        }

        /// <summary>
        /// Statically analyzes C# source code of a ScriptableRenderPass or ScriptableRendererFeature
        /// for Unity 6 Render Graph performance and memory hazards.
        /// </summary>
        public static RenderGraphSourceAuditResult AnalyzeRenderPassCSharpSource(string rawCSharpSource, string filePath = "<Memory>")
        {
            var res = new RenderGraphSourceAuditResult
            {
                filePath = filePath ?? "<Memory>"
            };

            if (string.IsNullOrWhiteSpace(rawCSharpSource))
                return res;

            string uncommentedNoStrings = SanitizeCSharpCommentsAndStrings(rawCSharpSource);
            string code = StripDisabledPreprocessorBlocks(uncommentedNoStrings);

            res.usesRecordRenderGraph = RecordRenderGraphRegex.IsMatch(code);

            // Scope Render Graph checks to the body of RecordRenderGraph so dual-path passes
            // that also implement legacy Execute() are not falsely flagged for CommandBufferPool/GetTemporary in Execute()
            string graphScopeCode = res.usesRecordRenderGraph
                ? ExtractMethodBodies(code, RecordRenderGraphRegex)
                : code;

            res.usesAddRasterRenderPass = AddRasterPassRegex.IsMatch(graphScopeCode);
            res.usesAddUnsafePass = AddUnsafePassRegex.IsMatch(graphScopeCode);
            res.usesLegacyCommandBufferPoolInGraph = res.usesRecordRenderGraph && CommandBufferPoolRegex.IsMatch(graphScopeCode);
            res.usesLegacyGetTemporaryRtInGraph = res.usesRecordRenderGraph && GetTemporaryRtRegex.IsMatch(graphScopeCode);

            MatchCollection renderFuncs = SetRenderFuncLambdaRegex.Matches(graphScopeCode);
            for (int i = 0; i < renderFuncs.Count; i++)
            {
                if (renderFuncs[i].Groups[1].Success)
                {
                    res.usesStaticRenderFuncLambda = true;
                }
                else
                {
                    res.hasNonStaticRenderFuncClosureRisk = true;
                }
            }

            MatchCollection methodGroups = SetRenderFuncMethodGroupRegex.Matches(graphScopeCode);
            for (int i = 0; i < methodGroups.Count; i++)
            {
                string methodName = methodGroups[i].Groups[1].Value;
                if (string.Equals(methodName, "static", StringComparison.Ordinal) ||
                    string.Equals(methodName, "delegate", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsMethodDeclaredStatic(code, methodName))
                {
                    res.usesStaticRenderFuncLambda = true;
                }
                else
                {
                    res.hasNonStaticRenderFuncClosureRisk = true;
                }
            }

            if (res.hasNonStaticRenderFuncClosureRisk)
            {
                res.findings.Add(
                    "GC CLOSURE ALLOCATION RISK: SetRenderFunc callback is a non-static lambda or instance method group. Use 'static (PassData data, RasterGraphContext ctx) => ...' or a 'static void' method group to prevent per-frame delegate/closure GC allocations.");
            }

            if (res.usesAddUnsafePass)
            {
                res.findings.Add(
                    "UNSAFE PASS OVERHEAD: AddUnsafePass disables automatic Render Graph native pass merging. Prefer AddRasterRenderPass or AddComputePass unless modifying global state.");
            }

            if (res.usesLegacyCommandBufferPoolInGraph)
            {
                res.findings.Add(
                    "LEGACY COMMANDBUFFER POOL IN RENDER GRAPH: Avoid CommandBufferPool.Get() inside RecordRenderGraph; use context.cmd (RasterCommandBuffer) directly inside SetRenderFunc.");
            }

            if (res.usesLegacyGetTemporaryRtInGraph)
            {
                res.findings.Add(
                    "UNMANAGED TEMPORARY RT: Avoid RenderTexture.GetTemporary() inside RecordRenderGraph; use renderGraph.CreateTexture() or UniversalResourceData handles so Render Graph can alias memory.");
            }

            if (res.findings.Count == 0 && res.usesRecordRenderGraph)
            {
                res.findings.Add("Clean Unity 6 Render Graph implementation.");
            }

            return res;
        }

        private static bool IsMethodDeclaredStatic(string cleanClassCode, string methodName)
        {
            if (string.IsNullOrEmpty(methodName))
                return false;

            var staticMethodPattern = new Regex(
                $@"\bstatic\b[^{{;]*?\bvoid\s+{Regex.Escape(methodName)}\s*\(",
                RegexOptions.Singleline);
            if (staticMethodPattern.IsMatch(cleanClassCode))
                return true;

            var instanceMethodPattern = new Regex(
                $@"\bvoid\s+{Regex.Escape(methodName)}\s*\(",
                RegexOptions.Singleline);
            if (instanceMethodPattern.IsMatch(cleanClassCode))
                return false;

            // External static helper (e.g. SomeClass.ExecutePass) defaults to true if qualified
            return true;
        }

        private static string ExtractMethodBodies(string code, Regex methodHeaderRegex)
        {
            MatchCollection matches = methodHeaderRegex.Matches(code);
            if (matches.Count == 0)
                return code;

            var sb = new StringBuilder();
            for (int m = 0; m < matches.Count; m++)
            {
                int idx = matches[m].Index + matches[m].Length;
                int parenDepth = 1;
                while (idx < code.Length && parenDepth > 0)
                {
                    char c = code[idx++];
                    if (c == '(') parenDepth++;
                    else if (c == ')') parenDepth--;
                }

                while (idx < code.Length && code[idx] != '{' && code[idx] != ';')
                    idx++;

                if (idx < code.Length && code[idx] == '{')
                {
                    int bodyStart = idx + 1;
                    int braceDepth = 1;
                    idx++;
                    while (idx < code.Length && braceDepth > 0)
                    {
                        char c = code[idx++];
                        if (c == '{') braceDepth++;
                        else if (c == '}') braceDepth--;
                    }
                    int len = Math.Max(0, idx - bodyStart - 1);
                    sb.AppendLine(code.Substring(bodyStart, len));
                }
            }

            return sb.Length > 0 ? sb.ToString() : code;
        }

        private static string SanitizeCSharpCommentsAndStrings(string rawSource)
        {
            if (string.IsNullOrEmpty(rawSource))
                return string.Empty;

            int len = rawSource.Length;
            var sb = new StringBuilder(len);
            int i = 0;

            while (i < len)
            {
                char c = rawSource[i];

                // Block comment /* ... */
                if (c == '/' && i + 1 < len && rawSource[i + 1] == '*')
                {
                    sb.Append(' ');
                    i += 2;
                    while (i < len)
                    {
                        if (rawSource[i] == '*' && i + 1 < len && rawSource[i + 1] == '/')
                        {
                            i += 2;
                            break;
                        }
                        if (rawSource[i] == '\n')
                            sb.Append('\n');
                        i++;
                    }
                    continue;
                }

                // Line comment // ...
                if (c == '/' && i + 1 < len && rawSource[i + 1] == '/')
                {
                    i += 2;
                    while (i < len && rawSource[i] != '\n' && rawSource[i] != '\r')
                        i++;
                    continue;
                }

                // Verbatim string literal @"..."
                if (c == '@' && i + 1 < len && rawSource[i + 1] == '"')
                {
                    sb.Append("\"\"");
                    i += 2;
                    while (i < len)
                    {
                        if (rawSource[i] == '"')
                        {
                            if (i + 1 < len && rawSource[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        if (rawSource[i] == '\n')
                            sb.Append('\n');
                        i++;
                    }
                    continue;
                }

                // Regular string literal "..."
                if (c == '"')
                {
                    sb.Append("\"\"");
                    i++;
                    while (i < len)
                    {
                        char sc = rawSource[i++];
                        if (sc == '\\' && i < len)
                        {
                            i++;
                        }
                        else if (sc == '"' || sc == '\n' || sc == '\r')
                        {
                            break;
                        }
                    }
                    continue;
                }

                // Character literal '...'
                if (c == '\'')
                {
                    sb.Append("''");
                    i++;
                    while (i < len)
                    {
                        char cc = rawSource[i++];
                        if (cc == '\\' && i < len)
                        {
                            i++;
                        }
                        else if (cc == '\'' || cc == '\n' || cc == '\r')
                        {
                            break;
                        }
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        private static string StripDisabledPreprocessorBlocks(string source)
        {
            if (string.IsNullOrEmpty(source))
                return string.Empty;

            var sb = new StringBuilder(source.Length);
            var stack = new Stack<(bool parentActive, bool branchActive, bool seenActive)>();
            bool currentlyActive = true;

            using (var reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        string directive = trimmed.Substring(1).TrimStart();
                        if (directive.StartsWith("if", StringComparison.Ordinal) &&
                            (directive.Length == 2 || char.IsWhiteSpace(directive[2]) || directive[2] == '('))
                        {
                            string expr = directive.Substring(2).Trim().Trim('(', ')').Trim();
                            bool cond = !string.Equals(expr, "0", StringComparison.Ordinal) &&
                                        !string.Equals(expr, "false", StringComparison.OrdinalIgnoreCase);
                            bool active = currentlyActive && cond;
                            stack.Push((currentlyActive, active, active));
                            currentlyActive = active;
                            sb.AppendLine();
                            continue;
                        }
                        if (directive.StartsWith("else", StringComparison.Ordinal) &&
                            (directive.Length == 4 || char.IsWhiteSpace(directive[4])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                bool active = top.parentActive && !top.seenActive;
                                stack.Push((top.parentActive, active, true));
                                currentlyActive = active;
                            }
                            sb.AppendLine();
                            continue;
                        }
                        if (directive.StartsWith("endif", StringComparison.Ordinal) &&
                            (directive.Length == 5 || char.IsWhiteSpace(directive[5])))
                        {
                            if (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                currentlyActive = top.parentActive;
                            }
                            sb.AppendLine();
                            continue;
                        }
                    }

                    if (currentlyActive)
                        sb.AppendLine(line);
                    else
                        sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        public static string ExportPassFindingsToMarkdown(IReadOnlyList<RenderPassMigrationFinding> findings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Unity 6 ScriptableRenderPass — Assembly Migration Report");
            sb.AppendLine();
            if (findings == null || findings.Count == 0)
            {
                sb.AppendLine("_No custom ScriptableRenderPass classes found._");
                return sb.ToString();
            }

            sb.AppendLine("| Pass Type | Assembly | RecordRenderGraph | Legacy Execute | Status |");
            sb.AppendLine("|---|---|---|---|---|");
            for (int i = 0; i < findings.Count; i++)
            {
                RenderPassMigrationFinding f = findings[i];
                sb.Append("| `").Append(f.typeFullName).Append("` | `")
                  .Append(f.assemblyName).Append("` | ")
                  .Append(f.implementsRecordRenderGraph ? "Yes" : "**NO**").Append(" | ")
                  .Append(f.implementsLegacyExecute ? "Yes" : "No").Append(" | ")
                  .Append(f.migrationStatus).AppendLine(" |");
            }
            return sb.ToString();
        }

        private static bool DeclaresMethodOverride(Type type, string methodName)
        {
            Type current = type;
            while (current != null && current.Name != "ScriptableRenderPass" && current != typeof(object))
            {
                MethodInfo[] methods = current.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (string.Equals(methods[i].Name, methodName, StringComparison.Ordinal))
                        return true;
                }
                current = current.BaseType;
            }
            return false;
        }

        private static bool IsSubclassOfByName(Type type, string baseTypeName)
        {
            Type current = type.BaseType;
            while (current != null && current != typeof(object))
            {
                if (string.Equals(current.Name, baseTypeName, StringComparison.Ordinal))
                    return true;
                current = current.BaseType;
            }
            return false;
        }
    }
}
