using System;
using System.Collections.Generic;
using System.Globalization;
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
        public bool hasInstanceCaptureInRenderFunc;
        public bool usesLegacyCommandBufferPoolInGraph;
        public bool usesLegacyGetTemporaryRtInGraph;

        // 8-Item Official URP Render Graph Checklist flags
        public bool hasUnusedOrUnboundMaterialHazard;        // Rule 1: Material Binding
        public bool hasSimultaneousReadWriteTextureHazard;   // Rule 2: Texture Resource Wiring
        public bool hasStalePooledPassDataHandleRisk;        // Rule 3: Pooled PassData Stale Handle Leak
        public bool hasManualTextureDescHazard;              // Rule 4: Render Graph Descriptor Validation
        public bool canSimplifyToCopyHelper;                 // Rule 5: Manual Copy Pass Simplification
        public bool canSimplifyFullscreenBlitPass;           // Rule 6: Manual Blit Pass Simplification
        public bool hasUnsafeGlobalTextureHazard;            // Rule 7: Global Resource Exposure
        public bool hasMissingConfigureInputDeclaration;     // Rule 8: Renderer Feature Input Declaration

        public List<string> capturedInstanceIdentifiers = new List<string>();
        public List<string> findings = new List<string>();
    }

    /// <summary>
    /// Scans user assemblies (filtering out Unity.* / UnityEditor.* internal engine assemblies) for custom
    /// ScriptableRenderPass implementations and statically analyzes C# Render Graph pass/feature source files
    /// against the 8-item official Unity 6 URP Render Graph checklist.
    /// </summary>
    public static class RenderGraphAssemblyAuditor
    {
        private static readonly string[] IgnoredEngineAssemblyPrefixes =
        {
            "System",
            "mscorlib",
            "netstandard",
            "Mono.",
            "nunit.",
            "Microsoft.",
            "Unity.",
            "UnityEngine",
            "UnityEditor",
            "Bee.",
            "ExCSS",
            "Google.",
            "Grpc.",
            "Newtonsoft."
        };

        private static readonly Regex RecordRenderGraphRegex = new Regex(@"\bvoid\s+RecordRenderGraph\s*\(", RegexOptions.Compiled);
        private static readonly Regex AddRasterPassRegex = new Regex(@"\.\s*AddRasterRenderPass\s*<", RegexOptions.Compiled);
        private static readonly Regex AddUnsafePassRegex = new Regex(@"\.\s*AddUnsafePass\s*<", RegexOptions.Compiled);
        private static readonly Regex SetRenderFuncLambdaRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*(static\b)?\s*(?:\(([^)]*)\)\s*=>|([A-Za-z_]\w*)\s*=>|delegate\s*(?:\(([^)]*)\))?)",
            RegexOptions.Compiled);
        private static readonly Regex SetRenderFuncMethodGroupRegex = new Regex(
            @"\.\s*SetRenderFunc\s*(?:<[^>]+>\s*)?\(\s*(?:[A-Za-z_]\w*\s*\.\s*)*([A-Za-z_]\w*)\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex CommandBufferPoolRegex = new Regex(@"\bCommandBufferPool\s*\.\s*Get\b", RegexOptions.Compiled);
        private static readonly Regex GetTemporaryRtRegex = new Regex(@"\bRenderTexture\s*\.\s*GetTemporary\b", RegexOptions.Compiled);

        // Official 8-item URP Render Graph rules regexes
        private static readonly Regex DeclaredMaterialFieldRegex = new Regex(
            @"\bMaterial\s+([A-Za-z_]\w*)\s*(?:;|=)",
            RegexOptions.Compiled);
        private static readonly Regex UseTextureCallRegex = new Regex(
            @"\.\s*UseTexture\s*\(\s*([A-Za-z_][\w\.]*)",
            RegexOptions.Compiled);
        private static readonly Regex SetRenderAttachmentCallRegex = new Regex(
            @"\.\s*SetRenderAttachment(?:Depth)?\s*\(\s*([A-Za-z_][\w\.]*)",
            RegexOptions.Compiled);
        private static readonly Regex PassDataClassRegex = new Regex(
            @"\bclass\s+(\w*PassData\w*)\s*\{([\s\S]*?)\}",
            RegexOptions.Compiled);
        private static readonly Regex TextureHandleFieldRegex = new Regex(
            @"\bTextureHandle\s+([A-Za-z_]\w*)\s*;",
            RegexOptions.Compiled);
        private static readonly Regex ManualTextureDescRegex = new Regex(
            @"(?:\bnew\s+TextureDesc\s*\(|\.\s*cameraTargetDescriptor\b)",
            RegexOptions.Compiled);
        private static readonly Regex GetDescriptorRegex = new Regex(
            @"\.\s*GetDescriptor\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex CmdSetGlobalTextureRegex = new Regex(
            @"\.\s*cmd\s*\.\s*SetGlobalTexture\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex UseAllGlobalTexturesRegex = new Regex(
            @"\.\s*UseAllGlobalTextures\s*\(\s*true\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex BlitTextureCallRegex = new Regex(
            @"\bBlitter\s*\.\s*BlitTexture\s*\(([^;]*)\);",
            RegexOptions.Compiled);
        private static readonly Regex ConfigureInputCallRegex = new Regex(
            @"\bConfigureInput\s*\(([^)]*)\)",
            RegexOptions.Compiled);
        private static readonly Regex ClassInstanceFieldDeclRegex = new Regex(
            @"^\s*(?:private|protected|public|internal)?\s*(?:readonly\s+)?(?!static\b)(?!const\b)(?:[A-Za-z_][\w<>.\[\]]*)\s+([A-Za-z_]\w*)\s*(?:=|;)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static List<RenderPassMigrationFinding> AuditAllLoadedRenderPasses(bool includeUnityInternalAssemblies = false)
        {
            var findings = new List<RenderPassMigrationFinding>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int a = 0; a < assemblies.Length; a++)
            {
                Assembly asm = assemblies[a];
                string asmName = asm.GetName().Name ?? string.Empty;

                if (!includeUnityInternalAssemblies && IsIgnoredEngineOrFrameworkAssembly(asmName))
                    continue;

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

        public static bool IsIgnoredEngineOrFrameworkAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return true;

            for (int i = 0; i < IgnoredEngineAssemblyPrefixes.Length; i++)
            {
                if (assemblyName.StartsWith(IgnoredEngineAssemblyPrefixes[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
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
        /// across all 8 official Unity 6 URP Render Graph checklist rules plus legacy pool/RT anti-patterns.
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
            string graphScopeCode = res.usesRecordRenderGraph
                ? ExtractMethodBodies(code, RecordRenderGraphRegex)
                : code;

            res.usesAddRasterRenderPass = AddRasterPassRegex.IsMatch(graphScopeCode);
            res.usesAddUnsafePass = AddUnsafePassRegex.IsMatch(graphScopeCode);
            res.usesLegacyCommandBufferPoolInGraph = res.usesRecordRenderGraph && CommandBufferPoolRegex.IsMatch(graphScopeCode);
            res.usesLegacyGetTemporaryRtInGraph = res.usesRecordRenderGraph && GetTemporaryRtRegex.IsMatch(graphScopeCode);

            HashSet<string> instanceFields = ExtractOuterClassInstanceFieldNames(code);

            // Rule 3A: Static vs Non-Static SetRenderFunc & Instance Capture Detection
            MatchCollection renderFuncs = SetRenderFuncLambdaRegex.Matches(graphScopeCode);
            var lambdaBodies = new List<(string dataParam, string body)>();
            for (int i = 0; i < renderFuncs.Count; i++)
            {
                Match rf = renderFuncs[i];
                if (rf.Groups[1].Success)
                    res.usesStaticRenderFuncLambda = true;
                else
                    res.hasNonStaticRenderFuncClosureRisk = true;

                string paramList = rf.Groups[2].Success ? rf.Groups[2].Value
                    : (rf.Groups[3].Success ? rf.Groups[3].Value
                    : (rf.Groups[4].Success ? rf.Groups[4].Value : string.Empty));
                string dataParam = ExtractFirstParameterIdentifier(paramList);
                string body = ExtractLambdaBodyAfterMatch(graphScopeCode, rf.Index + rf.Length);
                lambdaBodies.Add((dataParam, body));

                if (!rf.Groups[1].Success && !string.IsNullOrEmpty(body))
                {
                    DetectInstanceCapturesInLambda(body, dataParam, instanceFields, res);
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
                    res.usesStaticRenderFuncLambda = true;
                else
                    res.hasNonStaticRenderFuncClosureRisk = true;
            }

            if (res.usesRecordRenderGraph)
            {
                // Rule 1: Material Binding (declared Material field never referenced in RecordRenderGraph)
                EvaluateRule1MaterialBinding(code, graphScopeCode, res);

                // Rule 2: Simultaneous UseTexture(X) + SetRenderAttachment(X) read/write hazard
                EvaluateRule2SimultaneousReadWrite(graphScopeCode, res);

                // Rule 3B: Pooled PassData TextureHandle field read in SetRenderFunc without assignment in RecordRenderGraph
                EvaluateRule3PooledPassDataHandles(code, graphScopeCode, lambdaBodies, res);

                // Rule 4: Manual new TextureDesc(...) or cameraTargetDescriptor instead of GetDescriptor(renderGraph)
                if (ManualTextureDescRegex.IsMatch(graphScopeCode) && !GetDescriptorRegex.IsMatch(graphScopeCode))
                {
                    res.hasManualTextureDescHazard = true;
                }

                // Rule 5 & Rule 6: Manual Copy Pass / Simple Fullscreen Blit Pass simplification
                EvaluateRules5And6CopyBlitSimplification(graphScopeCode, lambdaBodies, res);

                // Rule 7: Direct cmd.SetGlobalTexture or UseAllGlobalTextures(true)
                if (CmdSetGlobalTextureRegex.IsMatch(graphScopeCode) || UseAllGlobalTexturesRegex.IsMatch(graphScopeCode))
                {
                    res.hasUnsafeGlobalTextureHazard = true;
                }

                // Rule 8: Pipeline-provided resourceData.cameraNormalsTexture / cameraDepthTexture / cameraOpaqueTexture / motionVectorsTexture without ConfigureInput
                EvaluateRule8ConfigureInput(code, graphScopeCode, res);
            }

            PopulateDiagnosticFindings(res);
            return res;
        }

        private static void PopulateDiagnosticFindings(RenderGraphSourceAuditResult res)
        {
            if (res.hasNonStaticRenderFuncClosureRisk)
            {
                string captureSuffix = res.hasInstanceCaptureInRenderFunc
                    ? $" (Captures instance state: {string.Join(", ", res.capturedInstanceIdentifiers)} — move into PassData before marking static to avoid CS8820)."
                    : ". Use 'static (PassData data, RasterGraphContext ctx) => ...' to prevent per-frame delegate/closure GC allocations.";
                res.findings.Add("RULE 3 [STATIC RENDER FUNC]: SetRenderFunc callback is non-static" + captureSuffix);
            }

            if (res.hasStalePooledPassDataHandleRisk)
            {
                res.findings.Add(
                    "RULE 3 [STALE POOLED PASSDATA]: A TextureHandle field on pooled PassData is read inside SetRenderFunc without being assigned during RecordRenderGraph, risking dangling frame-to-frame handle bugs.");
            }

            if (res.hasSimultaneousReadWriteTextureHazard)
            {
                res.findings.Add(
                    "RULE 2 [READ/WRITE WIRING HAZARD]: The same TextureHandle is wired to both UseTexture() (read) and SetRenderAttachment() (write) in the same pass. Ping-pong through a transient RenderGraph texture.");
            }

            if (res.hasUnusedOrUnboundMaterialHazard)
            {
                res.findings.Add(
                    "RULE 1 [MATERIAL BINDING]: Pass declares a Material field that is never passed into PassData or bound during RecordRenderGraph.");
            }

            if (res.hasManualTextureDescHazard)
            {
                res.findings.Add(
                    "RULE 4 [TEXTURE DESCRIPTOR]: Avoid 'new TextureDesc(...)' or 'cameraTargetDescriptor' by default; derive from 'resourceData.activeColorTexture.GetDescriptor(renderGraph)' so MSAA/HDR compatibility is preserved.");
            }

            if (res.canSimplifyToCopyHelper)
            {
                res.findings.Add(
                    "RULE 5 [COPY PASS SIMPLIFICATION]: Custom AddRasterRenderPass performs an unmodified texture copy without a Material; simplify with 'renderGraph.AddBlitPass(src, dst)' or 'AddCopyPass'.");
            }

            if (res.canSimplifyFullscreenBlitPass)
            {
                res.findings.Add(
                    "RULE 6 [BLIT PASS SIMPLIFICATION]: Custom AddRasterRenderPass executes only a single fullscreen Blitter.BlitTexture with a Material; simplify using 'renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(...))'.");
            }

            if (res.hasUnsafeGlobalTextureHazard)
            {
                res.findings.Add(
                    "RULE 7 [GLOBAL TEXTURE EXPOSURE]: Avoid 'ctx.cmd.SetGlobalTexture()' inside SetRenderFunc or 'UseAllGlobalTextures(true)'; prefer explicit TextureHandle wiring or 'builder.SetGlobalTextureAfterPass(...)'.");
            }

            if (res.hasMissingConfigureInputDeclaration)
            {
                res.findings.Add(
                    "RULE 8 [MISSING CONFIGUREINPUT]: Pass reads cameraDepthTexture / cameraNormalsTexture / cameraOpaqueTexture / motionVectorsTexture without declaring matching 'ConfigureInput(ScriptableRenderPassInput.*)'.");
            }

            if (res.usesAddUnsafePass)
            {
                res.findings.Add(
                    "UNSAFE PASS OVERHEAD: AddUnsafePass disables automatic Render Graph native pass merging. Prefer AddRasterRenderPass or AddComputePass unless modifying global state.");
            }

            if (res.usesLegacyCommandBufferPoolInGraph)
            {
                res.findings.Add(
                    "LEGACY COMMANDBUFFER POOL IN RENDER GRAPH: Avoid CommandBufferPool.Get() inside RecordRenderGraph; use context.cmd directly inside SetRenderFunc.");
            }

            if (res.usesLegacyGetTemporaryRtInGraph)
            {
                res.findings.Add(
                    "UNMANAGED TEMPORARY RT: Avoid RenderTexture.GetTemporary() inside RecordRenderGraph; use renderGraph.CreateTexture() so Render Graph can alias memory.");
            }

            if (res.findings.Count == 0 && res.usesRecordRenderGraph)
            {
                res.findings.Add("Clean Unity 6 Render Graph implementation.");
            }
        }

        private static HashSet<string> ExtractOuterClassInstanceFieldNames(string code)
        {
            // Remove PassData inner class blocks first so PassData fields are not confused with outer pass fields
            string withoutPassData = PassDataClassRegex.Replace(code, string.Empty);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            MatchCollection matches = ClassInstanceFieldDeclRegex.Matches(withoutPassData);
            for (int i = 0; i < matches.Count; i++)
            {
                string name = matches[i].Groups[1].Value;
                if (!string.IsNullOrEmpty(name))
                    fields.Add(name);
            }
            return fields;
        }

        private static void DetectInstanceCapturesInLambda(
            string lambdaBody,
            string dataParam,
            HashSet<string> instanceFields,
            RenderGraphSourceAuditResult res)
        {
            if (Regex.IsMatch(lambdaBody, @"\bthis\s*\."))
            {
                res.hasInstanceCaptureInRenderFunc = true;
                if (!res.capturedInstanceIdentifiers.Contains("this"))
                    res.capturedInstanceIdentifiers.Add("this");
            }

            foreach (string field in instanceFields)
            {
                // Match bare `fieldName` not preceded by `.` (e.g. not `data.fieldName`)
                var bareRefRegex = new Regex($@"(?<!\.\s*)\b{Regex.Escape(field)}\b");
                if (bareRefRegex.IsMatch(lambdaBody) && !string.Equals(field, dataParam, StringComparison.Ordinal))
                {
                    res.hasInstanceCaptureInRenderFunc = true;
                    if (!res.capturedInstanceIdentifiers.Contains(field))
                        res.capturedInstanceIdentifiers.Add(field);
                }
            }
        }

        private static void EvaluateRule1MaterialBinding(string fullCode, string graphScopeCode, RenderGraphSourceAuditResult res)
        {
            string withoutPassData = PassDataClassRegex.Replace(fullCode, string.Empty);
            MatchCollection matDecls = DeclaredMaterialFieldRegex.Matches(withoutPassData);
            for (int i = 0; i < matDecls.Count; i++)
            {
                string matField = matDecls[i].Groups[1].Value;
                if (!string.IsNullOrEmpty(matField) &&
                    !Regex.IsMatch(graphScopeCode, $@"\b{Regex.Escape(matField)}\b"))
                {
                    res.hasUnusedOrUnboundMaterialHazard = true;
                    return;
                }
            }
        }

        private static void EvaluateRule2SimultaneousReadWrite(string graphScopeCode, RenderGraphSourceAuditResult res)
        {
            var readHandles = new HashSet<string>(StringComparer.Ordinal);
            MatchCollection reads = UseTextureCallRegex.Matches(graphScopeCode);
            for (int i = 0; i < reads.Count; i++)
                readHandles.Add(reads[i].Groups[1].Value.Trim());

            if (readHandles.Count == 0) return;

            MatchCollection writes = SetRenderAttachmentCallRegex.Matches(graphScopeCode);
            for (int i = 0; i < writes.Count; i++)
            {
                if (readHandles.Contains(writes[i].Groups[1].Value.Trim()))
                {
                    res.hasSimultaneousReadWriteTextureHazard = true;
                    return;
                }
            }
        }

        private static void EvaluateRule3PooledPassDataHandles(
            string fullCode,
            string graphScopeCode,
            List<(string dataParam, string body)> lambdaBodies,
            RenderGraphSourceAuditResult res)
        {
            if (lambdaBodies.Count == 0) return;

            MatchCollection passDataClasses = PassDataClassRegex.Matches(fullCode);
            if (passDataClasses.Count == 0) return;

            var handleFields = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < passDataClasses.Count; c++)
            {
                MatchCollection hf = TextureHandleFieldRegex.Matches(passDataClasses[c].Groups[2].Value);
                for (int f = 0; f < hf.Count; f++)
                    handleFields.Add(hf[f].Groups[1].Value);
            }

            if (handleFields.Count == 0) return;

            foreach (string handleField in handleFields)
            {
                bool readInLambda = false;
                for (int l = 0; l < lambdaBodies.Count; l++)
                {
                    string dp = lambdaBodies[l].dataParam;
                    if (!string.IsNullOrEmpty(dp) &&
                        Regex.IsMatch(lambdaBodies[l].body, $@"\b{Regex.Escape(dp)}\s*\.\s*{Regex.Escape(handleField)}\b"))
                    {
                        readInLambda = true;
                        break;
                    }
                }

                if (!readInLambda) continue;

                // Must be assigned somewhere in RecordRenderGraph: `.handleField =`
                bool assignedInSetup = Regex.IsMatch(graphScopeCode, $@"\.\s*{Regex.Escape(handleField)}\s*=(?!=)");
                if (!assignedInSetup)
                {
                    res.hasStalePooledPassDataHandleRisk = true;
                    return;
                }
            }
        }

        private static void EvaluateRules5And6CopyBlitSimplification(
            string graphScopeCode,
            List<(string dataParam, string body)> lambdaBodies,
            RenderGraphSourceAuditResult res)
        {
            if (!res.usesAddRasterRenderPass || lambdaBodies.Count != 1)
                return;

            string body = lambdaBodies[0].body.Trim();
            MatchCollection blits = BlitTextureCallRegex.Matches(body);
            if (blits.Count != 1)
                return;

            // Only flag when the entire render lambda is just that single Blitter.BlitTexture statement
            string strippedSemis = body.Trim('{', '}', ' ', '\t', '\r', '\n');
            int semiCount = 0;
            for (int i = 0; i < strippedSemis.Length; i++)
            {
                if (strippedSemis[i] == ';') semiCount++;
            }
            if (semiCount != 1)
                return;

            string args = blits[0].Groups[1].Value;
            int commaCount = args.Split(',').Length - 1;
            // 3 args (cmd, src, scaleBias, mip, bilinear) without Material -> Copy pass; with Material -> Fullscreen Blit pass
            if (args.IndexOf("mat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                args.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                res.canSimplifyFullscreenBlitPass = true;
            }
            else if (commaCount <= 4)
            {
                res.canSimplifyToCopyHelper = true;
            }
        }

        private static void EvaluateRule8ConfigureInput(string fullCode, string graphScopeCode, RenderGraphSourceAuditResult res)
        {
            bool usesNormals = graphScopeCode.IndexOf("cameraNormalsTexture", StringComparison.Ordinal) >= 0;
            bool usesDepth = graphScopeCode.IndexOf("cameraDepthTexture", StringComparison.Ordinal) >= 0;
            bool usesOpaque = graphScopeCode.IndexOf("cameraOpaqueTexture", StringComparison.Ordinal) >= 0;
            bool usesMotion = graphScopeCode.IndexOf("motionVectorsTexture", StringComparison.Ordinal) >= 0;

            if (!usesNormals && !usesDepth && !usesOpaque && !usesMotion)
                return;

            MatchCollection cfgMatches = ConfigureInputCallRegex.Matches(fullCode);
            if (cfgMatches.Count == 0)
            {
                res.hasMissingConfigureInputDeclaration = true;
                return;
            }

            var combinedFlags = new StringBuilder();
            for (int i = 0; i < cfgMatches.Count; i++)
                combinedFlags.Append(' ').Append(cfgMatches[i].Groups[1].Value);
            string declared = combinedFlags.ToString();

            if ((usesNormals && declared.IndexOf("Normal", StringComparison.Ordinal) < 0) ||
                (usesDepth && declared.IndexOf("Depth", StringComparison.Ordinal) < 0 && declared.IndexOf("Normal", StringComparison.Ordinal) < 0) ||
                (usesOpaque && declared.IndexOf("Color", StringComparison.Ordinal) < 0) ||
                (usesMotion && declared.IndexOf("Motion", StringComparison.Ordinal) < 0))
            {
                res.hasMissingConfigureInputDeclaration = true;
            }
        }

        private static string ExtractFirstParameterIdentifier(string paramList)
        {
            if (string.IsNullOrWhiteSpace(paramList))
                return string.Empty;
            string firstParam = paramList.Split(',')[0].Trim();
            string[] parts = firstParam.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[parts.Length - 1].Trim() : string.Empty;
        }

        private static string ExtractLambdaBodyAfterMatch(string code, int startIdx)
        {
            int idx = startIdx;
            while (idx < code.Length && char.IsWhiteSpace(code[idx]))
                idx++;
            if (idx >= code.Length)
                return string.Empty;

            if (code[idx] == '{')
            {
                int bodyStart = idx + 1;
                int depth = 1;
                idx++;
                while (idx < code.Length && depth > 0)
                {
                    char c = code[idx++];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                return code.Substring(bodyStart, Math.Max(0, idx - bodyStart - 1));
            }

            int endExpr = idx;
            int parenDepth = 0;
            while (endExpr < code.Length)
            {
                char c = code[endExpr];
                if (c == '(') parenDepth++;
                else if (c == ')')
                {
                    if (parenDepth == 0) break;
                    parenDepth--;
                }
                else if (c == ';' && parenDepth == 0)
                {
                    break;
                }
                endExpr++;
            }
            return code.Substring(idx, Math.Max(0, endExpr - idx));
        }

        public static string GenerateUnifiedDiff(string beforeSource, string afterSource, string filePath = "Pass.cs")
        {
            string left = (beforeSource ?? string.Empty).Replace("\r\n", "\n");
            string right = (afterSource ?? string.Empty).Replace("\r\n", "\n");
            if (string.Equals(left, right, StringComparison.Ordinal))
                return string.Empty;

            string[] oldLines = left.Split('\n');
            string[] newLines = right.Split('\n');
            string cleanPath = string.IsNullOrEmpty(filePath) ? "Pass.cs" : filePath.Replace('\\', '/');

            var sb = new StringBuilder(Math.Max(256, (left.Length + right.Length) / 2));
            sb.Append("--- a/").AppendLine(cleanPath);
            sb.Append("+++ b/").AppendLine(cleanPath);
            sb.AppendFormat(CultureInfo.InvariantCulture, "@@ -1,{0} +1,{1} @@", oldLines.Length, newLines.Length).AppendLine();

            int i = 0, j = 0;
            while (i < oldLines.Length || j < newLines.Length)
            {
                if (i < oldLines.Length && j < newLines.Length &&
                    string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
                {
                    sb.Append(' ').AppendLine(oldLines[i]);
                    i++; j++;
                    continue;
                }

                int syncOld = -1, syncNew = -1;
                for (int look = 1; look <= 6 && syncOld < 0; look++)
                {
                    for (int o = i; o <= Math.Min(oldLines.Length - 1, i + look); o++)
                    {
                        for (int n = j; n <= Math.Min(newLines.Length - 1, j + look); n++)
                        {
                            if (string.Equals(oldLines[o], newLines[n], StringComparison.Ordinal))
                            {
                                syncOld = o; syncNew = n;
                                break;
                            }
                        }
                        if (syncOld >= 0) break;
                    }
                }

                if (syncOld >= 0)
                {
                    while (i < syncOld) sb.Append('-').AppendLine(oldLines[i++]);
                    while (j < syncNew) sb.Append('+').AppendLine(newLines[j++]);
                }
                else
                {
                    if (i < oldLines.Length) sb.Append('-').AppendLine(oldLines[i++]);
                    if (j < newLines.Length) sb.Append('+').AppendLine(newLines[j++]);
                }
            }
            return sb.ToString();
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

        public static string SanitizeCSharpCommentsAndStrings(string rawSource)
        {
            if (string.IsNullOrEmpty(rawSource))
                return string.Empty;

            int len = rawSource.Length;
            var sb = new StringBuilder(len);
            int i = 0;

            while (i < len)
            {
                char c = rawSource[i];

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

                if (c == '/' && i + 1 < len && rawSource[i + 1] == '/')
                {
                    i += 2;
                    while (i < len && rawSource[i] != '\n' && rawSource[i] != '\r')
                        i++;
                    continue;
                }

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

                if (c == '"')
                {
                    sb.Append("\"\"");
                    i++;
                    while (i < len)
                    {
                        char sc = rawSource[i++];
                        if (sc == '\\' && i < len)
                            i++;
                        else if (sc == '"' || sc == '\n' || sc == '\r')
                            break;
                    }
                    continue;
                }

                if (c == '\'')
                {
                    sb.Append("''");
                    i++;
                    while (i < len)
                    {
                        char cc = rawSource[i++];
                        if (cc == '\\' && i < len)
                            i++;
                        else if (cc == '\'' || cc == '\n' || cc == '\r')
                            break;
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
                            bool isFalse = string.Equals(expr, "0", StringComparison.Ordinal) ||
                                           string.Equals(expr, "false", StringComparison.OrdinalIgnoreCase);
                            bool isTrue = string.Equals(expr, "1", StringComparison.Ordinal) ||
                                          string.Equals(expr, "true", StringComparison.OrdinalIgnoreCase);
                            bool active = currentlyActive && !isFalse;
                            stack.Push((currentlyActive, active, isTrue && active));
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
            sb.AppendLine("# Unity 6 ScriptableRenderPass — User Assembly Migration Report");
            sb.AppendLine();
            if (findings == null || findings.Count == 0)
            {
                sb.AppendLine("_No custom project ScriptableRenderPass classes found._");
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
