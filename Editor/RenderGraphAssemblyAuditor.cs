using System;
using System.Collections.Generic;
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
        private static readonly Regex BlockCommentRegex = new Regex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
        private static readonly Regex LineCommentRegex = new Regex(@"//.*?$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex RecordRenderGraphRegex = new Regex(@"\bvoid\s+RecordRenderGraph\s*\(", RegexOptions.Compiled);
        private static readonly Regex AddRasterPassRegex = new Regex(@"\.\s*AddRasterRenderPass\s*<", RegexOptions.Compiled);
        private static readonly Regex AddUnsafePassRegex = new Regex(@"\.\s*AddUnsafePass\s*<", RegexOptions.Compiled);
        private static readonly Regex SetRenderFuncLambdaRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*(static\b)?\s*(?:\([^)]*\)\s*=>|[A-Za-z_]\w*\s*=>|delegate\b)",
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

            string code = BlockCommentRegex.Replace(rawCSharpSource, " ");
            code = LineCommentRegex.Replace(code, string.Empty);

            res.usesRecordRenderGraph = RecordRenderGraphRegex.IsMatch(code);
            res.usesAddRasterRenderPass = AddRasterPassRegex.IsMatch(code);
            res.usesAddUnsafePass = AddUnsafePassRegex.IsMatch(code);
            res.usesLegacyCommandBufferPoolInGraph = res.usesRecordRenderGraph && CommandBufferPoolRegex.IsMatch(code);
            res.usesLegacyGetTemporaryRtInGraph = res.usesRecordRenderGraph && GetTemporaryRtRegex.IsMatch(code);

            MatchCollection renderFuncs = SetRenderFuncLambdaRegex.Matches(code);
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

            if (res.hasNonStaticRenderFuncClosureRisk)
            {
                res.findings.Add(
                    "GC CLOSURE ALLOCATION RISK: SetRenderFunc lambda is not marked 'static'. Mark the lambda 'static (PassData data, RasterGraphContext ctx) => ...' to prevent per-frame delegate/closure GC allocations.");
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
