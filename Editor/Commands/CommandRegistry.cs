using System;
using System.Collections.Generic;
using System.Linq;

namespace AIBridge.Editor
{
    /// <summary>
    /// Registry for command handlers.
    /// Auto-discovers and registers all ICommand implementations via reflection.
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommand> Commands = new Dictionary<string, ICommand>();
        private static bool _initialized;

        /// <summary>
        /// Initialize the registry by discovering all command implementations
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            Commands.Clear();

            // Find all types implementing ICommand in Editor assemblies only
            // Avoid scanning Runtime assemblies to prevent TypeLoadException
            var commandTypes = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly =>
                {
                    var name = assembly.GetName().Name;
                    // Only scan Editor assemblies and this assembly
                    return name.Contains("Editor") ||
                           name.Contains("AIBridge") ||
                           name == "Assembly-CSharp-Editor";
                })
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Where(type =>
                {
                    try
                    {
                        return typeof(ICommand).IsAssignableFrom(type)
                               && !type.IsInterface
                               && !type.IsAbstract
                               && type.Namespace != null
                               && type.Namespace.Contains("Editor");
                    }
                    catch
                    {
                        // Ignore types that can't be loaded
                        return false;
                    }
                });

            foreach (var type in commandTypes)
            {
                try
                {
                    var command = (ICommand)Activator.CreateInstance(type);
                    if (!string.IsNullOrEmpty(command.Type))
                    {
                        if (Commands.ContainsKey(command.Type))
                        {
                            AIBridgeLogger.LogWarning($"Duplicate command type '{command.Type}', overwriting with {type.Name}");
                        }
                        Commands[command.Type] = command;
                        AIBridgeLogger.LogDebug($"Registered command: {command.Type} -> {type.Name}");
                    }
                }
                catch (Exception ex)
                {
                    AIBridgeLogger.LogError($"Failed to register command {type.Name}: {ex.Message}");
                }
            }

            _initialized = true;
            AIBridgeLogger.LogInfo($"CommandRegistry initialized with {Commands.Count} commands");
        }

        /// <summary>
        /// Register a command handler manually
        /// </summary>
        public static void Register(ICommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.Type))
            {
                return;
            }

            Commands[command.Type] = command;
            AIBridgeLogger.LogDebug($"Manually registered command: {command.Type}");
        }

        /// <summary>
        /// Try to get a command handler by type
        /// </summary>
        public static bool TryGetCommand(string type, out ICommand command)
        {
            if (!_initialized)
            {
                Initialize();
            }

            return Commands.TryGetValue(type, out command);
        }

        /// <summary>
        /// Get all registered command instances
        /// </summary>
        public static IEnumerable<ICommand> GetAllCommands()
        {
            if (!_initialized)
            {
                Initialize();
            }

            return Commands.Values;
        }

        /// <summary>
        /// Get all registered command types
        /// </summary>
        public static IEnumerable<string> GetRegisteredTypes()
        {
            if (!_initialized)
            {
                Initialize();
            }

            return Commands.Keys;
        }

        /// <summary>
        /// Reset the registry (useful for testing or reinitialization)
        /// </summary>
        public static void Reset()
        {
            Commands.Clear();
            _initialized = false;
        }
    }
}
