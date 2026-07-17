using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AIBridgeCLI.Core
{
    internal sealed class BoundedProcessOutput
    {
        public string Text { get; set; }
        public long CharsRead { get; set; }
        public bool Truncated { get; set; }
        public long TruncatedLineCount { get; set; }
    }

    internal static class BoundedProcessOutputReader
    {
        private const int ReadBufferSize = 4096;

        public static Task<BoundedProcessOutput> ReadAsync(TextReader reader, int maxCapturedChars)
        {
            return ReadAsync(reader, maxCapturedChars, 0, null);
        }

        public static async Task<BoundedProcessOutput> ReadAsync(
            TextReader reader,
            int maxCapturedChars,
            int maxLineChars,
            Action<string, bool> onLine)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var captureLimit = Math.Max(0, maxCapturedChars);
            var lineLimit = Math.Max(0, maxLineChars);
            var captured = new StringBuilder(Math.Min(captureLimit, ReadBufferSize));
            var line = onLine == null ? null : new StringBuilder(Math.Min(lineLimit, ReadBufferSize));
            var buffer = new char[ReadBufferSize];
            var charsRead = 0L;
            var lineTruncated = false;
            var truncatedLineCount = 0L;

            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                charsRead += read;
                var remaining = captureLimit - captured.Length;
                if (remaining > 0)
                {
                    captured.Append(buffer, 0, Math.Min(remaining, read));
                }

                if (onLine == null)
                {
                    continue;
                }

                for (var i = 0; i < read; i++)
                {
                    var character = buffer[i];
                    if (character == '\n')
                    {
                        EmitLine(line, lineTruncated, onLine);
                        if (lineTruncated)
                        {
                            truncatedLineCount++;
                        }

                        line.Length = 0;
                        lineTruncated = false;
                        continue;
                    }

                    if (line.Length < lineLimit)
                    {
                        line.Append(character);
                    }
                    else
                    {
                        lineTruncated = true;
                    }
                }
            }

            if (onLine != null && (line.Length > 0 || lineTruncated))
            {
                EmitLine(line, lineTruncated, onLine);
                if (lineTruncated)
                {
                    truncatedLineCount++;
                }
            }

            return new BoundedProcessOutput
            {
                Text = captured.ToString(),
                CharsRead = charsRead,
                Truncated = charsRead > captured.Length,
                TruncatedLineCount = truncatedLineCount
            };
        }

        private static void EmitLine(StringBuilder line, bool truncated, Action<string, bool> onLine)
        {
            var length = line.Length;
            if (length > 0 && line[length - 1] == '\r')
            {
                length--;
            }

            onLine(length == line.Length ? line.ToString() : line.ToString(0, length), truncated);
        }
    }
}
