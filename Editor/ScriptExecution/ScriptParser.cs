using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AIBridge.Editor.ScriptExecution.Commands;

namespace AIBridge.Editor.ScriptExecution
{
    /// <summary>
    /// 脚本解析器，将 .txt 文件解析为命令对象列表
    /// </summary>
    public static class ScriptParser
    {
        /// <summary>
        /// 解析脚本文件
        /// </summary>
        /// <param name="scriptPath">脚本文件路径</param>
        /// <returns>命令列表</returns>
        public static List<IScriptCommand> Parse(string scriptPath)
        {
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"脚本文件不存在: {scriptPath}");
            }

            var commands = new List<IScriptCommand>();
            var lines = File.ReadAllLines(scriptPath);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // 跳过空行和注释
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                {
                    continue;
                }

                try
                {
                    var command = ParseLine(line);
                    if (command != null)
                    {
                        commands.Add(command);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"解析脚本第 {i + 1} 行失败: {line}\n错误: {ex.Message}", ex);
                }
            }

            return commands;
        }

        /// <summary>
        /// 解析单行命令
        /// </summary>
        private static IScriptCommand ParseLine(string line)
        {
            // 移除行内注释
            var commentIndex = line.IndexOf('#');
            if (commentIndex > 0)
            {
                line = line.Substring(0, commentIndex).Trim();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            // call [command] [args...]
            if (line.StartsWith("call ", StringComparison.OrdinalIgnoreCase))
            {
                var args = line.Substring(5).Trim();
                
                // 提取超时参数（如果有）
                var timeoutMatch = Regex.Match(args, @"--timeout\s+(\d+)");
                var timeout = timeoutMatch.Success ? int.Parse(timeoutMatch.Groups[1].Value) : 60000;
                
                return new CallCommand(args, timeout);
            }

            if (line.Equals("wait_compile", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("wait_compile ", StringComparison.OrdinalIgnoreCase))
            {
                var args = line.Length > "wait_compile".Length ? line.Substring("wait_compile".Length).Trim() : string.Empty;
                return new WaitCompileCommand(ParseOptionalInt(args, 60000));
            }

            if (line.Equals("wait_playmode", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("wait_playmode ", StringComparison.OrdinalIgnoreCase))
            {
                var args = line.Length > "wait_playmode".Length ? line.Substring("wait_playmode".Length).Trim() : string.Empty;
                return ParseWaitPlayMode(args);
            }

            if (line.Equals("assert_log_empty", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("assert_log_empty ", StringComparison.OrdinalIgnoreCase))
            {
                var args = line.Length > "assert_log_empty".Length ? line.Substring("assert_log_empty".Length).Trim() : string.Empty;
                return ParseAssertLogEmpty(args);
            }

            if (line.StartsWith("assert_object ", StringComparison.OrdinalIgnoreCase))
            {
                var path = ScriptTextUtility.StripOptionalQuotes(line.Substring("assert_object".Length).Trim());
                return new AssertObjectCommand(path);
            }

            if (line.StartsWith("set_var ", StringComparison.OrdinalIgnoreCase))
            {
                return ParseSetVar(line.Substring("set_var".Length).Trim());
            }

            if (line.StartsWith("print_var ", StringComparison.OrdinalIgnoreCase))
            {
                var name = line.Substring("print_var".Length).Trim();
                return new PrintVarCommand(name);
            }

            if (line.Equals("dialog", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("dialog ", StringComparison.OrdinalIgnoreCase))
            {
                DialogClickCommand command;
                string error;
                if (DialogClickCommand.TryParse(line, out command, out error))
                {
                    return command;
                }

                throw new Exception(string.IsNullOrEmpty(error) ? $"未知的 dialog 命令: {line}" : error);
            }

            // delay [milliseconds]
            if (line.StartsWith("delay ", StringComparison.OrdinalIgnoreCase))
            {
                var msStr = line.Substring(6).Trim();
                if (int.TryParse(msStr, out var ms))
                {
                    return new DelayCommand(ms);
                }
                throw new Exception($"无效的延迟时间: {msStr}");
            }

            // menu [menuPath]
            if (line.StartsWith("menu ", StringComparison.OrdinalIgnoreCase))
            {
                var menuPath = line.Substring(5).Trim();
                return new MenuCommand(menuPath);
            }

            // log "message" 或 log message
            if (line.StartsWith("log ", StringComparison.OrdinalIgnoreCase))
            {
                var message = line.Substring(4).Trim();
                
                // 移除引号（如果有）
                if (message.StartsWith("\"") && message.EndsWith("\""))
                {
                    message = message.Substring(1, message.Length - 2);
                }
                
                return new LogCommand(message);
            }

            throw new Exception($"未知的命令类型: {line}");
        }

        private static IScriptCommand ParseWaitPlayMode(string args)
        {
            var targetPlaying = true;
            var timeoutMs = 30000;
            if (!string.IsNullOrWhiteSpace(args))
            {
                var parts = Regex.Split(args, @"\s+");
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    targetPlaying = ParsePlayModeTarget(parts[0]);
                }

                if (parts.Length > 1 && int.TryParse(parts[1], out var parsedTimeout))
                {
                    timeoutMs = parsedTimeout;
                }
            }

            return new WaitPlayModeCommand(targetPlaying, timeoutMs);
        }

        private static bool ParsePlayModeTarget(string value)
        {
            if (string.Equals(value, "stopped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "exit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "exited", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                || value == "0")
            {
                return false;
            }

            if (string.Equals(value, "playing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "play", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "entered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || value == "1")
            {
                return true;
            }

            throw new Exception($"无效的 PlayMode 目标状态: {value}");
        }

        private static IScriptCommand ParseAssertLogEmpty(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return new AssertLogEmptyCommand("Error", null, 500);
            }

            var parts = Regex.Split(args, @"\s+");
            var logType = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "Error";
            var count = 500;
            if (parts.Length > 1 && int.TryParse(parts[1], out var parsedCount))
            {
                count = parsedCount;
            }

            return new AssertLogEmptyCommand(logType, null, count);
        }

        private static IScriptCommand ParseSetVar(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                throw new Exception("set_var 需要变量名和值");
            }

            var separatorIndex = args.IndexOf('=');
            string name;
            string value;
            if (separatorIndex > 0)
            {
                name = args.Substring(0, separatorIndex).Trim();
                value = args.Substring(separatorIndex + 1).Trim();
            }
            else
            {
                var match = Regex.Match(args, @"^(?<name>\S+)\s+(?<value>.*)$");
                if (!match.Success)
                {
                    throw new Exception("set_var 需要变量名和值");
                }

                name = match.Groups["name"].Value.Trim();
                value = match.Groups["value"].Value.Trim();
            }

            return new SetVarCommand(name, ScriptTextUtility.StripOptionalQuotes(value));
        }

        private static int ParseOptionalInt(string value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            if (int.TryParse(value, out var parsed))
            {
                return parsed;
            }

            throw new Exception($"无效的数字参数: {value}");
        }

        /// <summary>
        /// 验证脚本语法
        /// </summary>
        /// <param name="scriptPath">脚本文件路径</param>
        /// <returns>验证结果（成功返回 null，失败返回错误信息）</returns>
        public static string Validate(string scriptPath)
        {
            try
            {
                Parse(scriptPath);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
