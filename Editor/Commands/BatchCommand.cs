using System;
using System.IO;
using AIBridge.Editor.ScriptExecution;

namespace AIBridge.Editor
{
    /// <summary>
    /// 批处理命令执行：执行脚本文件或脚本文本
    /// </summary>
    public class BatchCommand : ICommand, ICommandSkillDocProvider
    {
        public string Type => "batch";
        public bool RequiresRefresh => true;

        public CommandSkillDoc SkillDoc => new CommandSkillDoc(SkillDescription)
        {
            TargetSkillName = "aibridge-batch-script",
            TargetReferenceFileName = "batch-script-reference.md"
        };

        public string SkillDescription => @"### `batch` - 脚本自动化执行

**用途**：自动化 Unity 编辑器操作和 CLI 命令执行，支持编译暂停/恢复

**Actions**：
- `from_text` - 直接执行脚本文本（自动写入 `.aibridge/scripts` 临时目录）
- `from_file` - 执行已有脚本文件（.txt 格式）

**脚本语法**：
```
log ""消息""              # 输出日志
delay 毫秒数            # 延迟执行
call [CLI命令] [参数]   # 调用 AIBridge CLI（可选 --timeout 毫秒数）
menu 菜单路径           # 执行编辑器菜单项
wait_compile [timeoutMs]                 # 等待 Unity 编译完成
wait_playmode playing|stopped [timeoutMs] # 等待进入/退出 PlayMode
assert_log_empty [Error|Warning|Log]      # 断言 Console 指定最低等级日志为空
assert_object ""Canvas/Button""           # 断言场景对象存在
set_var name value                       # 设置脚本变量
print_var name                           # 打印脚本变量
dialog click ok | yes | Save             # 声明后续弹窗自动点击；再次声明会覆盖前一个策略
# 注释                 # 行注释
```

**使用示例**：
```bash
# 直接执行脚本文本
$CLI batch from_text --text ""call editor log 'Hello'\ndelay 1000""

# 执行并保存脚本到 `.aibridge/scripts` 目录
$CLI batch from_text --text ""..."" --name ""my_script"" --keep-file

# 执行已有脚本文件
$CLI batch from_file --file ""script.txt""
```

**脚本示例**：
```
# 自动化构建流程
log ""开始构建""
dialog click ok | yes | Save
call compile unity
wait_compile 120000
call scene get_hierarchy --depth 2
assert_log_empty Error
menu File/Save Project
```

**典型场景**：编译流程、场景批处理、资源管理、重复任务自动化";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam<string>("action");
            if (string.IsNullOrEmpty(action))
            {
                return CommandResult.Failure(request.id, "Missing 'action' parameter");
            }

            if (action == "from_file")
            {
                return ExecuteFromFile(request);
            }
            else if (action == "from_text")
            {
                return ExecuteFromText(request);
            }
            else
            {
                return CommandResult.Failure(request.id, $"Unknown action: {action}");
            }
        }

        /// <summary>
        /// 从文件执行脚本
        /// </summary>
        private CommandResult ExecuteFromFile(CommandRequest request)
        {
            var filePath = request.GetParam<string>("file");
            if (string.IsNullOrEmpty(filePath))
            {
                return CommandResult.Failure(request.id, "Missing 'file' parameter");
            }

            // 验证文件存在
            if (!File.Exists(filePath))
            {
                return CommandResult.Failure(request.id, $"Script file not found: {filePath}");
            }

            // 验证文件扩展名
            if (!filePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(request.id, "Script file must be .txt format");
            }

            // 调用 ScriptExecutor 执行脚本（异步）
            return ExecuteScriptViaExecutor(request.id, filePath, false);
        }

        /// <summary>
        /// 从文本执行脚本
        /// </summary>
        private CommandResult ExecuteFromText(CommandRequest request)
        {
            var scriptPath = request.GetParam<string>("scriptPath");
            if (string.IsNullOrEmpty(scriptPath))
            {
                return CommandResult.Failure(request.id, "Missing 'scriptPath' parameter");
            }

            var keepFile = request.GetParam<bool>("keepFile");

            // 调用 ScriptExecutor 执行脚本（异步）
            return ExecuteScriptViaExecutor(request.id, scriptPath, !keepFile);
        }

        /// <summary>
        /// 通过 ScriptExecutor 执行脚本（异步执行，结果由 ScriptExecutor 基于持久化状态写回）
        /// </summary>
        private CommandResult ExecuteScriptViaExecutor(string requestId, string scriptPath, bool deleteAfterExecution)
        {
            try
            {
                // 检查是否已有脚本正在执行
                if (ScriptExecutor.IsExecuting)
                {
                    return CommandResult.Failure(requestId, "Another script is already executing. Please wait for it to complete.");
                }

                // 调用 ScriptExecutor 启动异步执行
                ScriptExecutor.Execute(scriptPath, requestId, deleteAfterExecution);

                // 立即返回 null（表示异步处理，结果将通过文件写入）
                return null;
            }
            catch (Exception ex)
            {
                return CommandResult.Failure(requestId, $"Script execution failed: {ex.Message}");
            }
        }
    }
}
