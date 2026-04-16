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
                    return "Inspector operations: get/set properties, add/remove components";
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
                        get_components = new { type = "inspector", @params = new { action = "get_components", path = "Main Camera" } },
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
                        example = new
                        {
                            type = "batch",
                            @params = new
                            {
                                commands = new object[]
                                {
                                    new { type = "gameobject", @params = new { action = "create", name = "Cube1", primitiveType = "Cube" } },
                                    new { type = "gameobject", @params = new { action = "create", name = "Cube2", primitiveType = "Cube" } }
                                }
                            }
                        }
                    };

                default:
                    return new { note = "No examples available for this command" };
            }
        }
    }
}
