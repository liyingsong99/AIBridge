using System;
using UnityEditor;
using UnityEditor.Compilation;

namespace AIBridge.Editor
{
    /// <summary>
    /// Editor operations: undo, redo, compile, play mode control
    /// Supports multiple sub-commands via "action" parameter
    /// </summary>
    public class EditorCommand : ICommand
    {
        public string Type => "editor";
        public bool RequiresRefresh => false;  // Editor commands handle refresh internally if needed

        public string SkillDescription => @"### `editor` - Editor Control

```bash
$CLI editor log --message ""Hello"" [--logType Warning|Error]
$CLI editor undo [--count 3]
$CLI editor redo
$CLI editor compile  # Simple compile, use `compile` command for full features
$CLI editor refresh [--forceUpdate true]
$CLI editor play [--domainReload false]
$CLI editor stop|pause
$CLI editor get_state
```";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "undo");

            try
            {
                switch (action.ToLower())
                {
                    case "undo":
                        return Undo(request);
                    case "redo":
                        return Redo(request);
                    case "compile":
                        // Redirect to CompileCommand for backward compatibility
                        return RedirectToCompileCommand(request);
                    case "refresh":
                        return Refresh(request);
                    case "play":
                        return Play(request);
                    case "stop":
                        return Stop(request);
                    case "pause":
                        return Pause(request);
                    case "get_state":
                        return GetState(request);
                    case "log":
                        return Log(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: undo, redo, compile, refresh, play, stop, pause, get_state, log");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult Undo(CommandRequest request)
        {
            var count = request.GetParam("count", 1);

            for (var i = 0; i < count; i++)
            {
                UnityEditor.Undo.PerformUndo();
            }

            return CommandResult.Success(request.id, new
            {
                action = "undo",
                count = count
            });
        }

        private CommandResult Redo(CommandRequest request)
        {
            var count = request.GetParam("count", 1);

            for (var i = 0; i < count; i++)
            {
                UnityEditor.Undo.PerformRedo();
            }

            return CommandResult.Success(request.id, new
            {
                action = "redo",
                count = count
            });
        }

        /// <summary>
        /// Redirect compile action to CompileCommand for backward compatibility.
        /// The new CompileCommand provides more features (status polling, dotnet build).
        /// </summary>
        private CommandResult RedirectToCompileCommand(CommandRequest request)
        {
            // Create a new request for CompileCommand with action=start
            var compileRequest = new CommandRequest
            {
                id = request.id,
                type = "compile",
                @params = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "action", "start" }
                }
            };

            var compileCommand = new CompileCommand();
            return compileCommand.Execute(compileRequest);
        }

        private CommandResult Refresh(CommandRequest request)
        {
            var forceUpdate = request.GetParam("forceUpdate", false);
            var options = forceUpdate
                ? ImportAssetOptions.ForceUpdate
                : ImportAssetOptions.Default;

            AssetDatabase.Refresh(options);

            return CommandResult.Success(request.id, new
            {
                action = "refresh",
                forceUpdate = forceUpdate
            });
        }

        private CommandResult Play(CommandRequest request)
        {
            if (EditorApplication.isPlaying)
            {
                return CommandResult.Success(request.id, new
                {
                    action = "play",
                    alreadyPlaying = true
                });
            }

            // Configure Enter Play Mode Settings based on domainReload parameter
            var domainReload = request.GetParam("domainReload", true);
            if (domainReload)
            {
                EditorSettings.enterPlayModeOptionsEnabled = false;
            }
            else
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            }

            EditorApplication.isPlaying = true;

            return CommandResult.Success(request.id, new
            {
                action = "play",
                started = true,
                domainReload = domainReload
            });
        }

        private CommandResult Stop(CommandRequest request)
        {
            if (!EditorApplication.isPlaying)
            {
                return CommandResult.Success(request.id, new
                {
                    action = "stop",
                    alreadyStopped = true
                });
            }

            EditorApplication.isPlaying = false;

            return CommandResult.Success(request.id, new
            {
                action = "stop",
                stopped = true
            });
        }

        private CommandResult Pause(CommandRequest request)
        {
            var toggle = request.GetParam("toggle", true);

            if (toggle)
            {
                EditorApplication.isPaused = !EditorApplication.isPaused;
            }
            else
            {
                var pause = request.GetParam("pause", true);
                EditorApplication.isPaused = pause;
            }

            return CommandResult.Success(request.id, new
            {
                action = "pause",
                isPaused = EditorApplication.isPaused
            });
        }

        private CommandResult GetState(CommandRequest request)
        {
            return CommandResult.Success(request.id, new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                applicationPath = EditorApplication.applicationPath,
                applicationContentsPath = EditorApplication.applicationContentsPath,
                enterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                enterPlayModeOptions = EditorSettings.enterPlayModeOptions.ToString()
            });
        }

        private CommandResult Log(CommandRequest request)
        {
            var message = request.GetParam<string>("message");
            if (string.IsNullOrEmpty(message))
            {
                return CommandResult.Failure(request.id, "Parameter 'message' is required");
            }

            var logType = request.GetParam("logType", "Log");

            switch (logType.ToLower())
            {
                case "warning":
                    UnityEngine.Debug.LogWarning($"[AIBridge] {message}");
                    break;
                case "error":
                    UnityEngine.Debug.LogError($"[AIBridge] {message}");
                    break;
                default:
                    UnityEngine.Debug.Log($"[AIBridge] {message}");
                    break;
            }

            return CommandResult.Success(request.id, new
            {
                action = "log",
                message = message,
                logType = logType
            });
        }
    }
}
