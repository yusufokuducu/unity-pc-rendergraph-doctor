using System;
using System.Collections.Generic;
using System.Reflection;

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

    /// <summary>
    /// Scans loaded assemblies via reflection for custom ScriptableRenderPass implementations
    /// and verifies whether they implement Unity 6's native RecordRenderGraph API or force
    /// Compatibility Mode via legacy Execute(ScriptableRenderContext, ref RenderingData).
    /// </summary>
    public static class RenderGraphAssemblyAuditor
    {
        public static List<RenderPassMigrationFinding> AuditAllLoadedRenderPasses()
        {
            var findings = new List<RenderPassMigrationFinding>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int a = 0; a < assemblies.Length; a++)
            {
                Assembly asm = assemblies[a];
                string asmName = asm.GetName().Name;

                // Skip core system & Unity internal editor assemblies for user/third-party focus
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
