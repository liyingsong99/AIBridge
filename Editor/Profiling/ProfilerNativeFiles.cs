using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>当前已有 Profiler 历史的原生文件操作；不录制、不改变目标、不回退 JSON。</summary>
    internal static class ProfilerNativeFiles
    {
        internal const string NativeDirectory = ".aibridge/profiler/native";
        internal const long MaxLoadBytes = 2L * 1024 * 1024 * 1024;
        private const string Extension = ".data";
        private static readonly List<object> UnknownResources = new List<object>();
        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        internal sealed class Context
        {
            internal string ProjectRoot;
            internal string UnityVersion;
            internal INativeProfilerApi Api;
            internal Func<string> BusyReason;
            internal Action<object, FileStream> RetainUnknown = HoldUnknown;
        }

        public static CommandResult Execute(CommandRequest request, string action)
        {
            try
            {
                return Execute(request, action, new Context
                {
                    ProjectRoot = Directory.GetParent(Application.dataPath).FullName,
                    UnityVersion = Application.unityVersion,
                    Api = NativeProfilerApi.Resolve(),
                    BusyReason = EditorBusyReason
                });
            }
            catch (Exception ex)
            {
                return Failure(request, "unsupported", "Native Profiler setup failed: " + ex.GetType().Name);
            }
        }

        internal static CommandResult Execute(CommandRequest request, string action, Context context)
        {
            try
            {
                ValidateArguments(request, action);
                if (action == "native_capabilities") return Capabilities(request, context);
                var support = context.Api.Support;
                if (!support.State || (action == "save_profile" ? !support.Save : !support.Load))
                    return Failure(request, "unsupported", "Required native API signatures are unavailable.");
                var path = ResolvePath(context.ProjectRoot, RequestedPath(request, action));
                CheckState(context);
                object lease;
                if (!ProfilerNativeGate.TryAcquire(out lease))
                    return Failure(request, "busy", "Native Profiler/Memory gate is busy.");
                return RunExclusive(request, action, context, path, lease);
            }
            catch (Exception ex)
            {
                return Failure(request, "rejected", ex.Message);
            }
        }

        private static CommandResult RunExclusive(CommandRequest request, string action, Context context, string path, object lease)
        {
            var operation = new Operation { Request = request, Context = context, Path = path };
            CommandResult result = null;
            try
            {
                operation.State = CheckState(context);
                result = action == "save_profile" ? Save(operation) : Load(operation);
                return result;
            }
            catch (Exception ex)
            {
                // 原生调用抛出时不能断言工作已停止；保留 gate、源锁及临时源，不自动重放。
                operation.Unknown = operation.EnteredNative && !operation.NativeReturned;
                result = Failure(request, operation.Unknown ? "native_outcome_unknown" : "operation_failed",
                    ex.GetType().Name + ": " + ex.Message, operation.Unknown, operation.Unknown,
                    operation.Unknown ? RelativePath(operation.TemporaryPath) : null);
                return result;
            }
            finally
            {
                if (operation.Unknown)
                    context.RetainUnknown(lease, operation.Stream);
                else
                {
                    if (operation.Stream != null) operation.Stream.Dispose();
                    var retained = CleanupOwnedTemporary(operation);
                    if (retained && result != null && !result.success)
                        ((NativeFailure)result.data).retainedTemporaryPath = RelativePath(operation.TemporaryPath);
                    ProfilerNativeGate.Release(lease);
                }
            }
        }

        private static CommandResult Save(Operation operation)
        {
            var context = operation.Context;
            AssertAbsent(operation.Path);
            var directory = Path.GetDirectoryName(operation.Path);
            RejectReparseAncestors(directory);
            Directory.CreateDirectory(directory);
            ResolvePath(context.ProjectRoot, operation.Path);
            operation.TemporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + Extension);
            // CreateNew 失败不取得所有权，绝不先删目标或同名外来临时文件。
            using (var created = new FileStream(operation.TemporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                operation.OwnsTemporary = true;
                operation.Identity = FileIdentity.Read(created);
            }
            // Unity 原生 SaveProfile 独占打开输出；调用期间不能持有托管读取句柄。
            // 随机 CreateNew 占位保留原始文件身份，返回后重新打开核验；绝不删外来替换文件。
            operation.State = CheckState(context);
            ResolvePath(context.ProjectRoot, operation.TemporaryPath);
            operation.EnteredNative = true;
            var accepted = context.Api.Save(operation.TemporaryPath);
            operation.NativeReturned = true;
            if (!accepted) return Failure(operation.Request, "save_failed", "SaveProfile returned false.");
            ResolvePath(context.ProjectRoot, operation.TemporaryPath);
            operation.Stream = new FileStream(operation.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RequireOwnedIdentity(operation, operation.Stream);
            var size = operation.Stream.Length;
            if (size <= 0) return Failure(operation.Request, "save_failed", "SaveProfile produced no non-empty file.");
            Publish(operation);
            return CommandResult.Success(operation.Request.id, FileResult(operation, size));
        }

        private static void Publish(Operation operation)
        {
            // File.Move 的不覆盖语义是最终仲裁；即使最后一刻目标被创建，也保全旧文件。
            RequireOwnedIdentity(operation, operation.Stream);
            operation.Stream.Dispose();
            operation.Stream = null;
            ResolvePath(operation.Context.ProjectRoot, operation.TemporaryPath);
            ResolvePath(operation.Context.ProjectRoot, operation.Path);
            AssertAbsent(operation.Path);
            using (var identityCheck = new FileStream(operation.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                RequireOwnedIdentity(operation, identityCheck);
            File.Move(operation.TemporaryPath, operation.Path);
            operation.OwnsTemporary = false;
        }

        private static CommandResult Load(Operation operation)
        {
            ResolvePath(operation.Context.ProjectRoot, operation.Path);
            operation.Stream = new FileStream(operation.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ResolvePath(operation.Context.ProjectRoot, operation.Path);
            var size = operation.Stream.Length;
            if (!IsValidLoadSize(size))
                return Failure(operation.Request, "invalid_file", "Load requires a non-empty file no larger than 2 GiB.");
            operation.State = CheckState(operation.Context);
            operation.EnteredNative = true;
            var accepted = operation.Context.Api.Load(operation.Path, false);
            operation.NativeReturned = true;
            if (!accepted)
                return Failure(operation.Request, "load_failed", "LoadProfile returned false; shared history may have changed.", true);
            return CommandResult.Success(operation.Request.id, FileResult(operation, size));
        }

        internal static bool IsValidLoadSize(long size)
        {
            return size > 0 && size <= MaxLoadBytes;
        }

        private static object FileResult(Operation operation, long size)
        {
            return new
            {
                path = RelativePath(operation.Path),
                size,
                format = "unity-profiler-data",
                unityVersion = operation.Context.UnityVersion,
                currentTargetGuid = operation.State.CurrentTargetGuid,
                currentTargetName = operation.State.CurrentTargetName,
                historyProvenance = "unknown"
            };
        }

        private static CommandResult Capabilities(CommandRequest request, Context context)
        {
            var support = context.Api.Support;
            var unsupported = new List<object>(support.Unsupported);
            NativeProfilerState state = null;
            if (support.State)
            {
                try { state = context.Api.ReadState(); }
                catch (Exception ex) { unsupported.Add(new { feature = "current_state", reason = ex.GetType().Name }); }
            }
            return CommandResult.Success(request.id, new
            {
                unityVersion = context.UnityVersion,
                format = "unity-profiler-data",
                supported = new
                {
                    save_profile = support.Save && state != null,
                    load_profile = support.Load && state != null,
                    current_state = state != null,
                    saveReturnType = support.SaveReturnType
                },
                unsupported = unsupported.ToArray(),
                currentTargetGuid = state == null ? (int?)null : state.CurrentTargetGuid,
                currentTargetName = state == null ? null : state.CurrentTargetName,
                recording = state == null ? (bool?)null : state.Recording,
                gateBusy = ProfilerNativeGate.Busy,
                historyProvenance = "unknown"
            });
        }

        private static void ValidateArguments(CommandRequest request, string action)
        {
            if (request == null) throw new ArgumentException("Request is required.");
            if (action != "native_capabilities" && action != "save_profile" && action != "load_profile")
                throw new ArgumentException("Unknown native Profiler action.");
            if (request.@params != null)
                foreach (var key in request.@params.Keys)
                    if (key != "action" && (action == "native_capabilities" || (key != "path" && key != "confirm")))
                        throw new ArgumentException("Unsupported native Profiler parameter: " + key);
            if (action == "native_capabilities") return;
            object confirm;
            if (request.@params == null || !request.@params.TryGetValue("confirm", out confirm)
                || !(confirm is bool) || !(bool)confirm)
                throw new ArgumentException("Explicit --confirm true is required.");
        }

        private static string RequestedPath(CommandRequest request, string action)
        {
            object value;
            if (request.@params.TryGetValue("path", out value))
            {
                if (!(value is string) || string.IsNullOrWhiteSpace((string)value))
                    throw new ArgumentException("path must be a non-empty string.");
                return (string)value;
            }
            if (action == "load_profile") throw new ArgumentException("load_profile requires --path.");
            return NativeDirectory + "/" + Guid.NewGuid().ToString("N") + Extension;
        }

        internal static string ResolvePath(string projectRoot, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested) || requested.StartsWith("\\\\", StringComparison.Ordinal)
                || requested.StartsWith("//", StringComparison.Ordinal) || requested.IndexOf('\0') >= 0)
                throw new ArgumentException("UNC/device or empty paths are not allowed.");
            var segments = requested.Replace('\\', '/').Split('/');
            foreach (var segment in segments)
                if (segment == "..") throw new ArgumentException("Parent traversal is not allowed.");
            var project = Path.GetFullPath(projectRoot);
            var root = Path.GetFullPath(Path.Combine(project, NativeDirectory));
            var path = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(project, requested));
            if (!string.Equals(Path.GetDirectoryName(path), root, PathComparison)
                || !string.Equals(Path.GetExtension(path), Extension, PathComparison))
                throw new ArgumentException("Path must be a direct .data file under " + NativeDirectory + ".");
            ValidateFileName(Path.GetFileName(path));
            RejectReparseAncestors(path);
            FileAttributes attributes;
            if (TryAttributes(path, out attributes) && (attributes & FileAttributes.Directory) != 0)
                throw new IOException("Path is a directory.");
            return path;
        }

        private static void ValidateFileName(string name)
        {
            if (name.TrimEnd(' ', '.') != name || name.Length <= Extension.Length)
                throw new ArgumentException("Invalid .data filename.");
            foreach (var character in name)
                if (character < 32 || "<>:\"|?*\\".IndexOf(character) >= 0)
                    throw new ArgumentException("Invalid filename characters.");
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                    && stem[3] >= '1' && stem[3] <= '9'))
                throw new ArgumentException("Device filenames are not allowed.");
        }

        private static void RejectReparseAncestors(string path)
        {
            var current = Path.GetFullPath(path);
            while (current != null)
            {
                FileAttributes attributes;
                if (TryAttributes(current, out attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse/symlink paths are not allowed.");
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool TryAttributes(string path, out FileAttributes attributes)
        {
            try { attributes = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { attributes = default; return false; }
            catch (DirectoryNotFoundException) { attributes = default; return false; }
        }

        private static void AssertAbsent(string path)
        {
            FileAttributes attributes;
            if (TryAttributes(path, out attributes)) throw new IOException("Target already exists; overwrite is not supported.");
        }

        private static NativeProfilerState CheckState(Context context)
        {
            var busy = context.BusyReason();
            if (!string.IsNullOrEmpty(busy)) throw new InvalidOperationException(busy);
            var state = context.Api.ReadState();
            if (state == null || state.Recording) throw new InvalidOperationException("Profiler is recording or state is unavailable.");
            return state;
        }

        private static string EditorBusyReason()
        {
            if (EditorApplication.isCompiling) return "Editor is compiling.";
            if (EditorApplication.isUpdating) return "Editor is updating.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Editor is playing or changing Play Mode.";
            if (BuildPipeline.isBuildingPlayer) return "Editor is building a Player.";
            return null;
        }

        private static string RelativePath(string path)
        {
            return path == null ? null : NativeDirectory + "/" + Path.GetFileName(path);
        }

        private static CommandResult Failure(CommandRequest request, string code, string message,
            bool sideEffectsUnknown = false, bool nativeMayStillRun = false, string retainedTemporaryPath = null)
        {
            var result = CommandResult.Failure(request == null ? null : request.id, message);
            result.data = new NativeFailure { code = code, sideEffectsUnknown = sideEffectsUnknown,
                nativeMayStillRun = nativeMayStillRun, retainedTemporaryPath = retainedTemporaryPath };
            return result;
        }

        private static bool CleanupOwnedTemporary(Operation operation)
        {
            if (!operation.OwnsTemporary || operation.TemporaryPath == null) return false;
            try
            {
                ResolvePath(operation.Context.ProjectRoot, operation.TemporaryPath);
                // 按句柄身份核验，不能凭 creationTime 或路径存在就删除外来替换文件。
                FileIdentity.DeleteOwned(operation.TemporaryPath, operation.Identity);
            }
            catch (Exception) { return true; } // 清理失败不能遮蔽本次结果或误释放未知操作。
            return File.Exists(operation.TemporaryPath);
        }

        private sealed class NativeFailure
        {
            public string code { get; set; }
            public bool sideEffectsUnknown { get; set; }
            public bool nativeMayStillRun { get; set; }
            public string retainedTemporaryPath { get; set; }
        }

        private static void HoldUnknown(object lease, FileStream stream)
        {
            lock (UnknownResources)
            {
                UnknownResources.Add(lease);
                if (stream != null) UnknownResources.Add(stream);
            }
        }

        private static void RequireOwnedIdentity(Operation operation, FileStream stream)
        {
            if (operation.Identity != null && operation.Identity != FileIdentity.Read(stream))
                throw new IOException("Temporary source identity changed.");
        }

        private static class FileIdentity
        {
            private const uint DeleteAccess = 0x00010000;
            private const uint ReadAttributes = 0x80;

            internal static string Read(FileStream stream)
            {
                if (Path.DirectorySeparatorChar != '\\') return null;
                ByHandleInfo info;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out info))
                    throw new IOException("Cannot verify temporary file identity.");
                return info.VolumeSerial + ":" + info.FileIndexHigh + ":" + info.FileIndexLow;
            }

            internal static void DeleteOwned(string path, string identity)
            {
                // Windows 用持 DELETE 权限的同一文件句柄核验/标记删除，避免 check/delete 间误删替换文件。
                // 其它平台无可靠句柄级删除通道时保留源；不能退化为不安全的路径删除。
                if (identity == null || Path.DirectorySeparatorChar != '\\') return;
                using (var handle = CreateFile(path, DeleteAccess | ReadAttributes, 1 | 2 | 4,
                    IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) return;
                    ByHandleInfo info;
                    if (!GetFileInformationByHandle(handle, out info)) return;
                    var actual = info.VolumeSerial + ":" + info.FileIndexHigh + ":" + info.FileIndexLow;
                    if (actual != identity || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return;
                    var disposition = new FileDisposition { DeleteFile = true };
                    SetFileInformationByHandle(handle, 4, ref disposition, 1);
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ByHandleInfo
            {
                internal uint Attributes;
                internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                internal uint VolumeSerial;
                internal uint FileSizeHigh;
                internal uint FileSizeLow;
                internal uint NumberOfLinks;
                internal uint FileIndexHigh;
                internal uint FileIndexLow;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct FileDisposition
            {
                [MarshalAs(UnmanagedType.U1)] internal bool DeleteFile;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInfo info);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
            private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
                IntPtr security, uint mode, uint attributes, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
                ref FileDisposition information, uint size);
        }

        private sealed class Operation
        {
            internal CommandRequest Request;
            internal Context Context;
            internal string Path;
            internal FileStream Stream;
            internal NativeProfilerState State;
            internal string TemporaryPath;
            internal string Identity;
            internal bool OwnsTemporary;
            internal bool EnteredNative;
            internal bool NativeReturned;
            internal bool Unknown;
        }
    }
}
