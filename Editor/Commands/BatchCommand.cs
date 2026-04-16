using System;
using System.Collections.Generic;

namespace AIBridge.Editor
{
    /// <summary>
    /// Batch command execution.
    /// Supports executing multiple commands in a single request.
    /// </summary>
    public class BatchCommand : ICommand
    {
        public string Type => "batch";
        public bool RequiresRefresh => true;

        public string SkillDescription => @"### `batch` - Batch Commands

```bash
$CLI batch execute --commands '[{""type"":""editor"",""params"":{""action"":""log"",""message"":""Step 1""}}]'
$CLI batch from_file --file ""commands.json""
```";

        public CommandResult Execute(CommandRequest request)
        {
            var commandsParam = request.GetParam<object>("commands");
            if (commandsParam == null)
            {
                return CommandResult.Failure(request.id, "Missing 'commands' parameter");
            }

            var commands = commandsParam as List<object>;
            if (commands == null || commands.Count == 0)
            {
                return CommandResult.Failure(request.id, "'commands' must be a non-empty array");
            }

            var results = new List<object>();
            var successCount = 0;
            var failureCount = 0;

            foreach (var cmdObj in commands)
            {
                try
                {
                    var cmdDict = cmdObj as Dictionary<string, object>;
                    if (cmdDict == null)
                    {
                        results.Add(new { success = false, error = "Invalid command format" });
                        failureCount++;
                        continue;
                    }

                    var subRequest = new CommandRequest
                    {
                        id = $"{request.id}_{results.Count}",
                        type = cmdDict.ContainsKey("type") ? cmdDict["type"]?.ToString() : null,
                        @params = cmdDict.ContainsKey("params")
                            ? cmdDict["params"] as Dictionary<string, object>
                            : new Dictionary<string, object>()
                    };

                    if (string.IsNullOrEmpty(subRequest.type))
                    {
                        results.Add(new { success = false, error = "Missing 'type' in command" });
                        failureCount++;
                        continue;
                    }

                    if (!CommandRegistry.TryGetCommand(subRequest.type, out var command))
                    {
                        results.Add(new { success = false, error = $"Unknown command: {subRequest.type}" });
                        failureCount++;
                        continue;
                    }

                    var result = command.Execute(subRequest);
                    results.Add(new
                    {
                        type = subRequest.type,
                        success = result.success,
                        data = result.data,
                        error = result.error
                    });

                    if (result.success) successCount++;
                    else failureCount++;
                }
                catch (Exception ex)
                {
                    results.Add(new { success = false, error = ex.Message });
                    failureCount++;
                }
            }

            return CommandResult.Success(request.id, new
            {
                totalCommands = commands.Count,
                successCount = successCount,
                failureCount = failureCount,
                results = results
            });
        }
    }
}
