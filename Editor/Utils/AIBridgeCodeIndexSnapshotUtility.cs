using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
#else
#pragma warning disable 0649
#endif

namespace AIBridge.Editor
{
    internal static class AIBridgeCodeIndexSnapshotUtility
    {
        private const int SchemaVersion = 2;
        private const int ManifestFormatKind = 1;
        private const int AssemblyFormatKind = 2;
        private const int TextIndexFormatKind = 3;
        private const string Magic = "AIBCI";
        private const string SnapshotDirectoryName = "snapshot";
        private const string AssembliesDirectoryName = "assemblies";
        private const string IndexDirectoryName = "index";
        private const string NamesDirectoryName = "names";
        private const string TokensDirectoryName = "tokens";
        private const string ManifestBinFileName = "manifest.bin";
        private const string ManifestJsonFileName = "manifest.json";
        private const string AssemblySnapshotExtension = ".bin";
        private const string IndexExtension = ".idx";
        private const string TempDirectoryName = "temp";
        private const string CompilerInputFileName = "compiler-input.json";
        private const int StartupMaxWorkerCount = 2;
        private const int ManualMaxWorkerCount = 4;
        private const int SnapshotWorkerTimeoutMs = 600000;
        private static readonly Regex TokenRegex = new Regex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

#if UNITY_EDITOR
        public static string GetSnapshotDirectory()
        {
            return Path.Combine(AIBridgeCodeIndexEditorUtility.GetIndexDirectory(), SnapshotDirectoryName);
        }

        public static bool GenerateSnapshot(out string message)
        {
            try
            {
                return GenerateSnapshot(CreateSnapshotRequest(manual: true, reason: "manualSnapshot"), out message);
            }
            catch (Exception ex)
            {
                message = GetExceptionMessage(ex);
                return false;
            }
        }

        public static Task<SnapshotResult> GenerateSnapshotAsync(bool manual, string reason)
        {
            var request = CreateSnapshotRequest(manual, reason);
            var inputPath = WriteSnapshotCompilerInput(request);
            var cliPath = AIBridgeCodeIndexEditorUtility.ResolveCliPath();
            return Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(cliPath))
                {
                    return RunExternalSnapshotWorker(cliPath, inputPath, request);
                }

                string message = null;
                SnapshotResult result = null;
                RunWithSnapshotThreadPriority(request.Manual, () =>
                {
                    // 仅源码开发环境缺少已发布 CLI 时回退；正常路径应由外部 worker 执行重 IO/CPU 工作。
                    var success = GenerateSnapshot(request, out message);
                    result = new SnapshotResult(success, message + ", fallback=in-process");
                });
                return result;
            });
        }

        private static SnapshotResult RunExternalSnapshotWorker(string cliPath, string inputPath, SnapshotRequest request)
        {
            try
            {
                var arguments = "code_index build_snapshot"
                                + " --input " + QuoteCliArgument(inputPath)
                                + " --timeout " + SnapshotWorkerTimeoutMs.ToString(CultureInfo.InvariantCulture)
                                + " --priority " + (request.Manual ? "normal" : "low")
                                + " --workers " + Math.Max(1, request.WorkerCount).ToString(CultureInfo.InvariantCulture)
                                + " --project-root " + QuoteCliArgument(request.ProjectRoot);
                if (request.OwnerPid > 0)
                {
                    arguments += " --owner-pid " + request.OwnerPid.ToString(CultureInfo.InvariantCulture);
                }

                if (request.OwnerStartTicks > 0L)
                {
                    arguments += " --owner-start-ticks " + request.OwnerStartTicks.ToString(CultureInfo.InvariantCulture);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = arguments,
                    WorkingDirectory = request.ProjectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new SnapshotResult(false, "snapshot worker did not start");
                    }

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(SnapshotWorkerTimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return new SnapshotResult(false, "snapshot worker timed out after " + SnapshotWorkerTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms");
                    }

                    var stdout = stdoutTask.GetAwaiter().GetResult();
                    var stderr = stderrTask.GetAwaiter().GetResult();
                    return BuildWorkerResult(process.ExitCode, stdout, stderr);
                }
            }
            catch (Exception ex)
            {
                return new SnapshotResult(false, GetExceptionMessage(ex));
            }
        }

        private static SnapshotResult BuildWorkerResult(int exitCode, string stdout, string stderr)
        {
            var success = exitCode == 0 && ReadJsonBool(stdout, "success", true);
            var message = ReadJsonString(stdout, "message");
            if (string.IsNullOrWhiteSpace(message))
            {
                message = string.IsNullOrWhiteSpace(stderr) ? (stdout ?? string.Empty).Trim() : stderr.Trim();
            }

            return new SnapshotResult(success, message);
        }

        private static string WriteSnapshotCompilerInput(SnapshotRequest request)
        {
            var directory = Path.Combine(AIBridgeCodeIndexEditorUtility.GetIndexDirectory(), TempDirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, CompilerInputFileName);
            var builder = new StringBuilder();
            builder.Append("{\n");
            AppendJsonProperty(builder, "schemaVersion", SchemaVersion, true);
            AppendJsonProperty(builder, "projectRoot", request.ProjectRoot, true);
            AppendJsonProperty(builder, "snapshotDirectory", request.SnapshotDirectory, true);
            AppendJsonProperty(builder, "unityVersion", request.UnityVersion, true);
            AppendJsonProperty(builder, "buildTarget", request.BuildTarget, true);
            AppendJsonProperty(builder, "manual", request.Manual, true);
            AppendJsonProperty(builder, "reason", request.Reason, true);
            AppendJsonProperty(builder, "workerCount", request.WorkerCount, true);
            AppendJsonProperty(builder, "ownerPid", request.OwnerPid, true);
            AppendJsonProperty(builder, "ownerStartTicks", request.OwnerStartTicks, true);
            AppendSnapshotFilterJson(builder, request.FilterConfig);
            builder.Append(",\n");
            builder.Append("  \"assemblies\": [\n");
            for (var i = 0; i < request.Assemblies.Count; i++)
            {
                AppendAssemblyInputJson(builder, request.Assemblies[i]);
                if (i + 1 < request.Assemblies.Count)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n");
            builder.Append("}\n");
            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            return path;
        }

        private static void AppendSnapshotFilterJson(StringBuilder builder, SnapshotFilterConfig config)
        {
            builder.Append("  \"filterConfig\": {\n");
            AppendJsonProperty(builder, "includePackageCacheSourceAssemblies", config != null && config.IncludePackageCacheSourceAssemblies, true, 4);
            AppendJsonArray(builder, "ignoredAssemblyPatterns", config == null ? null : config.IgnoredAssemblyPatterns, true, 4);
            AppendJsonArray(builder, "ignoredSourcePathPatterns", config == null ? null : config.IgnoredSourcePathPatterns, true, 4);
            AppendJsonProperty(builder, "filterHash", config == null ? null : config.FilterHash, false, 4);
            builder.Append("  }");
        }

        private static void AppendAssemblyInputJson(StringBuilder builder, AssemblyInput input)
        {
            builder.Append("    {\n");
            AppendJsonProperty(builder, "assemblyName", input.AssemblyName, true, 6);
            AppendJsonProperty(builder, "assemblyId", input.AssemblyId, true, 6);
            AppendJsonProperty(builder, "snapshotFile", input.SnapshotFile, true, 6);
            AppendJsonProperty(builder, "nameIndexFile", input.NameIndexFile, true, 6);
            AppendJsonProperty(builder, "tokenIndexFile", input.TokenIndexFile, true, 6);
            AppendJsonProperty(builder, "outputPath", input.OutputPath, true, 6);
            AppendJsonProperty(builder, "asmdefPath", input.AsmdefPath, true, 6);
            AppendJsonProperty(builder, "languageVersion", input.LanguageVersion, true, 6);
            AppendJsonProperty(builder, "allowUnsafe", input.AllowUnsafe, true, 6);
            AppendJsonArray(builder, "sourceFiles", input.SourceFiles, true, 6);
            AppendJsonArray(builder, "referenceFiles", input.ReferenceFiles, true, 6);
            AppendJsonArray(builder, "defines", input.Defines, true, 6);
            AppendJsonArray(builder, "projectReferenceAssemblyNames", input.ProjectReferenceAssemblyNames, true, 6);
            AppendJsonArray(builder, "compilerOptions", input.CompilerOptions, false, 6);
            builder.Append("    }");
        }

        private static void AppendJsonArray(StringBuilder builder, string name, string[] values, bool comma, int indent)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": [");
            for (var i = 0; values != null && i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("\"").Append(EscapeJson(values[i])).Append("\"");
            }

            builder.Append("]");
            builder.Append(comma ? ",\n" : "\n");
        }

        private static bool ReadJsonBool(string json, string name, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
            {
                return defaultValue;
            }

            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(?<value>true|false)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return defaultValue;
            }

            return string.Equals(match.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadJsonString(string json, string name)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
            return match.Success ? UnescapeJsonString(match.Groups["value"].Value) : null;
        }

        private static string UnescapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(c);
                    continue;
                }

                i++;
                switch (value[i])
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(value[i]);
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    default:
                        builder.Append(value[i]);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string QuoteCliArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            var builder = new StringBuilder();
            builder.Append('"');
            var backslashes = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(c);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static SnapshotRequest CreateSnapshotRequest(bool manual, string reason)
        {
            var projectRoot = GetProjectRoot();
            var unityVersion = Application.unityVersion;
            var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            return new SnapshotRequest
            {
                ProjectRoot = projectRoot,
                SnapshotDirectory = GetSnapshotDirectory(),
                UnityVersion = unityVersion,
                BuildTarget = buildTarget,
                FilterConfig = CreateFilterConfig(AIBridgeProjectSettings.Instance.CodeIndex),
                Manual = manual,
                Reason = reason,
                WorkerCount = GetSnapshotWorkerCount(manual),
                OwnerPid = Process.GetCurrentProcess().Id,
                OwnerStartTicks = GetCurrentProcessStartTicks(),
                Assemblies = CaptureAssemblyInputs(unityVersion)
            };
        }

        private static List<AssemblyInput> CaptureAssemblyInputs(string unityVersion)
        {
            var unityAssemblies = CompilationPipeline.GetAssemblies();
            var result = new List<AssemblyInput>();
            for (var i = 0; i < unityAssemblies.Length; i++)
            {
                var assembly = unityAssemblies[i];
                var name = ReadString(assembly, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var assemblyId = SanitizeAssemblyId(name);
                var input = new AssemblyInput
                {
                    AssemblyName = name,
                    AssemblyId = assemblyId,
                    SnapshotFile = AssembliesDirectoryName + "/" + assemblyId + AssemblySnapshotExtension,
                    NameIndexFile = IndexDirectoryName + "/" + NamesDirectoryName + "/" + assemblyId + IndexExtension,
                    TokenIndexFile = IndexDirectoryName + "/" + TokensDirectoryName + "/" + assemblyId + IndexExtension,
                    OutputPath = ReadString(assembly, "outputPath"),
                    AsmdefPath = ReadFirstString(assembly, new[] { "definitionFilePath", "asmdefPath" }),
                    LanguageVersion = DetermineLanguageVersion(assembly, unityVersion),
                    AllowUnsafe = DetermineAllowUnsafe(assembly),
                    SourceFiles = ReadStringArray(assembly, "sourceFiles"),
                    ReferenceFiles = ReadStringArray(assembly, "compiledAssemblyReferences")
                };

                input.Defines.AddRange(Sort(ReadStringArray(assembly, "defines")));
                input.ProjectReferenceAssemblyNames.AddRange(Sort(ReadAssemblyReferenceNames(assembly)));
                input.CompilerOptions.AddRange(ReadCompilerOptions(assembly));
                result.Add(input);
            }

            return result;
        }
#endif

        public static bool GenerateSnapshot(SnapshotRequest request, out string message)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!IsOwnerProcessAlive(request))
                {
                    message = "snapshot worker owner process is not alive";
                    return false;
                }

                var snapshotDirectory = request.SnapshotDirectory;
                var assembliesDirectory = Path.Combine(snapshotDirectory, AssembliesDirectoryName);
                var nameIndexDirectory = Path.Combine(snapshotDirectory, IndexDirectoryName, NamesDirectoryName);
                var tokenIndexDirectory = Path.Combine(snapshotDirectory, IndexDirectoryName, TokensDirectoryName);
                Directory.CreateDirectory(assembliesDirectory);
                Directory.CreateDirectory(nameIndexDirectory);
                Directory.CreateDirectory(tokenIndexDirectory);

                var previousState = ReadPreviousSnapshotState(snapshotDirectory);
                var fileHashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var collection = CollectAssemblyRecords(request, fileHashCache, previousState.FileStates);
                var records = collection.IncludedRecords;
                var changedRecords = new List<AssemblyRecord>();
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    string previousHash;
                    var unchanged = previousState.AssemblyHashes.TryGetValue(record.AssemblyId, out previousHash)
                                    && string.Equals(previousHash, record.AssemblyHash, StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(Path.Combine(snapshotDirectory, record.SnapshotFile))
                                    && File.Exists(Path.Combine(snapshotDirectory, record.NameIndexFile))
                                    && File.Exists(Path.Combine(snapshotDirectory, record.TokenIndexFile));
                    if (!unchanged)
                    {
                        changedRecords.Add(record);
                    }
                }

                var rewritten = 0;
                Parallel.ForEach(
                    changedRecords,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, request.WorkerCount) },
                    record =>
                    {
                        ThrowIfOwnerProcessExited(request);

                        RunWithSnapshotThreadPriority(request.Manual, () =>
                        {
                            WriteAssemblySnapshot(Path.Combine(snapshotDirectory, record.SnapshotFile), record);
                            WriteTokenIndexes(request.ProjectRoot, snapshotDirectory, record);
                            Interlocked.Increment(ref rewritten);
                        });
                    });

                var nowTicks = DateTime.UtcNow.Ticks;
                var manifest = new SnapshotManifest
                {
                    SchemaVersion = SchemaVersion,
                    ProjectRootHash = ComputeHashText(request.ProjectRoot.Replace('\\', '/').ToLowerInvariant()),
                    UnityVersion = request.UnityVersion,
                    BuildTarget = request.BuildTarget,
                    CreatedAtTicks = nowTicks,
                    GenerationId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    IncludePackageCacheSourceAssemblies = request.FilterConfig.IncludePackageCacheSourceAssemblies,
                    IgnoredAssemblyPatterns = request.FilterConfig.IgnoredAssemblyPatterns,
                    IgnoredSourcePathPatterns = request.FilterConfig.IgnoredSourcePathPatterns,
                    FilterHash = request.FilterConfig.FilterHash,
                    ExcludedAssemblyCount = collection.ExcludedRecords.Count,
                    ExcludedSourceFileCount = CountSources(collection.ExcludedRecords),
                    Assemblies = records,
                    ExcludedAssemblies = collection.ExcludedRecords
                };
                manifest.SnapshotContentHash = ComputeSnapshotContentHash(manifest);

                var contentUnchanged = !string.IsNullOrWhiteSpace(previousState.SnapshotContentHash)
                                       && string.Equals(previousState.SnapshotContentHash, manifest.SnapshotContentHash, StringComparison.OrdinalIgnoreCase);
                if (contentUnchanged)
                {
                    // 内容未变化时保留旧元数据，避免 generationId/createdAt 造成无意义 stale。
                    if (previousState.CreatedAtTicks > 0)
                    {
                        manifest.CreatedAtTicks = previousState.CreatedAtTicks;
                    }

                    if (!string.IsNullOrWhiteSpace(previousState.GenerationId))
                    {
                        manifest.GenerationId = previousState.GenerationId;
                    }
                }

                var manifestBinPath = Path.Combine(snapshotDirectory, ManifestBinFileName);
                var manifestJsonPath = Path.Combine(snapshotDirectory, ManifestJsonFileName);
                var manifestWritten = false;
                var manifestJsonWritten = false;
                if (!contentUnchanged)
                {
                    WriteManifestBinary(manifestBinPath, manifest);
                    WriteManifestJson(manifestJsonPath, manifest);
                    manifestWritten = true;
                    manifestJsonWritten = true;
                }
                else if (!ManifestJsonHasSnapshotContentHash(manifestJsonPath, manifest.SnapshotContentHash))
                {
                    WriteManifestJson(manifestJsonPath, manifest);
                    manifestJsonWritten = true;
                }

                stopwatch.Stop();
                message = "snapshot generated: assemblies=" + records.Count
                          + ", excludedAssemblies=" + collection.ExcludedRecords.Count
                          + ", sources=" + CountSources(records)
                          + ", excludedSources=" + CountSources(collection.ExcludedRecords)
                          + ", rewritten=" + rewritten
                          + ", workers=" + Math.Max(1, request.WorkerCount)
                          + ", mode=" + (request.Manual ? "manual" : "background")
                          + ", reason=" + (request.Reason ?? "unknown")
                          + ", manifestWritten=" + manifestWritten
                          + ", manifestJsonWritten=" + manifestJsonWritten
                          + ", elapsedMs=" + stopwatch.ElapsedMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                message = GetExceptionMessage(ex);
                return false;
            }
        }

        private static long GetCurrentProcessStartTicks()
        {
            try
            {
                return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0L;
            }
        }

        private static bool IsOwnerProcessAlive(SnapshotRequest request)
        {
            if (request == null || request.OwnerPid <= 0)
            {
                return true;
            }

            try
            {
                using (var process = Process.GetProcessById(request.OwnerPid))
                {
                    if (process.HasExited)
                    {
                        return false;
                    }

                    if (request.OwnerStartTicks <= 0L)
                    {
                        return true;
                    }

                    var startTicks = process.StartTime.ToUniversalTime().Ticks;
                    return Math.Abs(startTicks - request.OwnerStartTicks) <= TimeSpan.FromSeconds(2).Ticks;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ThrowIfOwnerProcessExited(SnapshotRequest request)
        {
            if (!IsOwnerProcessAlive(request))
            {
                throw new OperationCanceledException("snapshot worker owner process exited");
            }
        }

        private static SnapshotCollection CollectAssemblyRecords(
            SnapshotRequest request,
            Dictionary<string, string> fileHashCache,
            Dictionary<string, FileState> previousFileStates)
        {
            var projectRoot = request.ProjectRoot;
            var filterConfig = request.FilterConfig;
            var allRecords = new List<AssemblyRecord>();
            var byName = new Dictionary<string, AssemblyRecord>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < request.Assemblies.Count; i++)
            {
                ThrowIfOwnerProcessExited(request);

                var input = request.Assemblies[i];
                var record = CreateAssemblyRecord(request, projectRoot, input);
                allRecords.Add(record);
                byName[record.AssemblyName] = record;
            }

            var collection = new SnapshotCollection();
            var excludedAssemblyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < allRecords.Count; i++)
            {
                ThrowIfOwnerProcessExited(request);

                var record = allRecords[i];
                string excludeReason;
                if (TryGetExcludeReason(record, filterConfig, out excludeReason))
                {
                    record.ExcludeReason = excludeReason;
                    excludedAssemblyIds.Add(record.AssemblyId);
                    collection.ExcludedRecords.Add(record);
                    continue;
                }

                var input = request.Assemblies[i];
                record.Sources.Clear();
                record.References.Clear();
                AddFileStates(request, projectRoot, input.SourceFiles, record.Sources, fileHashCache, includeHash: true, previousFileStates: previousFileStates);
                AddFileStates(request, projectRoot, input.ReferenceFiles, record.References, fileHashCache, includeHash: true, previousFileStates: previousFileStates);
                collection.IncludedRecords.Add(record);
            }

            for (var i = 0; i < collection.IncludedRecords.Count; i++)
            {
                ThrowIfOwnerProcessExited(request);

                var record = collection.IncludedRecords[i];
                for (var j = 0; j < record.ProjectReferenceAssemblyNames.Count; j++)
                {
                    AssemblyRecord dependency;
                    if (!byName.TryGetValue(record.ProjectReferenceAssemblyNames[j], out dependency))
                    {
                        continue;
                    }

                    if (excludedAssemblyIds.Contains(dependency.AssemblyId))
                    {
                        // 被过滤的源码程序集仍以编译输出 DLL 的形式参与语义解析，避免工程源码缺少类型定义。
                        AddFileStates(request, projectRoot, new[] { dependency.OutputPath }, record.References, fileHashCache, includeHash: true, previousFileStates: previousFileStates);
                        continue;
                    }

                    record.DependencyAssemblyIds.Add(dependency.AssemblyId);
                    dependency.ReverseDependencyAssemblyIds.Add(record.AssemblyId);
                }
            }

            for (var i = 0; i < allRecords.Count; i++)
            {
                var record = allRecords[i];
                if (!string.IsNullOrEmpty(record.ExcludeReason))
                {
                    continue;
                }

                record.DependencyAssemblyIds = Sort(record.DependencyAssemblyIds);
                record.ReverseDependencyAssemblyIds = Sort(record.ReverseDependencyAssemblyIds);
                record.DefinesHash = ComputeHash(record.Defines);
                record.SourcesHash = ComputeHash(record.Sources);
                record.ReferencesHash = ComputeHash(record.References);
                record.CompilerOptionsHash = ComputeHash(record.CompilerOptions);
                record.AssemblyHash = ComputeAssemblyHash(record, projectRoot, request.UnityVersion, request.BuildTarget, fileHashCache);
                record.LastWriteTimeTicks = ComputeLastWriteTime(record);
            }

            collection.IncludedRecords.Sort((left, right) => string.Compare(left.AssemblyName, right.AssemblyName, StringComparison.OrdinalIgnoreCase));
            collection.ExcludedRecords.Sort((left, right) => string.Compare(left.AssemblyName, right.AssemblyName, StringComparison.OrdinalIgnoreCase));
            return collection;
        }

        private static AssemblyRecord CreateAssemblyRecord(SnapshotRequest request, string projectRoot, AssemblyInput input)
        {
            var record = new AssemblyRecord
            {
                AssemblyName = input.AssemblyName,
                AssemblyId = input.AssemblyId,
                SnapshotFile = input.SnapshotFile,
                NameIndexFile = input.NameIndexFile,
                TokenIndexFile = input.TokenIndexFile,
                OutputPath = NormalizePath(projectRoot, input.OutputPath),
                AsmdefPath = NormalizePath(projectRoot, input.AsmdefPath),
                LanguageVersion = input.LanguageVersion,
                AllowUnsafe = input.AllowUnsafe
            };

            record.Defines.AddRange(input.Defines);
            record.ProjectReferenceAssemblyNames.AddRange(input.ProjectReferenceAssemblyNames);
            record.CompilerOptions.AddRange(input.CompilerOptions);
            AddFileStates(request, projectRoot, input.SourceFiles, record.Sources, null, includeHash: false);
            return record;
        }

#if UNITY_EDITOR
        private static SnapshotFilterConfig CreateFilterConfig(AIBridgeProjectSettings.CodeIndexSettingsData settings)
        {
            var config = new SnapshotFilterConfig
            {
                IncludePackageCacheSourceAssemblies = settings == null
                    ? AIBridgeProjectSettings.DefaultCodeIndexIncludePackageCacheSourceAssemblies
                    : settings.IncludePackageCacheSourceAssemblies
            };
            config.IgnoredAssemblyPatterns.AddRange(SplitFilterPatterns(settings == null ? null : settings.IgnoredAssemblyPatterns));
            config.IgnoredSourcePathPatterns.AddRange(SplitFilterPatterns(settings == null ? null : settings.IgnoredSourcePathPatterns));

            var parts = new List<string>();
            parts.Add(config.IncludePackageCacheSourceAssemblies ? "include-package-cache" : "exclude-package-cache");
            parts.AddRange(config.IgnoredAssemblyPatterns);
            parts.AddRange(config.IgnoredSourcePathPatterns);
            config.FilterHash = ComputeHash(parts);
            return config;
        }
#endif

        private static List<string> SplitFilterPatterns(string value)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = value.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var pattern = NormalizePatternText(parts[i]);
                if (!string.IsNullOrWhiteSpace(pattern) && seen.Add(pattern))
                {
                    result.Add(pattern);
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool TryGetExcludeReason(AssemblyRecord record, SnapshotFilterConfig filterConfig, out string reason)
        {
            reason = null;
            if (record == null || filterConfig == null)
            {
                return false;
            }

            string matchedPattern;
            if (TryMatchPattern(record.AssemblyName, filterConfig.IgnoredAssemblyPatterns, out matchedPattern)
                || TryMatchPattern(record.AssemblyId, filterConfig.IgnoredAssemblyPatterns, out matchedPattern))
            {
                reason = "ignoredAssemblyPattern:" + matchedPattern;
                return true;
            }

            if (!filterConfig.IncludePackageCacheSourceAssemblies && IsPackageCacheAssembly(record))
            {
                reason = "packageCache";
                return true;
            }

            if (TryMatchPattern(record.AsmdefPath, filterConfig.IgnoredSourcePathPatterns, out matchedPattern))
            {
                reason = "ignoredSourcePathPattern:" + matchedPattern;
                return true;
            }

            for (var i = 0; i < record.Sources.Count; i++)
            {
                if (TryMatchPattern(record.Sources[i].Path, filterConfig.IgnoredSourcePathPatterns, out matchedPattern))
                {
                    reason = "ignoredSourcePathPattern:" + matchedPattern;
                    return true;
                }
            }

            return false;
        }

        private static bool IsPackageCacheAssembly(AssemblyRecord record)
        {
            if (record == null)
            {
                return false;
            }

            if (IsPackageCachePath(record.AsmdefPath))
            {
                return true;
            }

            for (var i = 0; i < record.Sources.Count; i++)
            {
                if (IsPackageCachePath(record.Sources[i].Path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPackageCachePath(string path)
        {
            var normalized = NormalizePatternText(path);
            return normalized.StartsWith("Library/PackageCache/", StringComparison.OrdinalIgnoreCase)
                   || normalized.IndexOf("/Library/PackageCache/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryMatchPattern(string value, List<string> patterns, out string matchedPattern)
        {
            matchedPattern = null;
            if (string.IsNullOrWhiteSpace(value) || patterns == null)
            {
                return false;
            }

            for (var i = 0; i < patterns.Count; i++)
            {
                if (MatchesPattern(value, patterns[i]))
                {
                    matchedPattern = patterns[i];
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesPattern(string value, string pattern)
        {
            var normalizedValue = NormalizePatternText(value);
            var normalizedPattern = NormalizePatternText(pattern);
            if (string.IsNullOrEmpty(normalizedValue) || string.IsNullOrEmpty(normalizedPattern))
            {
                return false;
            }

            if (normalizedPattern.IndexOf('*') >= 0 || normalizedPattern.IndexOf('?') >= 0)
            {
                var regex = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(normalizedValue, regex, RegexOptions.IgnoreCase);
            }

            return normalizedValue.IndexOf(normalizedPattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizePatternText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace('\\', '/');
        }

        private static void AddFileStates(
            SnapshotRequest request,
            string projectRoot,
            string[] paths,
            List<FileState> target,
            Dictionary<string, string> fileHashCache,
            bool includeHash,
            Dictionary<string, FileState> previousFileStates = null)
        {
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; target != null && i < target.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(target[i].Path))
                {
                    unique.Add(target[i].Path);
                }
            }

            for (var i = 0; i < paths.Length; i++)
            {
                if ((i & 15) == 0)
                {
                    ThrowIfOwnerProcessExited(request);
                }

                var path = paths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fullPath = ResolveSnapshotPath(projectRoot, path);
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                var normalized = NormalizePath(projectRoot, fullPath);
                if (!unique.Add(normalized))
                {
                    continue;
                }

                var info = new FileInfo(fullPath);
                target.Add(new FileState
                {
                    Path = normalized,
                    Length = info.Length,
                    LastWriteTimeTicks = info.LastWriteTimeUtc.Ticks,
                    Hash = includeHash
                        ? ResolveFileHash(normalized, fullPath, info, fileHashCache, previousFileStates)
                        : string.Empty
                });
            }

            target.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveFileHash(
            string normalizedPath,
            string fullPath,
            FileInfo info,
            Dictionary<string, string> fileHashCache,
            Dictionary<string, FileState> previousFileStates)
        {
            FileState previous;
            if (previousFileStates != null
                && previousFileStates.TryGetValue(normalizedPath, out previous)
                && previous.Length == info.Length
                && previous.LastWriteTimeTicks == info.LastWriteTimeUtc.Ticks
                && !string.IsNullOrEmpty(previous.Hash))
            {
                // 增量刷新先复用上一份 snapshot 中未变化文件的 hash，避免每次刷新全量读盘。
                if (fileHashCache != null)
                {
                    fileHashCache[Path.GetFullPath(fullPath)] = previous.Hash;
                }

                return previous.Hash;
            }

            return ComputeFileHash(fullPath, fileHashCache);
        }

        private static void WriteManifestBinary(string path, SnapshotManifest manifest)
        {
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteHeader(writer, ManifestFormatKind, manifest.CreatedAtTicks);
                writer.Write(manifest.SchemaVersion);
                WriteString(writer, manifest.ProjectRootHash);
                WriteString(writer, manifest.UnityVersion);
                WriteString(writer, manifest.BuildTarget);
                WriteString(writer, manifest.GenerationId);
                writer.Write(manifest.IncludePackageCacheSourceAssemblies);
                WriteStringList(writer, manifest.IgnoredAssemblyPatterns);
                WriteStringList(writer, manifest.IgnoredSourcePathPatterns);
                WriteString(writer, manifest.FilterHash);
                writer.Write(manifest.ExcludedAssemblyCount);
                writer.Write(manifest.ExcludedSourceFileCount);
                writer.Write(manifest.Assemblies.Count);
                for (var i = 0; i < manifest.Assemblies.Count; i++)
                {
                    WriteAssemblyRecord(writer, manifest.Assemblies[i]);
                }
            }

            AtomicReplace(tempPath, path);
        }

        private static void WriteAssemblySnapshot(string path, AssemblyRecord record)
        {
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteHeader(writer, AssemblyFormatKind, DateTime.UtcNow.Ticks);
                WriteAssemblyRecord(writer, record);
                WriteStringList(writer, record.Defines);
                WriteFileStates(writer, record.Sources);
                WriteFileStates(writer, record.References);
                WriteStringList(writer, record.ProjectReferenceAssemblyNames);
                WriteStringList(writer, record.CompilerOptions);
            }

            AtomicReplace(tempPath, path);
        }

        private static void WriteAssemblyRecord(BinaryWriter writer, AssemblyRecord record)
        {
            WriteString(writer, record.AssemblyName);
            WriteString(writer, record.AssemblyId);
            WriteString(writer, record.SnapshotFile);
            WriteString(writer, record.NameIndexFile);
            WriteString(writer, record.TokenIndexFile);
            WriteString(writer, record.OutputPath);
            WriteString(writer, record.AsmdefPath);
            WriteString(writer, record.LanguageVersion);
            writer.Write(record.AllowUnsafe);
            writer.Write(record.Sources.Count);
            writer.Write(record.References.Count);
            WriteString(writer, record.DefinesHash);
            WriteString(writer, record.SourcesHash);
            WriteString(writer, record.ReferencesHash);
            WriteString(writer, record.CompilerOptionsHash);
            WriteString(writer, record.AssemblyHash);
            writer.Write(record.LastWriteTimeTicks);
            WriteStringList(writer, record.DependencyAssemblyIds);
            WriteStringList(writer, record.ReverseDependencyAssemblyIds);
        }

        private static int GetSnapshotWorkerCount(bool manual)
        {
            var availableWorkers = Math.Max(1, Environment.ProcessorCount - 1);
            var maxWorkers = manual ? ManualMaxWorkerCount : StartupMaxWorkerCount;
            return Math.Max(1, Math.Min(availableWorkers, maxWorkers));
        }

        private static void RunWithSnapshotThreadPriority(bool manual, Action action)
        {
            if (action == null)
            {
                return;
            }

            if (manual)
            {
                action();
                return;
            }

            var thread = Thread.CurrentThread;
            var originalPriority = thread.Priority;
            try
            {
                // 启动/自动刷新降低后台线程优先级，减少与 Editor 主流程争抢 CPU。
                if (originalPriority > System.Threading.ThreadPriority.BelowNormal)
                {
                    thread.Priority = System.Threading.ThreadPriority.BelowNormal;
                }
            }
            catch
            {
            }

            try
            {
                action();
            }
            finally
            {
                try
                {
                    thread.Priority = originalPriority;
                }
                catch
                {
                }
            }
        }

        private static void WriteTokenIndexes(string projectRoot, string snapshotDirectory, AssemblyRecord record)
        {
            List<string> nameEntries;
            List<string> tokenEntries;
            BuildTokenEntries(projectRoot, record, out nameEntries, out tokenEntries);
            WriteTextIndex(Path.Combine(snapshotDirectory, record.NameIndexFile), record, namesOnly: true, entries: nameEntries);
            WriteTextIndex(Path.Combine(snapshotDirectory, record.TokenIndexFile), record, namesOnly: false, entries: tokenEntries);
        }

        private static void WriteTextIndex(string path, AssemblyRecord record, bool namesOnly, List<string> entries)
        {
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteHeader(writer, TextIndexFormatKind, DateTime.UtcNow.Ticks);
                WriteString(writer, record.AssemblyId);
                WriteString(writer, record.AssemblyHash);
                writer.Write(namesOnly);
                writer.Write(entries.Count);
                for (var i = 0; i < entries.Count; i++)
                {
                    WriteString(writer, entries[i]);
                }
            }

            AtomicReplace(tempPath, path);
        }

        private static void BuildTokenEntries(
            string projectRoot,
            AssemblyRecord record,
            out List<string> nameEntries,
            out List<string> tokenEntries)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < record.Sources.Count; i++)
            {
                var fullPath = ResolveSnapshotPath(projectRoot, record.Sources[i].Path);
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                var lineNumber = 0;
                var fileTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines(fullPath))
                {
                    lineNumber++;
                    var matches = TokenRegex.Matches(line);
                    for (var j = 0; j < matches.Count; j++)
                    {
                        var value = matches[j].Value;
                        fileTokens.Add(value);
                        if (LooksLikeDeclarationName(line, value))
                        {
                            var entry = value + "|" + record.Sources[i].Path + "|" + lineNumber.ToString(CultureInfo.InvariantCulture);
                            names.Add(entry);
                        }
                    }
                }

                foreach (var value in fileTokens)
                {
                    // references 查询只需要 token -> file 候选集合，行号写 0 保持旧解析格式兼容。
                    tokens.Add(value + "|" + record.Sources[i].Path + "|0");
                }
            }

            nameEntries = new List<string>(names);
            nameEntries.Sort(StringComparer.OrdinalIgnoreCase);
            tokenEntries = new List<string>(tokens);
            tokenEntries.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static bool LooksLikeDeclarationName(string line, string value)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(value))
            {
                return false;
            }

            var index = line.IndexOf(value, StringComparison.Ordinal);
            if (index <= 0)
            {
                return false;
            }

            var prefix = line.Substring(0, index);
            return prefix.IndexOf("class ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("struct ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("interface ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("enum ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("delegate ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("void ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("public ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("private ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("protected ", StringComparison.Ordinal) >= 0
                   || prefix.IndexOf("internal ", StringComparison.Ordinal) >= 0;
        }

        private static void WriteManifestJson(string path, SnapshotManifest manifest)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            AppendJsonProperty(builder, "workspaceMode", "unity-snapshot", true);
            AppendJsonProperty(builder, "schemaVersion", manifest.SchemaVersion, true);
            AppendJsonProperty(builder, "projectRootHash", manifest.ProjectRootHash, true);
            AppendJsonProperty(builder, "unityVersion", manifest.UnityVersion, true);
            AppendJsonProperty(builder, "buildTarget", manifest.BuildTarget, true);
            AppendJsonProperty(builder, "createdAt", new DateTime(manifest.CreatedAtTicks, DateTimeKind.Utc).ToString("o"), true);
            AppendJsonProperty(builder, "generationId", manifest.GenerationId, true);
            AppendJsonProperty(builder, "snapshotContentHash", manifest.SnapshotContentHash, true);
            AppendJsonProperty(builder, "assemblyCount", manifest.Assemblies.Count, true);
            AppendJsonProperty(builder, "sourceFileCount", CountSources(manifest.Assemblies), true);
            AppendJsonProperty(builder, "excludedAssemblyCount", manifest.ExcludedAssemblyCount, true);
            AppendJsonProperty(builder, "excludedSourceFileCount", manifest.ExcludedSourceFileCount, true);
            AppendJsonProperty(builder, "includePackageCacheSourceAssemblies", manifest.IncludePackageCacheSourceAssemblies, true);
            AppendJsonArray(builder, "ignoredAssemblyPatterns", manifest.IgnoredAssemblyPatterns, true, 2);
            AppendJsonArray(builder, "ignoredSourcePathPatterns", manifest.IgnoredSourcePathPatterns, true, 2);
            AppendJsonProperty(builder, "filterHash", manifest.FilterHash, true);
            builder.Append("  \"assemblyRecords\": [\n");
            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                var record = manifest.Assemblies[i];
                builder.Append("    {\n");
                AppendJsonProperty(builder, "assemblyName", record.AssemblyName, true, 6);
                AppendJsonProperty(builder, "assemblyId", record.AssemblyId, true, 6);
                AppendJsonProperty(builder, "snapshotFile", record.SnapshotFile, true, 6);
                AppendJsonProperty(builder, "nameIndexFile", record.NameIndexFile, true, 6);
                AppendJsonProperty(builder, "tokenIndexFile", record.TokenIndexFile, true, 6);
                AppendJsonProperty(builder, "sourceFileCount", record.Sources.Count, true, 6);
                AppendJsonProperty(builder, "referenceCount", record.References.Count, true, 6);
                AppendJsonProperty(builder, "definesHash", record.DefinesHash, true, 6);
                AppendJsonProperty(builder, "sourcesHash", record.SourcesHash, true, 6);
                AppendJsonProperty(builder, "referencesHash", record.ReferencesHash, true, 6);
                AppendJsonProperty(builder, "compilerOptionsHash", record.CompilerOptionsHash, true, 6);
                AppendJsonProperty(builder, "assemblyHash", record.AssemblyHash, true, 6);
                AppendJsonProperty(builder, "lastWriteTime", record.LastWriteTimeTicks, true, 6);
                AppendJsonArray(builder, "dependencyAssemblyIds", record.DependencyAssemblyIds, true, 6);
                AppendJsonArray(builder, "reverseDependencyAssemblyIds", record.ReverseDependencyAssemblyIds, false, 6);
                builder.Append("    }");
                if (i + 1 < manifest.Assemblies.Count)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ],\n");
            builder.Append("  \"excludedAssemblyRecords\": [\n");
            for (var i = 0; i < manifest.ExcludedAssemblies.Count; i++)
            {
                var record = manifest.ExcludedAssemblies[i];
                builder.Append("    {\n");
                AppendJsonProperty(builder, "assemblyName", record.AssemblyName, true, 6);
                AppendJsonProperty(builder, "assemblyId", record.AssemblyId, true, 6);
                AppendJsonProperty(builder, "excludeReason", record.ExcludeReason, true, 6);
                AppendJsonProperty(builder, "sourceFileCount", record.Sources.Count, true, 6);
                AppendJsonProperty(builder, "outputPath", record.OutputPath, false, 6);
                builder.Append("    }");
                if (i + 1 < manifest.ExcludedAssemblies.Count)
                {
                    builder.Append(",");
                }

                builder.Append("\n");
            }

            builder.Append("  ]\n");
            builder.Append("}\n");
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, builder.ToString(), Encoding.UTF8);
            AtomicReplace(tempPath, path);
        }

        private static bool ManifestJsonHasSnapshotContentHash(string path, string snapshotContentHash)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(snapshotContentHash) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var match = Regex.Match(text, "\"snapshotContentHash\"\\s*:\\s*\"(?<value>[0-9a-fA-F]+)\"");
                return match.Success && string.Equals(match.Groups["value"].Value, snapshotContentHash, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static PreviousSnapshotState ReadPreviousSnapshotState(string snapshotDirectory)
        {
            var result = new PreviousSnapshotState();
            var manifestPath = Path.Combine(snapshotDirectory, ManifestBinFileName);
            if (!File.Exists(manifestPath))
            {
                return result;
            }

            try
            {
                using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var createdAtTicks = ReadSnapshotHeader(reader, ManifestFormatKind);
                    var manifest = new SnapshotManifest
                    {
                        SchemaVersion = reader.ReadInt32(),
                        ProjectRootHash = ReadBinaryString(reader),
                        UnityVersion = ReadBinaryString(reader),
                        BuildTarget = ReadBinaryString(reader),
                        GenerationId = ReadBinaryString(reader),
                        IncludePackageCacheSourceAssemblies = reader.ReadBoolean(),
                        IgnoredAssemblyPatterns = ReadBinaryStringList(reader),
                        IgnoredSourcePathPatterns = ReadBinaryStringList(reader),
                        FilterHash = ReadBinaryString(reader),
                        ExcludedAssemblyCount = reader.ReadInt32(),
                        ExcludedSourceFileCount = reader.ReadInt32()
                    };
                    if (manifest.SchemaVersion != SchemaVersion)
                    {
                        return result;
                    }

                    result.CreatedAtTicks = createdAtTicks;
                    result.GenerationId = manifest.GenerationId;
                    var count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var record = ReadBinaryAssemblyRecord(reader);
                        manifest.Assemblies.Add(record);
                        if (!string.IsNullOrWhiteSpace(record.AssemblyId) && !string.IsNullOrWhiteSpace(record.AssemblyHash))
                        {
                            result.AssemblyHashes[record.AssemblyId] = record.AssemblyHash;
                        }

                        ReadPreviousAssemblyFile(snapshotDirectory, record, result);
                    }

                    result.SnapshotContentHash = ComputeSnapshotContentHash(manifest);
                }
            }
            catch
            {
            }

            return result;
        }

        private static void ReadPreviousAssemblyFile(string snapshotDirectory, AssemblyRecord record, PreviousSnapshotState state)
        {
            if (record == null || state == null || string.IsNullOrEmpty(record.SnapshotFile))
            {
                return;
            }

            var path = Path.Combine(snapshotDirectory, record.SnapshotFile);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    ReadSnapshotHeader(reader, AssemblyFormatKind);
                    ReadBinaryAssemblyRecord(reader);
                    ReadBinaryStringList(reader);
                    AddPreviousFileStates(state, ReadBinaryFileStates(reader));
                    AddPreviousFileStates(state, ReadBinaryFileStates(reader));
                }
            }
            catch
            {
            }
        }

        private static void AddPreviousFileStates(PreviousSnapshotState state, List<FileState> files)
        {
            if (state == null || files == null)
            {
                return;
            }

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file != null && !string.IsNullOrEmpty(file.Path) && !state.FileStates.ContainsKey(file.Path))
                {
                    state.FileStates[file.Path] = file;
                }
            }
        }

        private static long ReadSnapshotHeader(BinaryReader reader, int expectedFormatKind)
        {
            var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
            var schema = reader.ReadInt32();
            var formatKind = reader.ReadInt32();
            var createdAtTicks = reader.ReadInt64();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal) || schema != SchemaVersion || formatKind != expectedFormatKind)
            {
                throw new InvalidDataException("Unsupported Code Index snapshot format.");
            }

            return createdAtTicks;
        }

        private static AssemblyRecord ReadBinaryAssemblyRecord(BinaryReader reader)
        {
            var record = new AssemblyRecord
            {
                AssemblyName = ReadBinaryString(reader),
                AssemblyId = ReadBinaryString(reader),
                SnapshotFile = ReadBinaryString(reader),
                NameIndexFile = ReadBinaryString(reader),
                TokenIndexFile = ReadBinaryString(reader),
                OutputPath = ReadBinaryString(reader),
                AsmdefPath = ReadBinaryString(reader),
                LanguageVersion = ReadBinaryString(reader),
                AllowUnsafe = reader.ReadBoolean()
            };
            record.SourceFileCount = reader.ReadInt32();
            record.ReferenceCount = reader.ReadInt32();
            record.DefinesHash = ReadBinaryString(reader);
            record.SourcesHash = ReadBinaryString(reader);
            record.ReferencesHash = ReadBinaryString(reader);
            record.CompilerOptionsHash = ReadBinaryString(reader);
            record.AssemblyHash = ReadBinaryString(reader);
            record.LastWriteTimeTicks = reader.ReadInt64();
            record.DependencyAssemblyIds = ReadBinaryStringList(reader);
            record.ReverseDependencyAssemblyIds = ReadBinaryStringList(reader);
            return record;
        }

        private static List<string> ReadBinaryStringList(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var result = new List<string>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                result.Add(ReadBinaryString(reader));
            }

            return result;
        }

        private static List<FileState> ReadBinaryFileStates(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            var result = new List<FileState>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                result.Add(new FileState
                {
                    Path = ReadBinaryString(reader),
                    Length = reader.ReadInt64(),
                    LastWriteTimeTicks = reader.ReadInt64(),
                    Hash = ReadBinaryString(reader)
                });
            }

            return result;
        }

        private static string ReadBinaryString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

#if UNITY_EDITOR
        private static string[] ReadStringArray(object instance, string name)
        {
            var value = ReadMemberValue(instance, name);
            if (value == null)
            {
                return new string[0];
            }

            var array = value as string[];
            if (array != null)
            {
                return array;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string)
            {
                return new string[0];
            }

            var result = new List<string>();
            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    result.Add(Convert.ToString(item));
                }
            }

            return result.ToArray();
        }

        private static List<string> ReadAssemblyReferenceNames(object assembly)
        {
            var result = new List<string>();
            var references = ReadMemberValue(assembly, "assemblyReferences") as System.Collections.IEnumerable;
            if (references == null)
            {
                return result;
            }

            foreach (var reference in references)
            {
                var name = ReadString(reference, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        private static List<string> ReadCompilerOptions(object assembly)
        {
            var result = new List<string>();
            var compilerOptions = ReadMemberValue(assembly, "compilerOptions");
            if (compilerOptions == null)
            {
                return result;
            }

            var type = compilerOptions.GetType();
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (var i = 0; i < properties.Length; i++)
            {
                if (!properties[i].CanRead)
                {
                    continue;
                }

                object value;
                if (TryGetReflectedValue(properties[i].Name, () => properties[i].GetValue(compilerOptions, null), out value))
                {
                    TryAddCompilerOption(result, properties[i].Name, value);
                }
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            for (var i = 0; i < fields.Length; i++)
            {
                object value;
                if (TryGetReflectedValue(fields[i].Name, () => fields[i].GetValue(compilerOptions), out value))
                {
                    TryAddCompilerOption(result, fields[i].Name, value);
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool TryGetReflectedValue(string name, Func<object> getter, out object value)
        {
            value = null;
            try
            {
                value = getter();
                return true;
            }
            catch
            {
                // Unity 各版本的 CompilationPipeline 反射属性不完全稳定，单个属性失败不能中断整个快照。
                return false;
            }
        }

        private static void TryAddCompilerOption(List<string> result, string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name) || value == null)
            {
                return;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null && !(value is string))
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                    {
                        result.Add(name + "=" + Convert.ToString(item));
                    }
                }

                return;
            }

            result.Add(name + "=" + Convert.ToString(value));
        }

        private static string DetermineLanguageVersion(object assembly, string unityVersion)
        {
            var compilerOptions = ReadMemberValue(assembly, "compilerOptions");
            var value = ReadFirstString(compilerOptions, new[] { "languageVersion", "LanguageVersion" });
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var dot = unityVersion.IndexOf('.');
            var majorText = dot < 0 ? unityVersion : unityVersion.Substring(0, dot);
            int major;
            if (int.TryParse(majorText, out major) && major <= 2019)
            {
                return "7.3";
            }

            return "9.0";
        }

        private static bool DetermineAllowUnsafe(object assembly)
        {
            var flags = ReadMemberValue(assembly, "flags");
            if (flags != null && flags.ToString().IndexOf("Unsafe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var compilerOptions = ReadMemberValue(assembly, "compilerOptions");
            var unsafeValue = ReadMemberValue(compilerOptions, "allowUnsafeCode") ?? ReadMemberValue(compilerOptions, "AllowUnsafeCode");
            return unsafeValue is bool && (bool)unsafeValue;
        }

        private static object ReadMemberValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                try
                {
                    return property.GetValue(instance, null);
                }
                catch
                {
                }
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                }
            }

            return null;
        }

        private static string ReadString(object instance, string name)
        {
            var value = ReadMemberValue(instance, name);
            return value == null ? null : Convert.ToString(value);
        }

        private static string ReadFirstString(object instance, string[] names)
        {
            if (instance == null || names == null)
            {
                return null;
            }

            for (var i = 0; i < names.Length; i++)
            {
                var value = ReadString(instance, names[i]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
#endif

        private static string NormalizePath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            var root = Path.GetFullPath(projectRoot).Replace('\\', '/').TrimEnd('/') + "/";
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length);
            }

            return fullPath;
        }

        private static string ResolveSnapshotPath(string projectRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(projectRoot, path));
        }

#if UNITY_EDITOR
        private static string GetProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }
#endif

        private static string SanitizeAssemblyId(string name)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                builder.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            }

            return builder.Length == 0 ? "Assembly" : builder.ToString();
        }

        private static List<string> Sort(IEnumerable<string> values)
        {
            var result = new List<string>();
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string ComputeAssemblyHash(
            AssemblyRecord record,
            string projectRoot,
            string unityVersion,
            string buildTarget,
            Dictionary<string, string> fileHashCache)
        {
            var parts = new List<string>();
            parts.Add(record.AssemblyName);
            parts.Add(record.OutputPath);
            parts.Add(record.AsmdefPath);
            parts.Add(record.LanguageVersion);
            parts.Add(record.AllowUnsafe ? "unsafe" : "safe");
            parts.Add(unityVersion);
            parts.Add(buildTarget);
            parts.Add(record.DefinesHash);
            parts.Add(record.SourcesHash);
            parts.Add(record.ReferencesHash);
            parts.Add(record.CompilerOptionsHash);
            parts.Add(ComputeHash(record.ProjectReferenceAssemblyNames));
            parts.Add(ComputeHash(record.DependencyAssemblyIds));
            var asmdefFullPath = ResolveSnapshotPath(projectRoot, record.AsmdefPath);
            if (!string.IsNullOrWhiteSpace(asmdefFullPath) && File.Exists(asmdefFullPath))
            {
                parts.Add(ComputeFileHash(asmdefFullPath, fileHashCache));
            }

            return ComputeHash(parts);
        }

        private static string ComputeSnapshotContentHash(SnapshotManifest manifest)
        {
            var parts = new List<string>();
            if (manifest == null)
            {
                return ComputeHash(parts);
            }

            AddContentPart(parts, "schemaVersion", manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "projectRootHash", manifest.ProjectRootHash);
            AddContentPart(parts, "unityVersion", manifest.UnityVersion);
            AddContentPart(parts, "buildTarget", manifest.BuildTarget);
            AddContentPart(parts, "includePackageCacheSourceAssemblies", manifest.IncludePackageCacheSourceAssemblies ? "true" : "false");
            AddStringListContentParts(parts, "ignoredAssemblyPatterns", manifest.IgnoredAssemblyPatterns);
            AddStringListContentParts(parts, "ignoredSourcePathPatterns", manifest.IgnoredSourcePathPatterns);
            AddContentPart(parts, "filterHash", manifest.FilterHash);
            AddContentPart(parts, "excludedAssemblyCount", manifest.ExcludedAssemblyCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "excludedSourceFileCount", manifest.ExcludedSourceFileCount.ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assemblyCount", manifest.Assemblies.Count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < manifest.Assemblies.Count; i++)
            {
                AddAssemblyContentParts(parts, manifest.Assemblies[i], i);
            }

            return ComputeHash(parts);
        }

        private static void AddAssemblyContentParts(List<string> parts, AssemblyRecord record, int index)
        {
            AddContentPart(parts, "assembly[" + index + "].assemblyName", record.AssemblyName);
            AddContentPart(parts, "assembly[" + index + "].assemblyId", record.AssemblyId);
            AddContentPart(parts, "assembly[" + index + "].snapshotFile", record.SnapshotFile);
            AddContentPart(parts, "assembly[" + index + "].nameIndexFile", record.NameIndexFile);
            AddContentPart(parts, "assembly[" + index + "].tokenIndexFile", record.TokenIndexFile);
            AddContentPart(parts, "assembly[" + index + "].outputPath", record.OutputPath);
            AddContentPart(parts, "assembly[" + index + "].asmdefPath", record.AsmdefPath);
            AddContentPart(parts, "assembly[" + index + "].languageVersion", record.LanguageVersion);
            AddContentPart(parts, "assembly[" + index + "].allowUnsafe", record.AllowUnsafe ? "true" : "false");
            AddContentPart(parts, "assembly[" + index + "].sourceFileCount", GetSourceFileCount(record).ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].referenceCount", GetReferenceCount(record).ToString(CultureInfo.InvariantCulture));
            AddContentPart(parts, "assembly[" + index + "].definesHash", record.DefinesHash);
            AddContentPart(parts, "assembly[" + index + "].sourcesHash", record.SourcesHash);
            AddContentPart(parts, "assembly[" + index + "].referencesHash", record.ReferencesHash);
            AddContentPart(parts, "assembly[" + index + "].compilerOptionsHash", record.CompilerOptionsHash);
            AddContentPart(parts, "assembly[" + index + "].assemblyHash", record.AssemblyHash);
            AddContentPart(parts, "assembly[" + index + "].lastWriteTimeTicks", record.LastWriteTimeTicks.ToString(CultureInfo.InvariantCulture));
            AddStringListContentParts(parts, "assembly[" + index + "].dependencyAssemblyIds", record.DependencyAssemblyIds);
            AddStringListContentParts(parts, "assembly[" + index + "].reverseDependencyAssemblyIds", record.ReverseDependencyAssemblyIds);
        }

        private static void AddStringListContentParts(List<string> parts, string name, List<string> values)
        {
            var count = values == null ? 0 : values.Count;
            AddContentPart(parts, name + ".count", count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; values != null && i < values.Count; i++)
            {
                AddContentPart(parts, name + "[" + i + "]", values[i]);
            }
        }

        private static void AddContentPart(List<string> parts, string name, string value)
        {
            parts.Add(name + "=" + (value ?? string.Empty));
        }

        private static int GetSourceFileCount(AssemblyRecord record)
        {
            return record.Sources != null && record.Sources.Count > 0 ? record.Sources.Count : record.SourceFileCount;
        }

        private static int GetReferenceCount(AssemblyRecord record)
        {
            return record.References != null && record.References.Count > 0 ? record.References.Count : record.ReferenceCount;
        }

        private static string ComputeHash(IEnumerable<FileState> files)
        {
            var parts = new List<string>();
            if (files != null)
            {
                foreach (var file in files)
                {
                    parts.Add(file.Path + "|" + file.Length + "|" + file.LastWriteTimeTicks + "|" + file.Hash);
                }
            }

            return ComputeHash(parts);
        }

        private static string ComputeHash(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            if (values != null)
            {
                foreach (var value in values)
                {
                    builder.Append(value ?? string.Empty).Append('\n');
                }
            }

            return ComputeHashText(builder.ToString());
        }

        private static string ComputeHashText(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return ToHex(bytes);
            }
        }

        private static string ComputeFileHash(string path)
        {
            return ComputeFileHash(path, null);
        }

        private static string ComputeFileHash(string path, Dictionary<string, string> fileHashCache)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fileHashCache != null)
                {
                    string cached;
                    if (fileHashCache.TryGetValue(fullPath, out cached))
                    {
                        return cached;
                    }
                }

                using (var sha = SHA256.Create())
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var hash = ToHex(sha.ComputeHash(stream));
                    if (fileHashCache != null)
                    {
                        fileHashCache[fullPath] = hash;
                    }

                    return hash;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static long ComputeLastWriteTime(AssemblyRecord record)
        {
            var value = 0L;
            for (var i = 0; i < record.Sources.Count; i++)
            {
                value = Math.Max(value, record.Sources[i].LastWriteTimeTicks);
            }

            for (var i = 0; i < record.References.Count; i++)
            {
                value = Math.Max(value, record.References[i].LastWriteTimeTicks);
            }

            return value;
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static int CountSources(List<AssemblyRecord> records)
        {
            var count = 0;
            for (var i = 0; i < records.Count; i++)
            {
                count += records[i].Sources.Count;
            }

            return count;
        }

        private static void WriteHeader(BinaryWriter writer, int formatKind, long createdAtTicks)
        {
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(SchemaVersion);
            writer.Write(formatKind);
            writer.Write(createdAtTicks);
        }

        private static void WriteStringList(BinaryWriter writer, List<string> values)
        {
            writer.Write(values == null ? 0 : values.Count);
            if (values == null)
            {
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                WriteString(writer, values[i]);
            }
        }

        private static void WriteFileStates(BinaryWriter writer, List<FileState> values)
        {
            writer.Write(values == null ? 0 : values.Count);
            if (values == null)
            {
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                WriteString(writer, values[i].Path);
                writer.Write(values[i].Length);
                writer.Write(values[i].LastWriteTimeTicks);
                WriteString(writer, values[i].Hash);
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null)
            {
                writer.Write(-1);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static void AtomicReplace(string tempPath, string finalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            try
            {
                if (File.Exists(finalPath))
                {
                    File.Replace(tempPath, finalPath, null);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }
            }
            catch
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(tempPath, finalPath);
            }
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, string value, bool comma, int indent = 2)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": ");
            if (value == null)
            {
                builder.Append("null");
            }
            else
            {
                builder.Append("\"").Append(EscapeJson(value)).Append("\"");
            }

            builder.Append(comma ? ",\n" : "\n");
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, int value, bool comma, int indent = 2)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
            builder.Append(comma ? ",\n" : "\n");
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, bool value, bool comma, int indent = 2)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": ").Append(value ? "true" : "false");
            builder.Append(comma ? ",\n" : "\n");
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, long value, bool comma, int indent = 2)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
            builder.Append(comma ? ",\n" : "\n");
        }

        private static void AppendJsonArray(StringBuilder builder, string name, List<string> values, bool comma, int indent)
        {
            builder.Append(new string(' ', indent));
            builder.Append("\"").Append(EscapeJson(name)).Append("\": [");
            for (var i = 0; values != null && i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("\"").Append(EscapeJson(values[i])).Append("\"");
            }

            builder.Append("]");
            builder.Append(comma ? ",\n" : "\n");
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(value[i]);
                        break;
                }
            }

            return builder.ToString();
        }

        private static string GetExceptionMessage(Exception ex)
        {
            if (ex == null)
            {
                return string.Empty;
            }

            var inner = ex.InnerException;
            return inner == null ? ex.Message : inner.Message + " (" + ex.GetType().Name + ")";
        }

        public sealed class SnapshotResult
        {
            public readonly bool Success;
            public readonly string Message;

            public SnapshotResult(bool success, string message)
            {
                Success = success;
                Message = message;
            }
        }

        public sealed class SnapshotRequest
        {
            public string ProjectRoot;
            public string SnapshotDirectory;
            public string UnityVersion;
            public string BuildTarget;
            public SnapshotFilterConfig FilterConfig;
            public bool Manual;
            public string Reason;
            public int WorkerCount;
            public int OwnerPid;
            public long OwnerStartTicks;
            public List<AssemblyInput> Assemblies = new List<AssemblyInput>();
        }

        public sealed class AssemblyInput
        {
            public string AssemblyName;
            public string AssemblyId;
            public string SnapshotFile;
            public string NameIndexFile;
            public string TokenIndexFile;
            public string OutputPath;
            public string AsmdefPath;
            public string LanguageVersion;
            public bool AllowUnsafe;
            public string[] SourceFiles = new string[0];
            public string[] ReferenceFiles = new string[0];
            public List<string> Defines = new List<string>();
            public List<string> ProjectReferenceAssemblyNames = new List<string>();
            public List<string> CompilerOptions = new List<string>();
        }

        public sealed class SnapshotManifest
        {
            public int SchemaVersion;
            public string ProjectRootHash;
            public string UnityVersion;
            public string BuildTarget;
            public long CreatedAtTicks;
            public string GenerationId;
            public string SnapshotContentHash;
            public bool IncludePackageCacheSourceAssemblies;
            public List<string> IgnoredAssemblyPatterns = new List<string>();
            public List<string> IgnoredSourcePathPatterns = new List<string>();
            public string FilterHash;
            public int ExcludedAssemblyCount;
            public int ExcludedSourceFileCount;
            public List<AssemblyRecord> Assemblies = new List<AssemblyRecord>();
            public List<AssemblyRecord> ExcludedAssemblies = new List<AssemblyRecord>();
        }

        public sealed class SnapshotCollection
        {
            public List<AssemblyRecord> IncludedRecords = new List<AssemblyRecord>();
            public List<AssemblyRecord> ExcludedRecords = new List<AssemblyRecord>();
        }

        public sealed class SnapshotFilterConfig
        {
            public bool IncludePackageCacheSourceAssemblies;
            public List<string> IgnoredAssemblyPatterns = new List<string>();
            public List<string> IgnoredSourcePathPatterns = new List<string>();
            public string FilterHash;
        }

        public sealed class PreviousSnapshotState
        {
            public long CreatedAtTicks;
            public string GenerationId;
            public string SnapshotContentHash;
            public Dictionary<string, string> AssemblyHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, FileState> FileStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class AssemblyRecord
        {
            public string AssemblyName;
            public string AssemblyId;
            public string SnapshotFile;
            public string NameIndexFile;
            public string TokenIndexFile;
            public string OutputPath;
            public string AsmdefPath;
            public string LanguageVersion;
            public bool AllowUnsafe;
            public int SourceFileCount;
            public int ReferenceCount;
            public List<string> Defines = new List<string>();
            public List<FileState> Sources = new List<FileState>();
            public List<FileState> References = new List<FileState>();
            public List<string> ProjectReferenceAssemblyNames = new List<string>();
            public List<string> CompilerOptions = new List<string>();
            public List<string> DependencyAssemblyIds = new List<string>();
            public List<string> ReverseDependencyAssemblyIds = new List<string>();
            public string DefinesHash;
            public string SourcesHash;
            public string ReferencesHash;
            public string CompilerOptionsHash;
            public string AssemblyHash;
            public long LastWriteTimeTicks;
            public string ExcludeReason;
        }

        public sealed class FileState
        {
            public string Path;
            public long Length;
            public long LastWriteTimeTicks;
            public string Hash;
        }
    }
}
