using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace AIBridge.Editor
{
    /// <summary>
    /// Help command.
    /// Returns information about all registered commands.
    /// </summary>
    [Description("Returns help information about available commands")]
    public class HelpCommand : ICommand
    {
        public string Type => "help";
        public bool RequiresRefresh => false;
        public string SkillDescription => null;

        public CommandResult Execute(CommandRequest request)
        {
            var commandType = request.GetParam<string>("command");

            if (!string.IsNullOrEmpty(commandType))
            {
                // Return detailed help for specific command
                return GetCommandHelp(request.id, commandType);
            }

            // Return all commands list
            return GetAllCommandsHelp(request.id);
        }

        private CommandResult GetAllCommandsHelp(string requestId)
        {
            var commands = new List<object>();
            var registeredTypes = CommandRegistry.GetRegisteredTypes();

            foreach (var type in registeredTypes)
            {
                if (CommandRegistry.TryGetCommand(type, out var command))
                {
                    var info = GetCommandInfo(command);
                    commands.Add(info);
                }
            }

            return CommandResult.Success(requestId, new
            {
                totalCommands = commands.Count,
                commands = commands,
                usage = "Use { \"type\": \"help\", \"params\": { \"command\": \"<command_type>\" } } for detailed help"
            });
        }

        private CommandResult GetCommandHelp(string requestId, string commandType)
        {
            if (!CommandRegistry.TryGetCommand(commandType, out var command))
            {
                var availableTypes = CommandRegistry.GetRegisteredTypes();
                return CommandResult.Failure(requestId,
                    $"Command not found: {commandType}. Available commands: {string.Join(", ", availableTypes)}");
            }

            return CommandResult.Success(requestId, GetCommandInfo(command, detailed: true));
        }

        private object GetCommandInfo(ICommand command, bool detailed = false)
        {
            var type = command.GetType();
            var description = GetDescription(type);

            var info = new Dictionary<string, object>
            {
                { "type", command.Type },
                { "description", description },
                { "requiresRefresh", command.RequiresRefresh },
                { "assemblyName", type.Assembly.GetName().Name }
            };

            if (detailed)
            {
                // Add usage examples based on command type
                info["examples"] = GetExamples(command.Type);
            }

            return info;
        }

        private string GetDescription(Type type)
        {
            // Try to get description from attribute
            var attr = type.GetCustomAttribute<DescriptionAttribute>();
            if (attr != null)
            {
                return attr.Description;
            }

            // Use type-specific descriptions as fallback
            return GetDefaultDescription(type.Name);
        }

        private string GetDefaultDescription(string typeName)
        {
            switch (typeName)
            {
                case "MenuItemCommand":
                    return "Execute Unity Editor menu item by path";
                case "GetLogsCommand":
                    return "Get console logs from Unity Editor";
                case "AssetDatabaseCommand":
                    return "Asset database operations: search, find, get_path, load, import, refresh, and fallback read_text";
                case "SceneCommand":
                    return "Scene operations: load, save, get hierarchy";
                case "EditorCommand":
                    return "Editor operations: undo, redo, compile, play mode (with domain reload option)";
                case "SelectionCommand":
                    return "Selection operations: get, set, clear";
                case "GameObjectCommand":
                    return "GameObject operations: create, destroy, find, rename";
                case "TransformCommand":
                    return "Transform operations: position, rotation, scale, parent";
                case "InspectorCommand":
                    return "Inspector operations: get/set SerializedProperty values on scene objects, prefab assets, and assets";
                case "PrefabCommand":
                    return "Prefab operations: instantiate, save, unpack, apply";
                case "BatchCommand":
                    return "Execute multiple commands in a single request";
                case "HelpCommand":
                    return "Returns help information about available commands";
                case "CompileCommand":
                    return "Compilation operations: start, status, dotnet build";
                case "ScreenshotCommand":
                    return "Screenshot and GIF recording operations";
                case "GameViewCommand":
                    return "Game view resolution management (get, set, list)";
                default:
                    return "No description available";
            }
        }

        private object GetExamples(string commandType)
        {
            switch (commandType)
            {
                case "menu_item":
                    return new
                    {
                        example = new { type = "menu_item", @params = new { menuPath = "File/Save Project" } }
                    };

                case "asset":
                    return new
                    {
                        format_note = "For asset search/find, format=full (default) returns asset objects, while format=paths returns asset path strings in data.assets.",
                        search = new { type = "asset", @params = new { action = "search", mode = "script", keyword = "Player", format = "paths", maxResults = 20 } },
                        find = new { type = "asset", @params = new { action = "find", filter = "t:Prefab", format = "paths", maxResults = 10 } },
                        get_path = new { type = "asset", @params = new { action = "get_path", guid = "abc123..." } },
                        load = new { type = "asset", @params = new { action = "load", assetPath = "Assets/Prefabs/Player.prefab" } },
                        read_text_fallback = new { type = "asset", @params = new { action = "read_text", assetPath = "Assets/Scripts/Player.cs", startLine = 1, maxLines = 120, maxChars = 12000 } },
                        refresh = new { type = "asset", @params = new { action = "refresh" } }
                    };

                case "scene":
                    return new
                    {
                        get_hierarchy = new { type = "scene", @params = new { action = "get_hierarchy", depth = 3 } },
                        save = new { type = "scene", @params = new { action = "save" } }
                    };

                case "gameobject":
                    return new
                    {
                        create = new { type = "gameobject", @params = new { action = "create", name = "NewObject", primitiveType = "Cube" } },
                        find = new { type = "gameobject", @params = new { action = "find", name = "Main Camera" } }
                    };

                case "inspector":
                    return new
                    {
                        powershell_note = "For set_properties in PowerShell, build a values JSON variable, escape embedded quotes for native EXE argument passing, and pass --values $values instead of inline --json.",
                        get_components = new { type = "inspector", @params = new { action = "get_components", path = "Main Camera" } },
                        get_prefab_components = new { type = "inspector", @params = new { action = "get_components", assetPath = "Assets/UI/LoginPanel.prefab", objectPath = "Root/Button" } },
                        find_rect_transform_property = new { type = "inspector", @params = new { action = "find_property", assetPath = "Assets/UI/LoginPanel.prefab", objectPath = "Root/Button", componentName = "RectTransform", keyword = "AnchoredPosition" } },
                        set_prefab_rect_transform_x = new { type = "inspector", @params = new { action = "set_property", assetPath = "Assets/UI/LoginPanel.prefab", objectPath = "Root/Button", componentName = "RectTransform", propertyName = "m_AnchoredPosition.x", value = 100 } },
                        set_multiple_prefab_values = new
                        {
                            type = "inspector",
                            @params = new
                            {
                                action = "set_properties",
                                assetPath = "Assets/UI/LoginPanel.prefab",
                                objectPath = "Root/Button",
                                componentName = "RectTransform",
                                values = new Dictionary<string, object>
                                {
                                    { "m_AnchoredPosition.x", 100 },
                                    { "m_AnchoredPosition.y", -40 },
                                    { "m_LocalPosition.z", 0 }
                                }
                            }
                        },
                        add_component = new { type = "inspector", @params = new { action = "add_component", path = "Main Camera", typeName = "AudioListener" } }
                    };

                case "prefab":
                    return new
                    {
                        instantiate = new { type = "prefab", @params = new { action = "instantiate", prefabPath = "Assets/Prefabs/MyPrefab.prefab" } },
                        get_info = new { type = "prefab", @params = new { action = "get_info", prefabPath = "Assets/Prefabs/MyPrefab.prefab" } },
                        get_hierarchy = new { type = "prefab", @params = new { action = "get_hierarchy", prefabPath = "Assets/Prefabs/MyPrefab.prefab", depth = 5 } }
                    };

                case "batch":
                    return new
                    {
                        from_file = new { type = "batch", @params = new { action = "from_file", file = "AIBridgeCache/scripts/setup.txt" } },
                        from_text_runtime = new { type = "batch", @params = new { action = "from_text", scriptPath = "AIBridgeCache/scripts/temp_script.txt", keepFile = false } },
                        cli_note = "CLI batch from_text writes --text to a temporary scriptPath before sending this Unity command."
                    };

                default:
                    return new { note = "No examples available for this command" };
            }
        }
    }
}
