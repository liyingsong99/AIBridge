using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AIBridge.Editor;
using Newtonsoft.Json;

namespace AIBridgeCodeIndex
{
    internal static class Program
    {
        private const string IndexDirectoryName = "code-index";
        private const string TempDirectoryName = "temp";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var options = CodeIndexOptions.Parse(args);
            if (!string.IsNullOrWhiteSpace(options.Worker))
            {
                return RunWorker(options);
            }

            if (string.IsNullOrWhiteSpace(options.ProjectRoot))
            {
                Console.Error.WriteLine("--project-root is required.");
                return 1;
            }

            options.ProjectRoot = Path.GetFullPath(options.ProjectRoot);
            if (!Directory.Exists(options.ProjectRoot))
            {
                Console.Error.WriteLine("Project root does not exist.");
                return 1;
            }

            var server = new CodeIndexServer(options);
            await server.RunAsync();
            return 0;
        }

        private static int RunWorker(CodeIndexOptions options)
        {
            if (!string.Equals(options.Worker, "snapshot", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Unsupported worker.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.ProjectRoot) || string.IsNullOrWhiteSpace(options.InputPath))
            {
                Console.Error.WriteLine("Snapshot worker requires project-root and input.");
                return 1;
            }

            if (!TryGetControlledPaths(options.ProjectRoot, options.InputPath, out var projectRoot, out var inputPath))
            {
                Console.Error.WriteLine("Snapshot worker input must be inside the project Code Index temporary directory.");
                return 1;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Snapshot worker input does not exist.");
                return 1;
            }

            AIBridgeCodeIndexSnapshotUtility.SnapshotRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<AIBridgeCodeIndexSnapshotUtility.SnapshotRequest>(
                    File.ReadAllText(inputPath, Encoding.UTF8));
            }
            catch
            {
                Console.Error.WriteLine("Snapshot worker input is invalid.");
                return 1;
            }

            if (request == null || !TryValidateSnapshotRequest(request, projectRoot))
            {
                Console.Error.WriteLine("Snapshot worker input contains an invalid project or snapshot path.");
                return 1;
            }

            if (options.WorkerCount > 0)
            {
                request.WorkerCount = options.WorkerCount;
            }

            request.ProjectRoot = projectRoot;
            request.WorkerCount = AIBridgeCodeIndexSnapshotUtility.ClampSnapshotWorkerCount(request.WorkerCount);
            if (request.OwnerPid <= 0 && options.OwnerPid > 0)
            {
                request.OwnerPid = options.OwnerPid;
            }

            if (request.OwnerStartTicks <= 0L && options.OwnerStartTicks > 0L)
            {
                request.OwnerStartTicks = options.OwnerStartTicks;
            }

            ApplyProcessPriority(options.Priority);
            string message;
            var success = AIBridgeCodeIndexSnapshotUtility.GenerateSnapshot(request, out message);
            Console.WriteLine(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["success"] = success,
                ["source"] = "snapshot-worker",
                ["message"] = success ? message : "Snapshot generation failed.",
                ["workerCount"] = request.WorkerCount
            }));
            return success ? 0 : 1;
        }

        private static bool TryGetControlledPaths(string projectRootValue, string inputPathValue, out string projectRoot, out string inputPath)
        {
            projectRoot = null;
            inputPath = null;
            if (string.IsNullOrWhiteSpace(projectRootValue) || string.IsNullOrWhiteSpace(inputPathValue)
                || !Path.IsPathRooted(projectRootValue) || !Path.IsPathRooted(inputPathValue))
            {
                return false;
            }

            try
            {
                projectRoot = Path.GetFullPath(projectRootValue);
                inputPath = Path.GetFullPath(inputPathValue);
                var tempDirectory = Path.Combine(projectRoot, ".aibridge", IndexDirectoryName, TempDirectoryName);
                return IsContainedPath(tempDirectory, inputPath);
            }
            catch
            {
                projectRoot = null;
                inputPath = null;
                return false;
            }
        }

        private static bool TryValidateSnapshotRequest(AIBridgeCodeIndexSnapshotUtility.SnapshotRequest request, string projectRoot)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProjectRoot) || string.IsNullOrWhiteSpace(request.SnapshotDirectory))
            {
                return false;
            }

            try
            {
                if (!PathsEqual(projectRoot, request.ProjectRoot))
                {
                    return false;
                }

                var snapshotDirectory = Path.GetFullPath(request.SnapshotDirectory);
                var expectedSnapshotDirectory = Path.Combine(projectRoot, ".aibridge", IndexDirectoryName, "snapshot");
                if (!PathsEqual(snapshotDirectory, expectedSnapshotDirectory))
                {
                    return false;
                }

                request.ProjectRoot = projectRoot;
                request.SnapshotDirectory = snapshotDirectory;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsContainedPath(string parentPath, string candidatePath)
        {
            var parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(parent, GetPathComparison());
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                GetPathComparison());
        }

        private static StringComparison GetPathComparison()
        {
            return Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private static void ApplyProcessPriority(string priority)
        {
            if (string.IsNullOrWhiteSpace(priority))
            {
                return;
            }

            try
            {
                if (string.Equals(priority, "low", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(priority, "below-normal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(priority, "belownormal", StringComparison.OrdinalIgnoreCase))
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
                }
            }
            catch
            {
                // worker 优先级只是资源调度优化，平台不支持时继续用普通优先级执行。
            }
        }
    }
}
