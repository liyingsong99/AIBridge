using System;
using System.IO;
using System.Text;

namespace AIBridge.Editor
{
    /// <summary>受限 run 的源码捕获与锁定快照；不是 C# 代码权限沙箱。</summary>
    public static class PipelineRunSource
    {
        private const int MaxSourceBytes = 512 * 1024;
        private const string CodeDirectory = ".aibridge/code";
        private const string SnapshotDirectory = ".aibridge/tmp/pipeline-run";

        public static string ValidatePath(string projectRoot, string file)
        {
            var root = Path.GetFullPath(Path.Combine(projectRoot, CodeDirectory));
            var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file));
            // Unix 可存在仅大小写不同的相邻目录，不能用 Windows 比较规则放宽来源根。
            var pathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(path), root, pathComparison)
                || !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("run source must be a direct .cs file.");
            RejectReparsePath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Source is not a file.");
            return path;
        }

        public static string ReadSource(string projectRoot, string file)
        {
            var path = ValidatePath(projectRoot, file);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                RejectReparsePath(path);
                if (stream.Length > MaxSourceBytes) throw new ArgumentException("Source is too large.");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var source = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
                        throw new ArgumentException("Source is empty or too large.");
                    return source;
                }
            }
        }

        public static Snapshot Capture(string projectRoot, string source, string sourcePath)
        {
            // 直调适配器亦须经过来源路径与大小门禁；不再读取文件正文，保持捕获文本身份。
            ValidatePath(projectRoot, sourcePath);
            if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
                throw new ArgumentException("Invalid captured source.");
            var directory = Path.GetFullPath(Path.Combine(projectRoot, SnapshotDirectory));
            RejectExistingAncestors(directory);
            Directory.CreateDirectory(directory);
            RejectReparsePath(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            FileStream stream = null;
            var created = false;
            try
            {
                var mappedPath = sourcePath.Replace("\\", "/").Replace("\"", "_").Replace("\r", "_").Replace("\n", "_");
                var content = "#line 1 \"" + mappedPath + "\"\n" + source;
                var bytes = new UTF8Encoding(false).GetBytes(content);
                using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    writer.Write(bytes, 0, bytes.Length);
                }
                // Pipeline 的 File.ReadAllText 也使用 FileShare.Read，不能保留带 Write access 的句柄。
                // 重新打开只读锁后复核正文，关闭写句柄到取得锁之间的替换不能改变已捕获源码。
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                RejectReparsePath(path);
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
                    if (reader.ReadToEnd() != content) throw new IOException("Captured source changed.");
                return new Snapshot(path, stream);
            }
            catch
            {
                stream?.Dispose();
                // CreateNew 失败时不能删除同名外来文件；仅清理本次已创建的快照。
                if (created) File.Delete(path);
                throw;
            }
        }

        private static void RejectExistingAncestors(string path)
        {
            var current = Path.GetFullPath(path);
            while (current != null)
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reparse path is not allowed.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        private static void RejectReparsePath(string path)
        {
            File.GetAttributes(path); // 不把权限/IO 错误当作不存在。
            RejectExistingAncestors(path);
        }

        public sealed class Snapshot : IDisposable
        {
            public string Path { get; }
            private FileStream _stream;
            public Snapshot(string path, FileStream stream) { Path = path; _stream = stream; }
            public void Dispose()
            {
                _stream?.Dispose();
                _stream = null;
            }
            public void DeleteAfterConfirmedOutcome()
            {
                Dispose();
                try { File.Delete(Path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
