using System;
using System.IO;
using System.Text;

namespace AIBridge.Runtime.Internal
{
    public static class AIBridgeAtomicFile
    {
        public static void WriteTextAtomic(string path, string text, Encoding encoding, bool ensureDirectory = true)
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
                ReplaceOrMove(tempPath, path);
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

        private static void ReplaceOrMove(string tempPath, string finalPath)
        {
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
