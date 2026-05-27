using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// 受控版临时代码执行命令。由项目设置统一控制是否允许执行。
    /// </summary>
    public class CodeCommand : ICommand
    {
        private const string ExecuteAction = "execute";
        private const string RuntimeExecuteAction = "runtime_execute";
        private const string StatusAction = "status";
        private const string CancelAction = "cancel";
        private const string RuntimeCodeExecuteAction = "runtime.code.execute";
        private const string RuntimeHttpCommandsPath = "/aibridge/commands";
        private const string CodeDirectoryName = "code";
        private const string CompiledDirectoryName = ".compiled";
        private const string FallbackCompilerProcessName = "dotnet";
        private const int DefaultTimeoutMs = 5000;
        private const int MinTimeoutMs = 1000;
        private const int MaxTimeoutMs = 60000;
        private const int MaxInlineCodeLength = 4000;
        private const long MaxSourceFileBytes = 512 * 1024;
        private const int RuntimeDispatchPollIntervalMs = 50;
        private const int MaxNormalizedDepth = 8;
        private const int MaxNormalizedCollectionItems = 512;
        private const int CompilerProbeTimeoutMs = 5000;
        private const string CSharpVersion2019 = "7.3";
        private const string CSharpVersion2020 = "8.0";
        private const string CSharpVersion2021OrNewer = "9.0";
        private static readonly Regex CompilerDiagnosticPattern = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s*(?<code>[A-Z]+\d+):\s*(?<message>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static ICodeOperation _activeOperation;

        public string Type => "code";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `code execute` - Controlled Temporary C# Execution

Experimental and enabled by default in project settings. Disable **AIBridge/Settings -> Basic -> Enable Code Execution** for untrusted projects or callers.

```bash
$CLI code execute --file "".aibridge/code/check.csx"" --timeout 5000
$CLI code execute --code ""Debug.Log(\""hello\""); return 123;""
$CLI code runtime_execute --file "".aibridge/code/player_probe.csx"" --transport http --url http://127.0.0.1:27182 --timeout 10000
$CLI code status
$CLI code cancel
```

**Rules:**
- Unity-side project setting cannot be bypassed by CLI parameters.
- `--file` must point to `.aibridge/code/*.cs` or `.aibridge/code/*.csx`.
- `--code` is intended for short snippets only.
- Prefer file mode for complex one-off Editor C# tasks: generated assets, structured analysis, diagnostics, Runtime/Public API calls, or multi-step UnityEditor API orchestration.
- `code runtime_execute` compiles a runtime-safe DLL in Editor, sends it to AIBridgeRuntime, and invokes it in Player through `Assembly.Load` + reflection. It is enabled only when `com.code-philosophy.hybridclr` is installed.
- For generation scripts, keep output under a clear folder such as `Assets/AIBridgeGenerated/<TaskName>/` and return structured result data.
- For existing Prefab structure changes prefer `prefab patch --dryRun true`; for single properties prefer `inspector`; for simple scene object edits prefer `gameobject`/`transform`.
- Snippets are wrapped as `object Execute()` or `Task<object> ExecuteAsync()` when `await` is present.
- Result data includes `enabled`, `status`, `source`, `elapsedMs`, `returnValue`, `logs`, `compileErrors`, `diagnostics`, and `exception` when applicable.
- `code execute` is single-flight. Use `code status` after a timeout and `code cancel` only to release AIBridge waiting state; user code may still finish on Unity's side.
- Use this only for trusted projects/callers; it is not a replacement for `compile unity` or `test run`.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", ExecuteAction);

            try
            {
                if (string.Equals(action, ExecuteAction, StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteCode(request);
                }

                if (string.Equals(action, RuntimeExecuteAction, StringComparison.OrdinalIgnoreCase))
                {
                    return ExecuteRuntimeCode(request);
                }

                if (string.Equals(action, StatusAction, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Success(request.id, BuildStatusData());
                }

                if (string.Equals(action, CancelAction, StringComparison.OrdinalIgnoreCase))
                {
                    return CancelActiveOperation(request);
                }

                return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: execute, runtime_execute, status, cancel");
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult ExecuteCode(CommandRequest request)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (!settings.EnableCodeExecution || !settings.CodeExecutionRiskAccepted)
            {
                return FailureWithData(
                    request.id,
                    "Code execution is disabled. Enable it in AIBridge/Settings -> Basic -> Enable Code Execution.",
                    BuildSettingsGateData(settings));
            }

            if (_activeOperation != null)
            {
                return FailureWithData(
                    request.id,
                    "Another code execution is already running.",
                    BuildBusyData());
            }

            SourceText sourceText;
            string sourceError;
            if (!TryResolveSource(request, CodeCompilationTarget.Editor, out sourceText, out sourceError))
            {
                return FailureWithData(
                    request.id,
                    sourceError,
                    new
                    {
                        enabled = true,
                        source = "invalid"
                    });
            }

            var timeoutMs = Mathf.Clamp(request.GetParam("timeout", DefaultTimeoutMs), MinTimeoutMs, MaxTimeoutMs);
            var stopwatch = Stopwatch.StartNew();
            var session = new CodeExecutionSession(request.id, sourceText, timeoutMs, stopwatch);

            try
            {
                string assemblyPath;
                List<string> compileErrors;
                if (!CompileSource(session, CodeCompilationTarget.Editor, out assemblyPath, out compileErrors))
                {
                    stopwatch.Stop();
                    return FailureWithData(
                        request.id,
                        "Code compilation failed.",
                        BuildResultData(session, null, compileErrors, null, ContainsTimeoutError(compileErrors) ? (bool?)true : null, "compile_failed"));
                }

                session.CompiledAssemblyPath = assemblyPath;
                Application.logMessageReceived += session.OnLogMessageReceived;

                var invocation = InvokeCompiledCode(assemblyPath, sourceText.ClassName);
                if (invocation.Task != null)
                {
                    _activeOperation = new CodeAsyncOperation(session, invocation.Task);
                    EditorApplication.update -= OnAsyncUpdate;
                    EditorApplication.update += OnAsyncUpdate;
                    return null;
                }

                Application.logMessageReceived -= session.OnLogMessageReceived;
                stopwatch.Stop();
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    return FailureWithData(
                        request.id,
                        "Code execution timed out after " + timeoutMs + "ms.",
                        BuildResultData(session, null, new List<string>(), null, true, "timed_out"));
                }

                return CommandResult.Success(request.id, BuildResultData(session, NormalizeReturnValue(invocation.ReturnValue), new List<string>(), null, false, "completed"));
            }
            catch (Exception ex)
            {
                Application.logMessageReceived -= session.OnLogMessageReceived;
                stopwatch.Stop();
                return FailureWithData(request.id, "Code execution failed.", BuildResultData(session, null, new List<string>(), BuildExceptionInfo(ex), false, "failed"));
            }
        }

        private CommandResult ExecuteRuntimeCode(CommandRequest request)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (!settings.EnableCodeExecution || !settings.CodeExecutionRiskAccepted)
            {
                return FailureWithData(
                    request.id,
                    "Code execution is disabled. Enable it in AIBridge/Settings -> Basic -> Enable Code Execution.",
                    BuildSettingsGateData(settings));
            }

            if (!AIBridgeHybridClrUtility.IsHybridClrInstalled())
            {
                return FailureWithData(
                    request.id,
                    "HybridCLR package is required for code runtime_execute.",
                    BuildRuntimeCodeUnavailableData(settings));
            }

            if (_activeOperation != null)
            {
                return FailureWithData(
                    request.id,
                    "Another code execution is already running.",
                    BuildBusyData());
            }

            SourceText sourceText;
            string sourceError;
            if (!TryResolveSource(request, CodeCompilationTarget.Runtime, out sourceText, out sourceError))
            {
                return FailureWithData(
                    request.id,
                    sourceError,
                    new
                    {
                        enabled = true,
                        source = "invalid"
                    });
            }

            var timeoutMs = Mathf.Clamp(request.GetParam("timeout", DefaultTimeoutMs), MinTimeoutMs, MaxTimeoutMs);
            var stopwatch = Stopwatch.StartNew();
            var session = new CodeExecutionSession(request.id, sourceText, timeoutMs, stopwatch);

            try
            {
                string assemblyPath;
                List<string> compileErrors;
                if (!CompileSource(session, CodeCompilationTarget.Runtime, out assemblyPath, out compileErrors))
                {
                    stopwatch.Stop();
                    return FailureWithData(
                        request.id,
                        "Runtime code compilation failed.",
                        BuildResultData(session, null, compileErrors, null, ContainsTimeoutError(compileErrors) ? (bool?)true : null, "compile_failed"));
                }

                session.CompiledAssemblyPath = assemblyPath;
                var assemblyBytes = File.ReadAllBytes(assemblyPath);
                var sha256 = ComputeSha256(assemblyBytes);

                RuntimeDispatchOptions dispatchOptions;
                string dispatchError;
                if (!TryBuildRuntimeDispatchOptions(request, timeoutMs, out dispatchOptions, out dispatchError))
                {
                    stopwatch.Stop();
                    return FailureWithData(
                        request.id,
                        dispatchError,
                        BuildRuntimeDispatchResultData(session, assemblyBytes.Length, sha256, null, null, "dispatch_invalid", false));
                }

                var runtimeCommand = BuildRuntimeCodeCommand(request, sourceText, assemblyBytes, sha256, dispatchOptions);
                var operation = new RuntimeCodeAsyncOperation(session, runtimeCommand, dispatchOptions, assemblyBytes.Length, sha256);
                _activeOperation = operation;
                EditorApplication.update -= OnAsyncUpdate;
                EditorApplication.update += OnAsyncUpdate;
                return null;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return FailureWithData(
                    request.id,
                    "Runtime code execution dispatch failed.",
                    BuildResultData(session, null, new List<string>(), BuildExceptionInfo(ex), false, "failed"));
            }
        }

        private static object BuildStatusData()
        {
            if (_activeOperation == null)
            {
                return new
                {
                    active = false,
                    busy = false,
                    status = "idle",
                    hint = "No code execution is active."
                };
            }

            return _activeOperation.BuildStatusData();
        }

        private static object BuildBusyData()
        {
            return new
            {
                active = true,
                busy = true,
                activeOperation = BuildStatusData(),
                hint = "code execute is single-flight. Run `code status` to inspect it, or `code cancel` to release AIBridge waiting state."
            };
        }

        private static CommandResult CancelActiveOperation(CommandRequest request)
        {
            if (_activeOperation == null)
            {
                return CommandResult.Success(request.id, new
                {
                    active = false,
                    canceled = false,
                    status = "idle",
                    hint = "No code execution is active."
                });
            }

            var targetRequestId = request.GetParam<string>("requestId", null);
            if (!string.IsNullOrWhiteSpace(targetRequestId)
                && !string.Equals(targetRequestId, _activeOperation.RequestId, StringComparison.Ordinal))
            {
                return FailureWithData(
                    request.id,
                    "Active code execution does not match requestId.",
                    new
                    {
                        active = true,
                        canceled = false,
                        requestedRequestId = targetRequestId,
                        activeOperation = _activeOperation.BuildStatusData()
                    });
            }

            var canceledRequestId = _activeOperation.RequestId;
            var originalResult = _activeOperation.CancelForOriginalRequest();
            var originalResultWritten = originalResult != null;
            if (originalResult != null)
            {
                WriteResultFile(originalResult);
            }

            _activeOperation = null;
            EditorApplication.update -= OnAsyncUpdate;
            return CommandResult.Success(request.id, new
            {
                active = false,
                canceled = true,
                canceledRequestId = canceledRequestId,
                originalResultWritten = originalResultWritten,
                warning = "Cancellation releases AIBridge waiting state only; Unity user code that already started may still finish."
            });
        }

        private static bool TryResolveSource(CommandRequest request, CodeCompilationTarget target, out SourceText sourceText, out string error)
        {
            sourceText = null;
            error = null;

            var file = request.GetParam<string>("file", null);
            var inlineCode = request.GetParam<string>("code", null);
            var hasFile = !string.IsNullOrWhiteSpace(file);
            var hasInlineCode = !string.IsNullOrWhiteSpace(inlineCode);
            if (hasFile == hasInlineCode)
            {
                error = "Provide exactly one source: --file or --code.";
                return false;
            }

            var projectRoot = GetProjectRoot();
            var codeRoot = Path.GetFullPath(Path.Combine(projectRoot, ".aibridge", CodeDirectoryName));

            if (hasFile)
            {
                var fullPath = ResolveCodeFilePath(file);
                if (!IsSameOrChildPath(codeRoot, fullPath))
                {
                    error = "Code file must be under .aibridge/code.";
                    return false;
                }

                var parentDirectory = Directory.GetParent(fullPath);
                if (parentDirectory == null || !string.Equals(parentDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), codeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    error = "Code file must be directly under .aibridge/code.";
                    return false;
                }

                var extension = Path.GetExtension(fullPath);
                if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".csx", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Code file must be .cs or .csx.";
                    return false;
                }

                if (!File.Exists(fullPath))
                {
                    error = "Code file not found: " + fullPath;
                    return false;
                }

                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length > MaxSourceFileBytes)
                {
                    error = "Code file is too large. Maximum size is " + MaxSourceFileBytes + " bytes.";
                    return false;
                }

                sourceText = BuildSourceText(File.ReadAllText(fullPath, Encoding.UTF8), "file", fullPath, target);
                return true;
            }

            if (inlineCode.Length > MaxInlineCodeLength)
            {
                error = "Inline code is too long. Use a file under .aibridge/code instead.";
                return false;
            }

            sourceText = BuildSourceText(inlineCode, "inline", "inline", target);
            return true;
        }

        private static SourceText BuildSourceText(string code, string sourceKind, string sourcePath, CodeCompilationTarget target)
        {
            var className = "AIBridgeCode_" + Guid.NewGuid().ToString("N");
            var containsAwait = code.IndexOf("await", StringComparison.Ordinal) >= 0;
            var leadingUsings = ExtractLeadingUsings(ref code);
            var wrappedSource = WrapCode(className, code, leadingUsings, containsAwait, target);
            return new SourceText
            {
                Kind = sourceKind,
                Path = sourcePath,
                Code = code,
                WrappedCode = wrappedSource,
                ClassName = className,
                IsAsync = containsAwait,
                Target = target
            };
        }

        private static List<string> ExtractLeadingUsings(ref string code)
        {
            var result = new List<string>();
            var reader = new StringReader(code ?? string.Empty);
            var body = new StringBuilder();
            string line;
            var stillReadingUsings = true;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (stillReadingUsings && (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (stillReadingUsings && trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith(";", StringComparison.Ordinal))
                {
                    result.Add(trimmed);
                    continue;
                }

                stillReadingUsings = false;
                body.AppendLine(line);
            }

            code = body.ToString();
            return result;
        }

        private static string WrapCode(string className, string body, IEnumerable<string> leadingUsings, bool isAsync, CodeCompilationTarget target)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Collections;");
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Threading.Tasks;");
            builder.AppendLine("using UnityEngine;");
            if (target == CodeCompilationTarget.Editor)
            {
                builder.AppendLine("using UnityEditor;");
                builder.AppendLine("using AIBridge.Editor;");
            }
            else
            {
                builder.AppendLine("using AIBridge.Runtime;");
            }

            foreach (var usingLine in leadingUsings)
            {
                builder.AppendLine(usingLine);
            }

            builder.AppendLine("public static class " + className);
            builder.AppendLine("{");
            if (target == CodeCompilationTarget.Runtime)
            {
                builder.AppendLine(isAsync
                    ? "    public static async Task<object> ExecuteAsync(AIBridgeRuntimeCommand AIBridgeCommand)"
                    : "    public static object Execute(AIBridgeRuntimeCommand AIBridgeCommand)");
            }
            else
            {
                builder.AppendLine(isAsync
                    ? "    public static async Task<object> ExecuteAsync()"
                    : "    public static object Execute()");
            }

            builder.AppendLine("    {");
            if (target == CodeCompilationTarget.Runtime)
            {
                builder.AppendLine("        var AIBridgeRuntimeArgs = AIBridgeCommand == null ? null : AIBridgeCommand.Params;");
            }

            builder.AppendLine(body ?? string.Empty);
            if (ShouldAppendFallbackReturn(body))
            {
                builder.AppendLine("        return null;");
            }
            builder.AppendLine("    }");
            builder.AppendLine("}");
            return builder.ToString();
        }

        internal static bool ShouldAppendFallbackReturn(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return true;
            }

            var trimmed = TrimTrailingTrivia(body);
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.EndsWith(";", StringComparison.Ordinal))
            {
                return true;
            }

            var statementStart = FindLastStatementStart(trimmed);
            var lastStatement = trimmed.Substring(statementStart).TrimStart();
            return !StartsWithKeyword(lastStatement, "return") && !StartsWithKeyword(lastStatement, "throw");
        }

        private static string TrimTrailingTrivia(string text)
        {
            var current = text ?? string.Empty;
            var changed = true;
            while (changed)
            {
                changed = false;
                current = current.TrimEnd();

                if (current.EndsWith("*/", StringComparison.Ordinal))
                {
                    var blockStart = current.LastIndexOf("/*", StringComparison.Ordinal);
                    if (blockStart >= 0)
                    {
                        current = current.Substring(0, blockStart);
                        changed = true;
                        continue;
                    }
                }

                var lineStart = Math.Max(current.LastIndexOf('\n'), current.LastIndexOf('\r')) + 1;
                var lineComment = current.IndexOf("//", lineStart, StringComparison.Ordinal);
                if (lineComment >= 0 && current.IndexOf(";", lineComment, StringComparison.Ordinal) < 0)
                {
                    current = current.Substring(0, lineComment);
                    changed = true;
                }
            }

            return current.TrimEnd();
        }

        private static int FindLastStatementStart(string text)
        {
            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            // 从末尾反扫，跳过匿名对象/集合初始化等表达式内部的分号，定位最后一个顶层语句。
            for (var i = text.Length - 2; i >= 0; i--)
            {
                var c = text[i];
                if (c == ')') parenDepth++;
                else if (c == '(') parenDepth--;
                else if (c == ']') bracketDepth++;
                else if (c == '[') bracketDepth--;
                else if (c == '}') braceDepth++;
                else if (c == '{') braceDepth--;
                else if (c == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    return i + 1;
                }
            }

            return 0;
        }

        private static bool StartsWithKeyword(string text, string keyword)
        {
            if (text == null || !text.StartsWith(keyword, StringComparison.Ordinal))
            {
                return false;
            }

            return text.Length == keyword.Length || !IsIdentifierChar(text[keyword.Length]);
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool CompileSource(CodeExecutionSession session, CodeCompilationTarget target, out string assemblyPath, out List<string> compileErrors)
        {
            compileErrors = new List<string>();
            assemblyPath = null;

            var projectRoot = GetProjectRoot();
            var compiledDir = Path.Combine(projectRoot, ".aibridge", CodeDirectoryName, CompiledDirectoryName);
            if (!Directory.Exists(compiledDir))
            {
                Directory.CreateDirectory(compiledDir);
            }

            var fileStem = session.Source.ClassName;
            var sourcePath = Path.Combine(compiledDir, fileStem + ".generated.cs");
            assemblyPath = Path.Combine(compiledDir, fileStem + ".dll");
            var responsePath = Path.Combine(compiledDir, fileStem + ".rsp");
            File.WriteAllText(sourcePath, session.Source.WrappedCode, Encoding.UTF8);
            File.WriteAllText(responsePath, BuildCompilerResponseFile(sourcePath, assemblyPath, GetSupportedCSharpLanguageVersion(), target), Encoding.UTF8);

            CompilerInvocation compiler;
            List<string> compilerProbePaths;
            List<string> compilerProbeFailures;
            if (!TryResolveCompiler(out compiler, out compilerProbePaths, out compilerProbeFailures))
            {
                compileErrors.Add("C# compiler was not found or could not start in the Unity installation.");
                for (var i = 0; i < compilerProbePaths.Count; i++)
                {
                    compileErrors.Add("Tried compiler path: " + compilerProbePaths[i]);
                }

                for (var i = 0; i < compilerProbeFailures.Count; i++)
                {
                    compileErrors.Add(compilerProbeFailures[i]);
                }

                session.CompilerProbePaths = compilerProbePaths;
                session.CompilerProbeFailures = compilerProbeFailures;
                return false;
            }

            session.Compiler = compiler;
            session.CompilerProbePaths = compilerProbePaths;
            session.CompilerProbeFailures = compilerProbeFailures;
            var output = RunCompilerProcess(compiler, responsePath, session.TimeoutMs, compileErrors, session.CompilerDiagnostics);
            if (!string.IsNullOrEmpty(output))
            {
                session.CompilerOutput = output;
            }

            return File.Exists(assemblyPath) && compileErrors.Count == 0;
        }

        private static bool ContainsTimeoutError(IEnumerable<string> errors)
        {
            if (errors == null)
            {
                return false;
            }

            foreach (var error in errors)
            {
                if (!string.IsNullOrEmpty(error)
                    && error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        internal static string GetSupportedCSharpLanguageVersion()
        {
            return GetSupportedCSharpLanguageVersion(Application.unityVersion);
        }

        internal static string GetSupportedCSharpLanguageVersion(string unityVersion)
        {
            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                return CSharpVersion2019;
            }

            if (version.Major >= 2022 || version.Major >= 6000)
            {
                return CSharpVersion2021OrNewer;
            }

            if (version.Major == 2021)
            {
                return version.Minor >= 2 ? CSharpVersion2021OrNewer : CSharpVersion2020;
            }

            if (version.Major == 2020)
            {
                return version.Minor >= 2 ? CSharpVersion2020 : CSharpVersion2019;
            }

            return CSharpVersion2019;
        }

        private static bool TryParseUnityVersion(string unityVersion, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                return false;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < unityVersion.Length; i++)
            {
                var c = unityVersion[i];
                if (char.IsDigit(c) || c == '.')
                {
                    builder.Append(c);
                    continue;
                }

                break;
            }

            var versionText = builder.ToString().Trim('.');
            if (string.IsNullOrEmpty(versionText))
            {
                return false;
            }

            while (versionText.Split('.').Length < 2)
            {
                versionText += ".0";
            }

            return Version.TryParse(versionText, out version);
        }

        private static string BuildCompilerResponseFile(string sourcePath, string assemblyPath, string languageVersion, CodeCompilationTarget target)
        {
            var builder = new StringBuilder();
            builder.AppendLine("-nologo");
            builder.AppendLine("-target:library");
            builder.AppendLine("-langversion:" + languageVersion);
            builder.AppendLine("-unsafe-");
            builder.AppendLine("-out:\"" + assemblyPath + "\"");

            foreach (var reference in GetCompilationReferences(target))
            {
                builder.AppendLine("-reference:\"" + reference + "\"");
            }

            builder.AppendLine("\"" + sourcePath + "\"");
            return builder.ToString();
        }

        private static IEnumerable<string> GetCompilationReferences(CodeCompilationTarget target)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .Where(assembly => target == CodeCompilationTarget.Editor || IsRuntimeCompilationReference(assembly))
                .Select(assembly =>
                {
                    try
                    {
                        return assembly.Location;
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRuntimeCompilationReference(Assembly assembly)
        {
            if (assembly == null)
            {
                return false;
            }

            var name = assembly.GetName().Name ?? string.Empty;
            if (IsEditorAssemblyName(name) || IsTestAssemblyName(name))
            {
                return false;
            }

            AssemblyName[] references;
            try
            {
                references = assembly.GetReferencedAssemblies();
            }
            catch
            {
                references = new AssemblyName[0];
            }

            for (var i = 0; i < references.Length; i++)
            {
                var referenceName = references[i] == null ? string.Empty : references[i].Name;
                if (IsEditorAssemblyName(referenceName))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEditorAssemblyName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return string.Equals(name, "UnityEditor", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("UnityEditor.", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(".Editor.", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Editor.Tests", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTestAssemblyName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.IndexOf(".Tests", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryResolveCompiler(
            out CompilerInvocation compiler,
            out List<string> probePaths,
            out List<string> probeFailures)
        {
            compiler = null;
            var contentsPath = EditorApplication.applicationContentsPath;
            var candidates = GetCompilerCandidatePaths(contentsPath);
            probePaths = candidates.ToList();
            probeFailures = new List<string>();

            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var candidateCompiler = CreateCompilerInvocation(contentsPath, candidate);
                string failure;
                if (CanStartCompiler(candidateCompiler, CompilerProbeTimeoutMs, out failure))
                {
                    compiler = candidateCompiler;
                    return true;
                }

                probeFailures.Add("Skipped compiler path: " + candidate + " (" + failure + ")");
            }

            return false;
        }

        internal static string[] GetCompilerCandidatePaths(string contentsPath)
        {
            return new[]
            {
                // Unity 2019 的完整 Roslyn 工具链在 Tools/Roslyn；优先使用它，避免 mono/4.5/csc.exe 缺少 facade 依赖。
                Path.Combine(contentsPath, "Tools", "Roslyn", "csc.exe"),
                // Unity 6000 的 MonoBleedingEdge Roslyn exe 可能缺运行时依赖；优先使用随 Editor 提供的 .NET Roslyn。
                Path.Combine(contentsPath, "DotNetSdkRoslyn", "csc.dll"),
                Path.Combine(contentsPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn", "csc.exe"),
                Path.Combine(contentsPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe")
            };
        }

        private static CompilerInvocation CreateCompilerInvocation(string contentsPath, string candidate)
        {
            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return new CompilerInvocation
                {
                    CompilerPath = candidate,
                    FileName = ResolveDotNetProcessPath(contentsPath),
                    PrefixArguments = QuoteArgument(candidate)
                };
            }

            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
#if UNITY_EDITOR_WIN
                return new CompilerInvocation
                {
                    CompilerPath = candidate,
                    FileName = candidate,
                    PrefixArguments = string.Empty
                };
#else
                var monoPath = Path.Combine(contentsPath, "MonoBleedingEdge", "bin", "mono");
                return new CompilerInvocation
                {
                    CompilerPath = candidate,
                    FileName = File.Exists(monoPath) ? monoPath : candidate,
                    PrefixArguments = File.Exists(monoPath) ? QuoteArgument(candidate) : string.Empty
                };
#endif
            }

            return new CompilerInvocation
            {
                CompilerPath = candidate,
                FileName = candidate,
                PrefixArguments = string.Empty
            };
        }

        internal static string ResolveDotNetProcessPath(string contentsPath)
        {
#if UNITY_EDITOR_WIN
            var dotNetFileName = "dotnet.exe";
#else
            var dotNetFileName = "dotnet";
#endif
            var bundledDotNet = Path.Combine(contentsPath, "NetCoreRuntime", dotNetFileName);
            return File.Exists(bundledDotNet) ? bundledDotNet : FallbackCompilerProcessName;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value + "\"";
        }

        private static bool CanStartCompiler(CompilerInvocation compiler, int timeoutMs, out string failure)
        {
            failure = null;
            var arguments = BuildCompilerProcessArguments(compiler, "-help");
            var outputEncoding = ResolveCompilerOutputEncoding();
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(compiler.FileName) ? FallbackCompilerProcessName : compiler.FileName,
                Arguments = arguments,
                WorkingDirectory = GetProjectRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding
            };

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = startInfo;
                    var stdoutBuilder = new StringBuilder();
                    var stderrBuilder = new StringBuilder();
                    process.OutputDataReceived += (sender, args) => AppendCompilerProbeLine(stdoutBuilder, args.Data);
                    process.ErrorDataReceived += (sender, args) => AppendCompilerProbeLine(stderrBuilder, args.Data);

                    if (!process.Start())
                    {
                        failure = "probe process did not start";
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Ignore kill failures.
                        }

                        failure = "probe timed out after " + timeoutMs + "ms";
                        return false;
                    }

                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        return true;
                    }

                    var detail = FirstNonEmptyLine(stderrBuilder.ToString());
                    if (string.IsNullOrEmpty(detail))
                    {
                        detail = FirstNonEmptyLine(stdoutBuilder.ToString());
                    }

                    failure = string.IsNullOrEmpty(detail)
                        ? "probe exited with code " + process.ExitCode
                        : "probe exited with code " + process.ExitCode + ": " + detail;
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static void AppendCompilerProbeLine(StringBuilder builder, string line)
        {
            if (builder == null || line == null || builder.Length >= 2048)
            {
                return;
            }

            builder.AppendLine(line);
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    return line;
                }
            }

            return null;
        }

        private static string RunCompilerProcess(
            CompilerInvocation compiler,
            string responsePath,
            int timeoutMs,
            List<string> compileErrors,
            List<CompilerDiagnostic> diagnostics)
        {
            var arguments = BuildCompilerProcessArguments(compiler, "@\"" + responsePath + "\"");

            var outputBuilder = new StringBuilder();
            var outputEncoding = ResolveCompilerOutputEncoding();
            compiler.Arguments = arguments;
            compiler.OutputEncodingName = GetEncodingDisplayName(outputEncoding);
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(compiler.FileName) ? FallbackCompilerProcessName : compiler.FileName,
                Arguments = arguments,
                WorkingDirectory = GetProjectRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding
            };

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stdoutBuilder.AppendLine(args.Data);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stderrBuilder.AppendLine(args.Data);
                    }
                };

                if (!process.Start())
                {
                    compileErrors.Add("Failed to start C# compiler process.");
                    return string.Empty;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures.
                    }

                    compileErrors.Add("Compilation timed out after " + timeoutMs + "ms.");
                }
                else
                {
                    process.WaitForExit();
                }

                var stdout = stdoutBuilder.ToString();
                var stderr = stderrBuilder.ToString();
                outputBuilder.Append(stdout);
                outputBuilder.Append(stderr);
                CollectCompilerDiagnostics(stdout, compileErrors, diagnostics);
                CollectCompilerDiagnostics(stderr, compileErrors, diagnostics);
                if (process.HasExited && process.ExitCode != 0 && compileErrors.Count == 0)
                {
                    compileErrors.Add("Compiler exited with code " + process.ExitCode + ".");
                }
            }

            return outputBuilder.ToString();
        }

        private static string BuildCompilerProcessArguments(CompilerInvocation compiler, string suffixArgument)
        {
            return string.IsNullOrEmpty(compiler.PrefixArguments)
                ? suffixArgument
                : compiler.PrefixArguments + " " + suffixArgument;
        }

        internal static Encoding ResolveCompilerOutputEncoding()
        {
            // CLI、命令文件和结果文件统一使用 UTF-8；编译器输出也按同一编码读取，避免跨平台分叉。
            return Encoding.UTF8;
        }

        private static string GetEncodingDisplayName(Encoding encoding)
        {
            if (encoding == null)
            {
                return null;
            }

            return encoding.WebName + " (codePage " + encoding.CodePage + ")";
        }

        private static void CollectCompilerDiagnostics(
            string output,
            List<string> compileErrors,
            List<CompilerDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(output))
            {
                return;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                var diagnostic = ParseCompilerDiagnosticLine(line);
                if (diagnostic == null)
                {
                    continue;
                }

                if (diagnostics != null)
                {
                    diagnostics.Add(diagnostic);
                }

                if (string.Equals(diagnostic.severity, "error", StringComparison.OrdinalIgnoreCase))
                {
                    compileErrors.Add(line);
                }
            }
        }

        internal static List<CompilerDiagnostic> ParseCompilerDiagnostics(string output)
        {
            var diagnostics = new List<CompilerDiagnostic>();
            if (string.IsNullOrEmpty(output))
            {
                return diagnostics;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var diagnostic = ParseCompilerDiagnosticLine(lines[i].Trim());
                if (diagnostic != null)
                {
                    diagnostics.Add(diagnostic);
                }
            }

            return diagnostics;
        }

        private static CompilerDiagnostic ParseCompilerDiagnosticLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var match = CompilerDiagnosticPattern.Match(line);
            if (match.Success)
            {
                int lineNumber;
                int columnNumber;
                int.TryParse(match.Groups["line"].Value, out lineNumber);
                int.TryParse(match.Groups["column"].Value, out columnNumber);
                return new CompilerDiagnostic
                {
                    file = match.Groups["file"].Value,
                    line = lineNumber,
                    column = columnNumber,
                    severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    code = match.Groups["code"].Value,
                    message = match.Groups["message"].Value,
                    raw = line
                };
            }

            var severity = DetectCompilerSeverity(line);
            if (severity == null)
            {
                return null;
            }

            return new CompilerDiagnostic
            {
                severity = severity,
                message = line,
                raw = line
            };
        }

        private static string DetectCompilerSeverity(string line)
        {
            if (line.StartsWith("error ", StringComparison.OrdinalIgnoreCase)
                || line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "error";
            }

            if (line.StartsWith("warning ", StringComparison.OrdinalIgnoreCase)
                || line.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "warning";
            }

            return null;
        }

        private static InvocationResult InvokeCompiledCode(string assemblyPath, string className)
        {
            var bytes = File.ReadAllBytes(assemblyPath);
            var assembly = Assembly.Load(bytes);
            var type = assembly.GetType(className, true);
            var asyncMethod = type.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static);
            if (asyncMethod != null)
            {
                var task = asyncMethod.Invoke(null, null) as Task;
                if (task == null)
                {
                    throw new InvalidOperationException("ExecuteAsync did not return a Task.");
                }

                return new InvocationResult { Task = task };
            }

            var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                throw new MissingMethodException(className, "Execute");
            }

            return new InvocationResult { ReturnValue = method.Invoke(null, null) };
        }

        private static void OnAsyncUpdate()
        {
            if (_activeOperation == null)
            {
                EditorApplication.update -= OnAsyncUpdate;
                return;
            }

            CommandResult result;
            bool shouldRelease;
            if (!_activeOperation.Step(out result, out shouldRelease))
            {
                return;
            }

            if (result != null)
            {
                WriteResultFile(result);
            }

            if (shouldRelease)
            {
                _activeOperation = null;
                EditorApplication.update -= OnAsyncUpdate;
            }
        }

        private static void WriteResultFile(CommandResult result)
        {
            try
            {
                var resultsDir = Path.Combine(AIBridge.BridgeDirectory, "results");
                if (!Directory.Exists(resultsDir))
                {
                    Directory.CreateDirectory(resultsDir);
                }

                var filePath = Path.Combine(resultsDir, result.id + ".json");
                var json = AIBridgeJson.Serialize(result, true);
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError("Failed to write code result for " + result.id + ": " + ex.Message);
            }
        }

        private static object BuildResultData(
            CodeExecutionSession session,
            object returnValue,
            List<string> compileErrors,
            object exception,
            bool? timedOut,
            string status = null)
        {
            return new
            {
                enabled = true,
                status = status,
                source = session.Source.Kind,
                sourcePath = session.Source.Path,
                isAsync = session.Source.IsAsync,
                elapsedMs = session.Stopwatch.ElapsedMilliseconds,
                timeoutMs = session.TimeoutMs,
                timedOut = timedOut,
                returnValue = returnValue,
                logs = session.Logs,
                compileErrors = compileErrors ?? new List<string>(),
                diagnostics = session.CompilerDiagnostics ?? new List<CompilerDiagnostic>(),
                compilerOutput = session.CompilerOutput,
                compiler = BuildCompilerInfo(session),
                compilerProbePaths = session.CompilerProbePaths ?? new List<string>(),
                compilerProbeFailures = session.CompilerProbeFailures ?? new List<string>(),
                exception = exception
            };
        }

        private static object BuildRuntimeDispatchResultData(
            CodeExecutionSession session,
            int assemblyBytes,
            string sha256,
            RuntimeDispatchOptions options,
            RuntimeDispatchResult runtimeResult,
            string status,
            bool timedOut)
        {
            return new
            {
                enabled = true,
                status = status,
                source = session.Source.Kind,
                sourcePath = session.Source.Path,
                isAsync = session.Source.IsAsync,
                elapsedMs = session.Stopwatch.ElapsedMilliseconds,
                timeoutMs = session.TimeoutMs,
                timedOut = timedOut,
                compiledAssembly = new
                {
                    path = session.CompiledAssemblyPath,
                    bytes = assemblyBytes,
                    sha256 = sha256,
                    entryType = session.Source.ClassName,
                    methodName = session.Source.IsAsync ? "ExecuteAsync" : "Execute"
                },
                runtime = runtimeResult == null ? null : runtimeResult.ToData(),
                dispatch = options == null ? null : options.ToData(),
                compileErrors = new List<string>(),
                diagnostics = session.CompilerDiagnostics ?? new List<CompilerDiagnostic>(),
                compilerOutput = session.CompilerOutput,
                compiler = BuildCompilerInfo(session),
                compilerProbePaths = session.CompilerProbePaths ?? new List<string>(),
                compilerProbeFailures = session.CompilerProbeFailures ?? new List<string>()
            };
        }

        private static bool TryBuildRuntimeDispatchOptions(
            CommandRequest request,
            int timeoutMs,
            out RuntimeDispatchOptions options,
            out string error)
        {
            options = null;
            error = null;

            var transport = request.GetParam<string>("transport", "http");
            if (string.IsNullOrWhiteSpace(transport))
            {
                transport = "http";
            }

            transport = transport.Trim().ToLowerInvariant();
            var runtimeDirectory = ResolveRuntimeDirectory(request);
            var target = request.GetParam<string>("target", "latest");
            if (string.IsNullOrWhiteSpace(target))
            {
                target = "latest";
            }

            var token = request.GetParam<string>("token", null);
            options = new RuntimeDispatchOptions
            {
                Transport = transport,
                RuntimeDirectory = runtimeDirectory,
                Target = target,
                Token = token,
                TimeoutMs = timeoutMs,
                PollIntervalMs = RuntimeDispatchPollIntervalMs,
                RuntimeCommandId = request.id + "_runtime"
            };

            if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                var url = request.GetParam<string>("url", null);
                if (string.IsNullOrWhiteSpace(url)
                    && !TryResolveHttpUrlFromRuntimeDirectory(runtimeDirectory, target, out url))
                {
                    url = AIBridgeRuntimeBridgeEditorUtility.BuildLocalHttpUrl();
                }

                options.Url = NormalizeRuntimeHttpUrl(url);
                if (string.IsNullOrEmpty(options.Url))
                {
                    error = "Runtime HTTP url is empty. Provide --url or start a Runtime target with HTTP transport.";
                    return false;
                }

                return true;
            }

            if (string.Equals(transport, "file", StringComparison.OrdinalIgnoreCase))
            {
                RuntimeFileTargetInfo fileTarget;
                if (!TryResolveRuntimeFileTarget(runtimeDirectory, target, out fileTarget, out error))
                {
                    return false;
                }

                options.Target = fileTarget.TargetId;
                options.CommandPath = Path.Combine(fileTarget.CommandsPath, options.RuntimeCommandId + ".json");
                options.ResultPath = Path.Combine(fileTarget.ResultsPath, options.RuntimeCommandId + ".json");
                return true;
            }

            error = "Unsupported runtime transport: " + transport + ". Supported: http, file.";
            return false;
        }

        private static Dictionary<string, object> BuildRuntimeCodeCommand(
            CommandRequest request,
            SourceText sourceText,
            byte[] assemblyBytes,
            string sha256,
            RuntimeDispatchOptions options)
        {
            var parameters = new Dictionary<string, object>
            {
                ["assemblyBase64"] = Convert.ToBase64String(assemblyBytes),
                ["sha256"] = sha256,
                ["entryType"] = sourceText.ClassName,
                ["methodName"] = sourceText.IsAsync ? "ExecuteAsync" : "Execute",
                ["riskAccepted"] = true,
                ["sourceKind"] = sourceText.Kind,
                ["sourcePath"] = sourceText.Path,
                ["requestParams"] = BuildRuntimeUserParams(request)
            };

            return new Dictionary<string, object>
            {
                ["id"] = options.RuntimeCommandId,
                ["type"] = "runtime",
                ["action"] = RuntimeCodeExecuteAction,
                ["token"] = options.Token ?? string.Empty,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["params"] = parameters
            };
        }

        private static Dictionary<string, object> BuildRuntimeUserParams(CommandRequest request)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (request == null || request.@params == null)
            {
                return result;
            }

            foreach (var pair in request.@params)
            {
                if (IsRuntimeDispatchReservedParam(pair.Key))
                {
                    continue;
                }

                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static bool IsRuntimeDispatchReservedParam(string key)
        {
            return string.Equals(key, "action", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "timeout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "transport", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "target", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "runtime-dir", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "runtimeDir", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "url", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "token", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveRuntimeDirectory(CommandRequest request)
        {
            var runtimeDirectory = request.GetParam<string>("runtime-dir", null);
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                runtimeDirectory = request.GetParam<string>("runtimeDir", null);
            }

            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                return AIBridgeRuntimeBridgeEditorUtility.GetRuntimeDirectory();
            }

            return Path.IsPathRooted(runtimeDirectory)
                ? Path.GetFullPath(runtimeDirectory)
                : Path.GetFullPath(Path.Combine(GetProjectRoot(), runtimeDirectory));
        }

        private static string NormalizeRuntimeHttpUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return url.Trim().TrimEnd('/');
        }

        private static bool TryResolveHttpUrlFromRuntimeDirectory(string runtimeDirectory, string target, out string url)
        {
            url = null;
            RuntimeFileTargetInfo fileTarget;
            string error;
            if (!TryResolveRuntimeFileTarget(runtimeDirectory, target, out fileTarget, out error)
                || fileTarget == null
                || fileTarget.Stale
                || string.IsNullOrWhiteSpace(fileTarget.HttpUrl))
            {
                return false;
            }

            url = fileTarget.HttpUrl;
            return true;
        }

        private static bool TryResolveRuntimeFileTarget(
            string runtimeDirectory,
            string target,
            out RuntimeFileTargetInfo targetInfo,
            out string error)
        {
            targetInfo = null;
            error = null;

            var targetsRoot = Path.Combine(runtimeDirectory, AIBridgeRuntimeBridgeEditorUtility.TargetsDirectoryName);
            if (!Directory.Exists(targetsRoot))
            {
                error = "Runtime targets directory not found: " + targetsRoot;
                return false;
            }

            var targets = new List<RuntimeFileTargetInfo>();
            var directories = Directory.GetDirectories(targetsRoot);
            for (var i = 0; i < directories.Length; i++)
            {
                var directory = directories[i];
                var heartbeatPath = Path.Combine(directory, AIBridgeRuntimeBridgeEditorUtility.HeartbeatFileName);
                var heartbeat = ReadJsonObject(heartbeatPath);
                var targetId = GetDictionaryString(heartbeat, "targetId");
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    targetId = Path.GetFileName(directory);
                }

                var lastHeartbeat = ParseUtcTime(GetDictionaryString(heartbeat, "lastHeartbeatUtc"));
                var stale = !lastHeartbeat.HasValue || DateTime.UtcNow - lastHeartbeat.Value > TimeSpan.FromSeconds(15);
                targets.Add(new RuntimeFileTargetInfo
                {
                    TargetId = targetId,
                    TargetPath = directory,
                    CommandsPath = GetDictionaryString(heartbeat, "commandsPath") ?? Path.Combine(directory, AIBridgeRuntimeBridgeEditorUtility.CommandsDirectoryName),
                    ResultsPath = GetDictionaryString(heartbeat, "resultsPath") ?? Path.Combine(directory, AIBridgeRuntimeBridgeEditorUtility.ResultsDirectoryName),
                    HttpUrl = GetDictionaryString(heartbeat, "httpUrl"),
                    LastHeartbeatUtc = lastHeartbeat,
                    Stale = stale
                });
            }

            if (targets.Count == 0)
            {
                error = "No Runtime targets found under: " + targetsRoot;
                return false;
            }

            targets = targets
                .OrderBy(item => item.Stale)
                .ThenByDescending(item => item.LastHeartbeatUtc.HasValue ? item.LastHeartbeatUtc.Value : DateTime.MinValue)
                .ThenBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "latest", StringComparison.OrdinalIgnoreCase))
            {
                targetInfo = targets[0];
                return true;
            }

            targetInfo = targets.FirstOrDefault(item => string.Equals(item.TargetId, target, StringComparison.OrdinalIgnoreCase));
            if (targetInfo != null)
            {
                return true;
            }

            error = "Runtime target was not found: " + target;
            return false;
        }

        private static Dictionary<string, object> ReadJsonObject(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return AIBridgeJson.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static string GetDictionaryString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static DateTime? ParseUtcTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                return parsed.UtcDateTime;
            }

            return null;
        }

        private static RuntimeDispatchResult DispatchRuntimeCommand(Dictionary<string, object> command, RuntimeDispatchOptions options)
        {
            return string.Equals(options.Transport, "file", StringComparison.OrdinalIgnoreCase)
                ? DispatchRuntimeFileCommand(command, options)
                : DispatchRuntimeHttpCommand(command, options);
        }

        private static RuntimeDispatchResult DispatchRuntimeHttpCommand(Dictionary<string, object> command, RuntimeDispatchOptions options)
        {
            var endpoint = options.Url + RuntimeHttpCommandsPath + "?timeoutMs=" + Math.Max(100, options.TimeoutMs).ToString(CultureInfo.InvariantCulture);
            var body = AIBridgeJson.Serialize(command, pretty: false);
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = Math.Max(1000, options.TimeoutMs + 1000);
                request.ReadWriteTimeout = request.Timeout;
                if (!string.IsNullOrWhiteSpace(options.Token))
                {
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + options.Token;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return ParseRuntimeDispatchResponse(reader.ReadToEnd(), options, null);
                }
            }
            catch (WebException ex)
            {
                var responseBody = ReadWebExceptionBody(ex);
                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    return ParseRuntimeDispatchResponse(responseBody, options, ex.Message);
                }

                return RuntimeDispatchResult.Failure(options, "HTTP runtime dispatch failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                return RuntimeDispatchResult.Failure(options, "HTTP runtime dispatch failed: " + ex.Message);
            }
        }

        private static RuntimeDispatchResult DispatchRuntimeFileCommand(Dictionary<string, object> command, RuntimeDispatchOptions options)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.CommandPath));
                Directory.CreateDirectory(Path.GetDirectoryName(options.ResultPath));

                var json = AIBridgeJson.Serialize(command, pretty: false);
                var tempPath = options.CommandPath + ".tmp";
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(options.CommandPath))
                {
                    File.Delete(options.CommandPath);
                }

                File.Move(tempPath, options.CommandPath);

                var startTime = DateTime.UtcNow;
                while ((DateTime.UtcNow - startTime).TotalMilliseconds < options.TimeoutMs)
                {
                    if (File.Exists(options.ResultPath))
                    {
                        Thread.Sleep(10);
                        var resultJson = File.ReadAllText(options.ResultPath, Encoding.UTF8);
                        try { File.Delete(options.ResultPath); } catch { }
                        return ParseRuntimeDispatchResponse(resultJson, options, null);
                    }

                    Thread.Sleep(options.PollIntervalMs);
                }

                try { if (File.Exists(options.CommandPath)) File.Delete(options.CommandPath); } catch { }
                return RuntimeDispatchResult.Failure(options, "Timeout waiting for runtime result after " + options.TimeoutMs + "ms.");
            }
            catch (Exception ex)
            {
                return RuntimeDispatchResult.Failure(options, "File runtime dispatch failed: " + ex.Message);
            }
        }

        private static RuntimeDispatchResult ParseRuntimeDispatchResponse(string json, RuntimeDispatchOptions options, string transportWarning)
        {
            try
            {
                var data = AIBridgeJson.DeserializeObject(json);
                var success = ReadBool(data, "Success") ?? ReadBool(data, "success") ?? false;
                var error = ReadString(data, "Error") ?? ReadString(data, "error");
                return new RuntimeDispatchResult
                {
                    Success = success,
                    Error = string.IsNullOrWhiteSpace(error) ? transportWarning : error,
                    Result = data,
                    Transport = options.Transport,
                    Target = options.Target,
                    Url = options.Url,
                    RuntimeDirectory = options.RuntimeDirectory,
                    RuntimeCommandId = options.RuntimeCommandId,
                    CommandPath = options.CommandPath,
                    ResultPath = options.ResultPath
                };
            }
            catch (Exception ex)
            {
                return RuntimeDispatchResult.Failure(options, "Failed to parse runtime result: " + ex.Message);
            }
        }

        private static bool? ReadBool(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) ? parsed : (bool?)null;
        }

        private static string ReadString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static string ReadWebExceptionBody(WebException ex)
        {
            if (ex == null || ex.Response == null)
            {
                return null;
            }

            try
            {
                using (var stream = ex.Response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static object BuildCompilerInfo(CodeExecutionSession session)
        {
            if (session == null || session.Compiler == null)
            {
                return null;
            }

            return new
            {
                compilerPath = session.Compiler.CompilerPath,
                processPath = session.Compiler.FileName,
                arguments = session.Compiler.Arguments,
                outputEncoding = session.Compiler.OutputEncodingName
            };
        }

        private static object BuildExceptionInfo(Exception ex)
        {
            var targetInvocation = ex as TargetInvocationException;
            if (targetInvocation != null && targetInvocation.InnerException != null)
            {
                ex = targetInvocation.InnerException;
            }

            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerException != null)
            {
                ex = aggregate.InnerException;
            }

            return new
            {
                type = ex.GetType().FullName,
                message = ex.Message,
                stackTrace = ex.StackTrace
            };
        }

        internal static object NormalizeReturnValue(object value)
        {
            return NormalizeReturnValue(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static object NormalizeReturnValue(object value, int depth, HashSet<object> visited)
        {
            if (value == null)
            {
                return null;
            }

            if (depth > MaxNormalizedDepth)
            {
                return new
                {
                    type = value.GetType().FullName,
                    value = "<max depth reached>"
                };
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal)
            {
                return value;
            }

            if (value is DateTime dateTime)
            {
                return dateTime.ToString("o", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
            }

            if (value is TimeSpan timeSpan)
            {
                return timeSpan.ToString();
            }

            if (value is Guid)
            {
                return value.ToString();
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
            {
                return new
                {
                    type = unityObject.GetType().FullName,
                    name = unityObject.name,
                    instanceId = unityObject.GetInstanceID()
                };
            }

            var generationResult = value as AIBridgeGenerationResult;
            if (generationResult != null)
            {
                return NormalizeGenerationResult(generationResult);
            }

            if (value is Vector2 vector2)
            {
                return new { x = vector2.x, y = vector2.y };
            }

            if (value is Vector3 vector3)
            {
                return new { x = vector3.x, y = vector3.y, z = vector3.z };
            }

            if (value is Vector4 vector4)
            {
                return new { x = vector4.x, y = vector4.y, z = vector4.z, w = vector4.w };
            }

            if (value is Quaternion quaternion)
            {
                return new { x = quaternion.x, y = quaternion.y, z = quaternion.z, w = quaternion.w };
            }

            if (value is Color color)
            {
                return new { r = color.r, g = color.g, b = color.b, a = color.a };
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                return NormalizeDictionary(dictionary, depth, visited);
            }

            if (value is IEnumerable && !(value is string))
            {
                return NormalizeEnumerable((IEnumerable)value, depth, visited);
            }

            if (IsStructuredReturnType(type))
            {
                return NormalizeStructuredObject(value, type, depth, visited);
            }

            return new
            {
                type = type.FullName,
                value = value.ToString()
            };
        }

        private static object NormalizeGenerationResult(AIBridgeGenerationResult result)
        {
            return new
            {
                assets = result.assets,
                prefabs = result.prefabs,
                scenes = result.scenes,
                warnings = result.warnings,
                messages = result.messages
            };
        }

        private static object NormalizeDictionary(IDictionary dictionary, int depth, HashSet<object> visited)
        {
            if (!TryEnterReference(dictionary, visited))
            {
                return new
                {
                    type = dictionary.GetType().FullName,
                    value = "<cycle>"
                };
            }

            var result = new Dictionary<string, object>();
            try
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                    result[key] = NormalizeReturnValue(entry.Value, depth + 1, visited);
                }

                return result;
            }
            finally
            {
                visited.Remove(dictionary);
            }
        }

        private static object NormalizeEnumerable(IEnumerable enumerable, int depth, HashSet<object> visited)
        {
            if (!TryEnterReference(enumerable, visited))
            {
                return new
                {
                    type = enumerable.GetType().FullName,
                    value = "<cycle>"
                };
            }

            var result = new List<object>();
            var count = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (count >= MaxNormalizedCollectionItems)
                    {
                        result.Add("<truncated after " + MaxNormalizedCollectionItems + " items>");
                        break;
                    }

                    result.Add(NormalizeReturnValue(item, depth + 1, visited));
                    count++;
                }

                return result;
            }
            finally
            {
                visited.Remove(enumerable);
            }
        }

        private static object NormalizeStructuredObject(object value, Type type, int depth, HashSet<object> visited)
        {
            if (!TryEnterReference(value, visited))
            {
                return new
                {
                    type = type.FullName,
                    value = "<cycle>"
                };
            }

            // 返回值可能来自临时脚本的匿名对象或 [Serializable] DTO；这里只展开公开成员，并跳过读取失败的属性。
            var result = new Dictionary<string, object>();
            try
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    result[field.Name] = NormalizeReturnValue(field.GetValue(value), depth + 1, visited);
                }

                var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                for (var i = 0; i < properties.Length; i++)
                {
                    var property = properties[i];
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    try
                    {
                        result[property.Name] = NormalizeReturnValue(property.GetValue(value, null), depth + 1, visited);
                    }
                    catch
                    {
                        result[property.Name] = "<property read failed>";
                    }
                }

                return result;
            }
            finally
            {
                visited.Remove(value);
            }
        }

        private static bool IsStructuredReturnType(Type type)
        {
            if (type == null || type.IsPrimitive || type.IsEnum)
            {
                return false;
            }

            if (type.IsSerializable)
            {
                return true;
            }

            return IsAnonymousType(type);
        }

        private static bool IsAnonymousType(Type type)
        {
            return type != null
                   && Attribute.IsDefined(type, typeof(CompilerGeneratedAttribute), false)
                   && type.IsGenericType
                   && type.Name.IndexOf("AnonymousType", StringComparison.Ordinal) >= 0;
        }

        private static bool TryEnterReference(object value, HashSet<object> visited)
        {
            var type = value.GetType();
            if (type.IsValueType)
            {
                return true;
            }

            return visited.Add(value);
        }

        private static CommandResult FailureWithData(string requestId, string error, object data)
        {
            return new CommandResult
            {
                id = requestId,
                success = false,
                error = error,
                data = data
            };
        }

        private static object BuildSettingsGateData(AIBridgeProjectSettings settings)
        {
            return new
            {
                enabled = settings.EnableCodeExecution,
                riskAccepted = settings.CodeExecutionRiskAccepted,
                source = "none"
            };
        }

        private static object BuildRuntimeCodeUnavailableData(AIBridgeProjectSettings settings)
        {
            return new
            {
                enabled = true,
                runtimeCodeExecutionSetting = settings.RuntimeBridge.EnableRuntimeCodeExecution,
                hybridClrPackage = AIBridgeHybridClrUtility.PackageName,
                hybridClrInstalled = false,
                requiredDefine = AIBridgeHybridClrUtility.HybridClrAvailableDefine,
                source = "none"
            };
        }

        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static string ResolveCodeFilePath(string file)
        {
            if (Path.IsPathRooted(file))
            {
                return Path.GetFullPath(file);
            }

            // Unity Editor 的当前工作目录在不同启动方式下不稳定，文件来源统一按项目根目录解析。
            return Path.GetFullPath(Path.Combine(GetProjectRoot(), file));
        }

        private static bool IsSameOrChildPath(string rootDirectory, string fullPath)
        {
            var normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private enum CodeCompilationTarget
        {
            Editor,
            Runtime
        }

        private sealed class SourceText
        {
            public string Kind;
            public string Path;
            public string Code;
            public string WrappedCode;
            public string ClassName;
            public bool IsAsync;
            public CodeCompilationTarget Target;
        }

        private sealed class CompilerInvocation
        {
            public string CompilerPath;
            public string FileName;
            public string PrefixArguments;
            public string Arguments;
            public string OutputEncodingName;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }

        private sealed class InvocationResult
        {
            public object ReturnValue;
            public Task Task;
        }

        [Serializable]
        private sealed class CapturedLog
        {
            public string type;
            public string message;
            public string stackTrace;
        }

        [Serializable]
        internal sealed class CompilerDiagnostic
        {
            public string file;
            public int line;
            public int column;
            public string severity;
            public string code;
            public string message;
            public string raw;
        }

        private sealed class CodeExecutionSession
        {
            public CodeExecutionSession(string requestId, SourceText source, int timeoutMs, Stopwatch stopwatch)
            {
                RequestId = requestId;
                Source = source;
                TimeoutMs = timeoutMs;
                Stopwatch = stopwatch;
                Logs = new List<CapturedLog>();
                CompilerDiagnostics = new List<CompilerDiagnostic>();
            }

            public string RequestId;
            public SourceText Source;
            public int TimeoutMs;
            public Stopwatch Stopwatch;
            public List<CapturedLog> Logs;
            public string CompiledAssemblyPath;
            public string CompilerOutput;
            public CompilerInvocation Compiler;
            public List<string> CompilerProbePaths;
            public List<string> CompilerProbeFailures;
            public List<CompilerDiagnostic> CompilerDiagnostics;

            public void OnLogMessageReceived(string condition, string stackTrace, LogType type)
            {
                Logs.Add(new CapturedLog
                {
                    type = type.ToString(),
                    message = condition,
                    stackTrace = stackTrace
                });
            }
        }

        private interface ICodeOperation
        {
            string RequestId { get; }
            bool Step(out CommandResult result, out bool shouldRelease);
            CommandResult CancelForOriginalRequest();
            object BuildStatusData();
        }

        private sealed class CodeAsyncOperation : ICodeOperation
        {
            private readonly CodeExecutionSession _session;
            private readonly Task _task;
            private bool _resultWritten;
            private bool _timedOut;
            private bool _cancelRequested;

            public CodeAsyncOperation(CodeExecutionSession session, Task task)
            {
                _session = session;
                _task = task;
            }

            public string RequestId => _session.RequestId;

            public bool Step(out CommandResult result, out bool shouldRelease)
            {
                result = null;
                shouldRelease = false;

                if (_task.IsCompleted)
                {
                    Application.logMessageReceived -= _session.OnLogMessageReceived;
                    if (_session.Stopwatch.IsRunning)
                    {
                        _session.Stopwatch.Stop();
                    }

                    shouldRelease = true;
                    if (_resultWritten)
                    {
                        return true;
                    }

                    if (_task.IsFaulted)
                    {
                        result = FailureWithData(
                            _session.RequestId,
                            "Code execution failed.",
                            BuildResultData(_session, null, new List<string>(), BuildExceptionInfo(_task.Exception), false, "failed"));
                    }
                    else if (_task.IsCanceled)
                    {
                        result = FailureWithData(
                            _session.RequestId,
                            "Code execution was canceled.",
                            BuildResultData(_session, null, new List<string>(), null, false, "canceled"));
                    }
                    else
                    {
                        result = CommandResult.Success(
                            _session.RequestId,
                            BuildResultData(_session, NormalizeReturnValue(GetTaskResult(_task)), new List<string>(), null, false, "completed"));
                    }

                    _resultWritten = true;
                    return true;
                }

                if (!_timedOut && _session.Stopwatch.ElapsedMilliseconds >= _session.TimeoutMs)
                {
                    Application.logMessageReceived -= _session.OnLogMessageReceived;
                    _timedOut = true;
                    result = FailureWithData(
                        _session.RequestId,
                        "Code execution timed out after " + _session.TimeoutMs + "ms.",
                        BuildResultData(_session, null, new List<string>(), null, true, "timed_out_waiting_for_task"));
                    _resultWritten = true;
                    return true;
                }

                return false;
            }

            public CommandResult CancelForOriginalRequest()
            {
                _cancelRequested = true;
                Application.logMessageReceived -= _session.OnLogMessageReceived;
                if (_session.Stopwatch.IsRunning)
                {
                    _session.Stopwatch.Stop();
                }

                if (_resultWritten)
                {
                    return null;
                }

                _resultWritten = true;
                return FailureWithData(
                    _session.RequestId,
                    "Code execution was canceled.",
                    BuildResultData(_session, null, new List<string>(), null, false, "canceled"));
            }

            public object BuildStatusData()
            {
                return new
                {
                    active = true,
                    busy = true,
                    status = GetStatusText(),
                    requestId = _session.RequestId,
                    source = _session.Source.Kind,
                    sourcePath = _session.Source.Path,
                    isAsync = _session.Source.IsAsync,
                    elapsedMs = _session.Stopwatch.ElapsedMilliseconds,
                    timeoutMs = _session.TimeoutMs,
                    timedOut = _timedOut,
                    resultWritten = _resultWritten,
                    canCancel = true,
                    hint = _timedOut
                        ? "The CLI result has timed out, but the async Task has not completed yet. Wait for status to become idle or run `code cancel`."
                        : "A code execution is running. Run `code status` again later or `code cancel` to release AIBridge waiting state."
                };
            }

            private string GetStatusText()
            {
                if (_cancelRequested)
                {
                    return "cancel_requested";
                }

                if (_timedOut)
                {
                    return "timed_out_waiting_for_task";
                }

                return "running";
            }

            private static object GetTaskResult(Task task)
            {
                var type = task.GetType();
                if (!type.IsGenericType)
                {
                    return null;
                }

                var resultProperty = type.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                return resultProperty != null ? resultProperty.GetValue(task, null) : null;
            }
        }

        private sealed class RuntimeCodeAsyncOperation : ICodeOperation
        {
            private readonly CodeExecutionSession _session;
            private readonly Task<RuntimeDispatchResult> _task;
            private readonly RuntimeDispatchOptions _options;
            private readonly int _assemblyBytes;
            private readonly string _sha256;
            private bool _resultWritten;
            private bool _timedOut;
            private bool _cancelRequested;

            public RuntimeCodeAsyncOperation(
                CodeExecutionSession session,
                Dictionary<string, object> command,
                RuntimeDispatchOptions options,
                int assemblyBytes,
                string sha256)
            {
                _session = session;
                _options = options;
                _assemblyBytes = assemblyBytes;
                _sha256 = sha256;
                // Runtime Player 可能就是当前 Editor Play Mode；转发和等待放到后台线程，避免阻塞主线程处理 Runtime 命令。
                _task = Task.Run(() => DispatchRuntimeCommand(command, options));
            }

            public string RequestId => _session.RequestId;

            public bool Step(out CommandResult result, out bool shouldRelease)
            {
                result = null;
                shouldRelease = false;

                if (_task.IsCompleted)
                {
                    if (_session.Stopwatch.IsRunning)
                    {
                        _session.Stopwatch.Stop();
                    }

                    shouldRelease = true;
                    if (_resultWritten)
                    {
                        return true;
                    }

                    if (_task.IsFaulted)
                    {
                        result = FailureWithData(
                            _session.RequestId,
                            "Runtime code dispatch failed.",
                            BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, null, "failed", false));
                    }
                    else if (_task.IsCanceled)
                    {
                        result = FailureWithData(
                            _session.RequestId,
                            "Runtime code dispatch was canceled.",
                            BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, null, "canceled", false));
                    }
                    else
                    {
                        var runtimeResult = _task.Result;
                        if (runtimeResult != null && runtimeResult.Success)
                        {
                            result = CommandResult.Success(
                                _session.RequestId,
                                BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, runtimeResult, "completed", false));
                        }
                        else
                        {
                            result = FailureWithData(
                                _session.RequestId,
                                runtimeResult == null ? "Runtime code execution failed." : runtimeResult.Error,
                                BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, runtimeResult, "runtime_failed", false));
                        }
                    }

                    _resultWritten = true;
                    return true;
                }

                if (!_timedOut && _session.Stopwatch.ElapsedMilliseconds >= _session.TimeoutMs)
                {
                    _timedOut = true;
                    result = FailureWithData(
                        _session.RequestId,
                        "Runtime code execution timed out after " + _session.TimeoutMs + "ms.",
                        BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, null, "timed_out_waiting_for_runtime", true));
                    _resultWritten = true;
                    shouldRelease = true;
                    return true;
                }

                return false;
            }

            public CommandResult CancelForOriginalRequest()
            {
                _cancelRequested = true;
                if (_session.Stopwatch.IsRunning)
                {
                    _session.Stopwatch.Stop();
                }

                if (_resultWritten)
                {
                    return null;
                }

                _resultWritten = true;
                return FailureWithData(
                    _session.RequestId,
                    "Runtime code execution was canceled.",
                    BuildRuntimeDispatchResultData(_session, _assemblyBytes, _sha256, _options, null, "canceled", false));
            }

            public object BuildStatusData()
            {
                return new
                {
                    active = true,
                    busy = true,
                    status = GetStatusText(),
                    requestId = _session.RequestId,
                    runtimeCommandId = _options.RuntimeCommandId,
                    source = _session.Source.Kind,
                    sourcePath = _session.Source.Path,
                    elapsedMs = _session.Stopwatch.ElapsedMilliseconds,
                    timeoutMs = _session.TimeoutMs,
                    timedOut = _timedOut,
                    resultWritten = _resultWritten,
                    dispatch = _options.ToData(),
                    canCancel = true,
                    hint = _timedOut
                        ? "The CLI result has timed out, but runtime dispatch may still finish on the target."
                        : "Runtime code execution is waiting for the target Player result."
                };
            }

            private string GetStatusText()
            {
                if (_cancelRequested)
                {
                    return "cancel_requested";
                }

                if (_timedOut)
                {
                    return "timed_out_waiting_for_runtime";
                }

                return "running_runtime";
            }
        }

        private sealed class RuntimeDispatchOptions
        {
            public string Transport;
            public string RuntimeDirectory;
            public string Target;
            public string Url;
            public string Token;
            public int TimeoutMs;
            public int PollIntervalMs;
            public string RuntimeCommandId;
            public string CommandPath;
            public string ResultPath;

            public object ToData()
            {
                return new
                {
                    transport = Transport,
                    runtimeDirectory = RuntimeDirectory,
                    target = Target,
                    url = Url,
                    timeoutMs = TimeoutMs,
                    pollIntervalMs = PollIntervalMs,
                    runtimeCommandId = RuntimeCommandId,
                    commandPath = CommandPath,
                    resultPath = ResultPath
                };
            }
        }

        private sealed class RuntimeDispatchResult
        {
            public bool Success;
            public string Error;
            public Dictionary<string, object> Result;
            public string Transport;
            public string RuntimeDirectory;
            public string Target;
            public string Url;
            public string RuntimeCommandId;
            public string CommandPath;
            public string ResultPath;

            public static RuntimeDispatchResult Failure(RuntimeDispatchOptions options, string error)
            {
                return new RuntimeDispatchResult
                {
                    Success = false,
                    Error = error,
                    Transport = options == null ? null : options.Transport,
                    RuntimeDirectory = options == null ? null : options.RuntimeDirectory,
                    Target = options == null ? null : options.Target,
                    Url = options == null ? null : options.Url,
                    RuntimeCommandId = options == null ? null : options.RuntimeCommandId,
                    CommandPath = options == null ? null : options.CommandPath,
                    ResultPath = options == null ? null : options.ResultPath
                };
            }

            public object ToData()
            {
                return new
                {
                    success = Success,
                    error = Error,
                    transport = Transport,
                    runtimeDirectory = RuntimeDirectory,
                    target = Target,
                    url = Url,
                    runtimeCommandId = RuntimeCommandId,
                    commandPath = CommandPath,
                    resultPath = ResultPath,
                    result = Result
                };
            }
        }

        private sealed class RuntimeFileTargetInfo
        {
            public string TargetId;
            public string TargetPath;
            public string CommandsPath;
            public string ResultsPath;
            public string HttpUrl;
            public DateTime? LastHeartbeatUtc;
            public bool Stale;
        }
    }
}
