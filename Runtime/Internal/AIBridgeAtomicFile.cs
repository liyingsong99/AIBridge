using System;
using System.IO;
using System.Text;

namespace AIBridge.Runtime.Internal
{
    public static class AIBridgeAtomicFile
    {
        public static void WriteTextAtomic(string path, string text, Encoding encoding, bool ensureDirectory = true)
        {
            WriteTextAtomic(path, text, encoding, ensureDirectory, false);
        }

        public static void WriteTextAtomic(string path, string text, Encoding encoding, bool ensureDirectory, bool requireAtomicReplace)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is required.", nameof(path));
            }

            encoding = encoding ?? new UTF8Encoding(false);

            if (ensureDirectory)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            // 先写同目录临时文件再改名，避免另一端监听到最终文件名后读到半截 JSON。
            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, text ?? string.Empty, encoding);
                ReplaceOrMove(tempPath, path, requireAtomicReplace);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        public static string ReadAllText(string path, Encoding encoding)
        {
            encoding = encoding ?? Encoding.UTF8;
            return File.ReadAllText(path, encoding);
        }

        private static void ReplaceOrMove(string tempPath, string finalPath, bool requireAtomicReplace)
        {
            if (requireAtomicReplace)
            {
                ReplacePreservingPrevious(tempPath, finalPath);
                return;
            }
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
                // 安全日志要求失败时保留旧版本；禁止先删后移造成状态暂时消失。
                if (requireAtomicReplace) throw;
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(tempPath, finalPath);
            }
        }

        private static void ReplacePreservingPrevious(string tempPath, string finalPath)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(finalPath)) File.Replace(tempPath, finalPath, null);
                    else File.Move(tempPath, finalPath);
                    return;
                }
                catch (IOException exception)
                {
                    // Windows ReplaceFile 偶发 1175/1176（扫描器占用）；只重试本地原子替换。
                    // 不扩大为网络/任意IO重试，不删旧状态，最后仍失败关闭。
                    var nativeCode = exception.HResult & 0xffff;
                    if (attempt >= maxAttempts || (nativeCode != 1175 && nativeCode != 1176) || !File.Exists(tempPath)) throw;
                    System.Threading.Thread.Sleep(15 * attempt);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
