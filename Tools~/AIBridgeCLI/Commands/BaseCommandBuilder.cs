using System;
using System.Collections.Generic;
using System.Text;
using AIBridge.Runtime.Internal;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

// ReSharper disable InconsistentNaming

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Base class for command builders with common functionality
    /// </summary>
    public abstract class BaseCommandBuilder : ICommandBuilder
    {
        public abstract string Type { get; }
        public abstract string[] Actions { get; }
        public abstract string Description { get; }

        /// <summary>
        /// Get parameter definitions for each action
        /// </summary>
        protected abstract Dictionary<string, List<ParameterInfo>> ActionParameters { get; }

        public virtual CommandRequest Build(string action, Dictionary<string, string> options)
        {
            var request = new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = Type,
                @params = new Dictionary<string, object>()
            };

            // Add action if specified
            if (!string.IsNullOrEmpty(action))
            {
                request.@params["action"] = action;
            }

            // Handle --json parameter for complex objects
            if (options.TryGetValue("json", out var jsonValue))
            {
                try
                {
                    var jsonParams = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonValue);
                    foreach (var kvp in jsonParams)
                    {
                        request.@params[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Invalid JSON in --json parameter: {ex.Message}");
                }
                return request;
            }

            // Global options to exclude from params
            var globalOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "json", "stdin", "timeout", "transport-timeout", "poll-interval",
                "no-wait", "raw", "pretty", "quiet", "help", "on-dialog", "workflow-run"
            };

            // Process other options
            foreach (var kvp in options)
            {
                if (globalOptions.Contains(kvp.Key)) continue;

                var value = ParseValue(kvp.Value);
                request.@params[kvp.Key] = value;
            }

            // Validate required parameters
            ValidateParameters(action, request.@params);

            return request;
        }

        /// <summary>
        /// Parse a string value to appropriate type
        /// </summary>
        protected object ParseValue(string value)
        {
            if (value == null) return null;

            // Boolean
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

            // Integer
            if (long.TryParse(value, out var longValue)) return longValue;

            // Float
            if (double.TryParse(value, out var doubleValue)) return doubleValue;

            // String
            return value;
        }

        /// <summary>
        /// Validate that required parameters are present
        /// </summary>
        protected virtual void ValidateParameters(string action, Dictionary<string, object> @params)
        {
            if (ActionParameters == null) return;

            var key = action ?? "";
            if (!ActionParameters.TryGetValue(key, out var parameters)) return;

            foreach (var param in parameters)
            {
                if (param.Required && !@params.ContainsKey(param.Name))
                {
                    throw new ArgumentException($"Missing required parameter: --{param.Name}");
                }
            }
        }

        public virtual string GetHelp(string action = null)
        {
            var sb = new StringBuilder();

            if (string.IsNullOrEmpty(action))
            {
                // General help
                sb.AppendLine($"{Type}: {Description}");
                sb.AppendLine();
                sb.AppendLine("Actions:");
                foreach (var act in Actions)
                {
                    sb.AppendLine($"  {act}");
                }
                sb.AppendLine();
                sb.AppendLine($"Usage: AIBridgeCLI {Type} <action> [options]");
                sb.AppendLine($"       AIBridgeCLI {Type} <action> --help");
            }
            else
            {
                // Action-specific help
                sb.AppendLine($"{Type} {action}");
                sb.AppendLine();

                if (ActionParameters != null && ActionParameters.TryGetValue(action, out var parameters))
                {
                    var unityVersion = AIBridgeObjectIdPresentation.ResolveUnityVersion(PathHelper.TryGetUnityProjectRoot());
                    var showedObjectId = false;
                    sb.AppendLine("Parameters:");
                    foreach (var param in parameters)
                    {
                        if (!AIBridgeObjectIdPresentation.ShouldShowInHelp(param.Name, unityVersion))
                        {
                            continue;
                        }

                        if (AIBridgeObjectIdPresentation.IsObjectIdParamName(param.Name))
                        {
                            showedObjectId = true;
                        }

                        var required = param.Required ? "(required)" : "(optional)";
                        var defaultVal = param.DefaultValue != null ? $" [default: {param.DefaultValue}]" : "";
                        var description = AIBridgeObjectIdPresentation.NormalizeHelpDescription(
                            param.Name,
                            param.Description,
                            unityVersion);
                        sb.AppendLine($"  --{param.Name,-20} {required} {description}{defaultVal}");
                    }

                    if (showedObjectId)
                    {
                        sb.AppendLine();
                        sb.AppendLine(AIBridgeObjectIdPresentation.BuildCompatibilityNote(unityVersion));
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"Usage: AIBridgeCLI {Type} {action} [options]");
            }

            return sb.ToString();
        }
    }
}
