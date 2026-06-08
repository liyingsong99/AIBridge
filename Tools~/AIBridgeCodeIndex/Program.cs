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
                Console.Error.WriteLine("Project root does not exist: " + options.ProjectRoot);
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
                Console.Error.WriteLine("Unsupported worker: " + options.Worker);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.InputPath))
            {
                Console.Error.WriteLine("--input is required for snapshot worker.");
                return 1;
            }

            ApplyProcessPriority(options.Priority);

            var inputPath = Path.GetFullPath(options.InputPath);
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Snapshot worker input does not exist: " + inputPath);
                return 1;
            }

            var request = JsonConvert.DeserializeObject<AIBridgeCodeIndexSnapshotUtility.SnapshotRequest>(
                File.ReadAllText(inputPath, Encoding.UTF8));
            if (request == null)
            {
                Console.Error.WriteLine("Snapshot worker input is empty.");
                return 1;
            }

            if (options.WorkerCount > 0)
            {
                request.WorkerCount = options.WorkerCount;
            }

            if (string.IsNullOrWhiteSpace(request.ProjectRoot) && !string.IsNullOrWhiteSpace(options.ProjectRoot))
            {
                request.ProjectRoot = Path.GetFullPath(options.ProjectRoot);
            }

            if (string.IsNullOrWhiteSpace(request.SnapshotDirectory) && !string.IsNullOrWhiteSpace(request.ProjectRoot))
            {
                request.SnapshotDirectory = Path.Combine(request.ProjectRoot, ".aibridge", "code-index", "snapshot");
            }

            if (request.OwnerPid <= 0 && options.OwnerPid > 0)
            {
                request.OwnerPid = options.OwnerPid;
            }

            if (request.OwnerStartTicks <= 0L && options.OwnerStartTicks > 0L)
            {
                request.OwnerStartTicks = options.OwnerStartTicks;
            }

            string message;
            var success = AIBridgeCodeIndexSnapshotUtility.GenerateSnapshot(request, out message);
            Console.WriteLine(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["success"] = success,
                ["source"] = "snapshot-worker",
                ["message"] = message,
                ["projectRoot"] = request.ProjectRoot,
                ["snapshotPath"] = request.SnapshotDirectory,
                ["workerCount"] = request.WorkerCount,
                ["ownerPid"] = request.OwnerPid,
                ["ownerStartTicks"] = request.OwnerStartTicks
            }));
            return success ? 0 : 1;
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
