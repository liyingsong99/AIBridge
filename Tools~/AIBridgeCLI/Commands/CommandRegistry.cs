using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Registry for all available commands
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommandBuilder> _commands = new Dictionary<string, ICommandBuilder>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        /// <summary>
        /// Initialize the registry with all known commands
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            Register(new EditorCommandBuilder());
            Register(new CompileCommandBuilder());
            Register(new GameObjectCommandBuilder());
            Register(new TransformCommandBuilder());
            Register(new InspectorCommandBuilder());
            Register(new SelectionCommandBuilder());
            Register(new SceneCommandBuilder());
            Register(new PrefabCommandBuilder());
            Register(new AssetCommandBuilder());
            Register(new MenuItemCommandBuilder());
            Register(new GetLogsCommandBuilder());
            Register(new BatchCommandBuilder());
            Register(new ScreenshotCommandBuilder());
            Register(new GameViewCommandBuilder());

            _initialized = true;
        }

        /// <summary>
        /// Register a command builder
        /// </summary>
        public static void Register(ICommandBuilder builder)
        {
            _commands[builder.Type] = builder;
        }

        /// <summary>
        /// Try to get a command builder by type
        /// </summary>
        public static bool TryGet(string type, out ICommandBuilder builder)
        {
            if (!_initialized) Initialize();
            return _commands.TryGetValue(type, out builder);
        }

        /// <summary>
        /// Get all registered command types
        /// </summary>
        public static IEnumerable<string> GetTypes()
        {
            if (!_initialized) Initialize();
            return _commands.Keys;
        }

        /// <summary>
        /// Get all registered command builders
        /// </summary>
        public static IEnumerable<ICommandBuilder> GetAll()
        {
            if (!_initialized) Initialize();
            return _commands.Values;
        }

        /// <summary>
        /// Generate global help text
        /// </summary>
        public static string GetGlobalHelp()
        {
            if (!_initialized) Initialize();

            var sb = new StringBuilder();
            sb.AppendLine("AIBridgeCLI - Command line interface for AI Bridge");
            sb.AppendLine();
            sb.AppendLine("Usage: AIBridgeCLI <command> <action> [options]");
            sb.AppendLine();
            sb.AppendLine("Commands:");

            var maxLen = _commands.Keys.Max(k => k.Length);
            maxLen = Math.Max(maxLen, "focus".Length); // Include focus command

            // Add focus command (CLI-only)
            sb.AppendLine($"  {"focus".PadRight(maxLen + 2)} Bring Unity Editor window to foreground (CLI-only, no Unity needed)");

            foreach (var cmd in _commands.Values.OrderBy(c => c.Type))
            {
                sb.AppendLine($"  {cmd.Type.PadRight(maxLen + 2)} {cmd.Description}");
            }

            sb.AppendLine();
            sb.AppendLine("Global Options:");
            sb.AppendLine("  --timeout <ms>     Timeout in milliseconds (default: 5000)");
            sb.AppendLine("  --no-wait          Don't wait for result, return command ID immediately");
            sb.AppendLine("  --raw              Output compact raw JSON (default)");
            sb.AppendLine("  --pretty           Output human-readable formatted text");
            sb.AppendLine("  --quiet            Quiet mode, minimal output");
            sb.AppendLine("  --json <json>      Pass complex parameters as JSON string");
            sb.AppendLine("  --stdin            Read parameters from stdin (JSON format)");
            sb.AppendLine("  --help             Show help");
            sb.AppendLine();
            sb.AppendLine("Default output is compact raw JSON. Use --pretty for human-readable output.");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  AIBridgeCLI editor log --message \"Hello World\"");
            sb.AppendLine("  AIBridgeCLI gameobject create --name \"MyCube\" --primitiveType Cube");
            sb.AppendLine("  AIBridgeCLI transform set_position --path \"Player\" --x 1 --y 2 --z 3");
            sb.AppendLine("  AIBridgeCLI --help");
            sb.AppendLine("  AIBridgeCLI editor --help");
            sb.AppendLine("  AIBridgeCLI editor log --help");

            return sb.ToString();
        }
    }
}
