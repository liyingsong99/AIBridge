using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// 批处理命令构建器：执行脚本文件或脚本文本
    /// </summary>
    public class BatchCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "batch";
        public override string Description => "Execute script file or script text";

        public override string[] Actions => new[] { "from_file", "from_text" };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["from_file"] = new List<ParameterInfo>
            {
                new ParameterInfo("file", "Path to script file (.txt)", true)
            },
            ["from_text"] = new List<ParameterInfo>
            {
                new ParameterInfo("text", "Script text content", false),
                new ParameterInfo("stdin", "Read from standard input", false, "false"),
                new ParameterInfo("output-dir", "Script save directory (optional)", false),
                new ParameterInfo("name", "Script name (optional)", false),
                new ParameterInfo("keep-file", "Keep file after execution", false, "false")
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var request = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = Type,
                @params = new Dictionary<string, object>()
            };

            if (action == "from_file")
            {
                // 从文件执行脚本
                if (!options.TryGetValue("file", out var filePath))
                {
                    throw new ArgumentException("Missing required parameter: --file");
                }

                if (!File.Exists(filePath))
                {
                    throw new ArgumentException($"File not found: {filePath}");
                }

                // 验证文件扩展名
                if (!filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Script file must be .txt format");
                }

                request.@params["action"] = "from_file";
                request.@params["file"] = Path.GetFullPath(filePath);
            }
            else if (action == "from_text")
            {
                // 从文本执行脚本
                string scriptText = null;

                // 从标准输入读取
                if (options.TryGetValue("stdin", out var stdinValue) && stdinValue.ToLower() == "true")
                {
                    scriptText = Console.In.ReadToEnd();
                }
                // 从 --text 参数读取
                else if (options.TryGetValue("text", out var textValue))
                {
                    scriptText = textValue;
                }

                if (string.IsNullOrEmpty(scriptText))
                {
                    throw new ArgumentException("Script text is required. Use --text or --stdin.");
                }

                // 生成脚本文件路径（写入 AIBridgeCache/scripts 目录）
                string cacheDir = PathHelper.GetExchangeDirectory();
                string scriptsDir = Path.Combine(cacheDir, "scripts");
                string scriptName = options.TryGetValue("name", out var name) ? name : $"script_{DateTime.Now:yyyyMMddHHmmss}";
                bool keepFile = options.TryGetValue("keep-file", out var keep) && keep.ToLower() == "true";

                // 确保脚本目录存在
                if (!Directory.Exists(scriptsDir))
                {
                    Directory.CreateDirectory(scriptsDir);
                }

                // 生成完整路径
                string scriptPath = Path.Combine(scriptsDir, $"{scriptName}.txt");

                // 写入脚本文件
                File.WriteAllText(scriptPath, scriptText, Encoding.UTF8);

                request.@params["action"] = "from_text";
                request.@params["scriptPath"] = Path.GetFullPath(scriptPath);
                request.@params["keepFile"] = keepFile;
            }
            else
            {
                throw new ArgumentException($"Unknown action: {action}");
            }

            return request;
        }
    }
}
