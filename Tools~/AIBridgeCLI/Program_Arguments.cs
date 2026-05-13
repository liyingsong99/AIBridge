using System;
using System.Collections.Generic;
using System.Linq;
using AIBridgeCLI.Commands;
using AIBridgeCLI.Core;
using Newtonsoft.Json;

namespace AIBridgeCLI
{
    partial class Program
    {
        static ParsedArgs ParseArguments(string[] args)
        {
            var result = new ParsedArgs();

            var i = 0;
            while (i < args.Length)
            {
                var arg = args[i];

                if (arg.StartsWith("--"))
                {
                    var key = arg.Substring(2);

                    // Check for boolean flags
                    if (key == "help" || key == "raw" || key == "pretty" || key == "quiet" || key == "no-wait" || key == "stdin" || key == "show-warnings")
                    {
                        result.Options[key] = "true";
                        i++;
                        continue;
                    }

                    // Key-value pair
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        result.Options[key] = args[i + 1];
                        i += 2;
                    }
                    else
                    {
                        result.Options[key] = "true";
                        i++;
                    }
                }
                else if (arg.StartsWith("-"))
                {
                    // Short form not supported, treat as error
                    throw new ArgumentException($"Short form arguments not supported: {arg}");
                }
                else
                {
                    // Positional argument
                    if (result.CommandType == null)
                    {
                        result.CommandType = arg;
                    }
                    else if (result.Action == null)
                    {
                        // For multi command, treat all extra args as commands
                        if (result.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
                        {
                            result.ExtraArgs.Add(arg);
                        }
                        else
                        {
                            result.Action = arg;
                        }
                    }
                    else
                    {
                        // For multi command, collect extra args
                        if (result.CommandType.Equals("multi", StringComparison.OrdinalIgnoreCase))
                        {
                            result.ExtraArgs.Add(arg);
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected argument: {arg}");
                        }
                    }
                    i++;
                }
            }

            return result;
        }

        class ParsedArgs
        {
            public string CommandType { get; set; }
            public string Action { get; set; }
            public List<string> ExtraArgs { get; } = new List<string>();
            public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool GetBool(string key)
            {
                return Options.TryGetValue(key, out var value) &&
                       (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
            }

            public int GetInt(string key, int defaultValue)
            {
                if (Options.TryGetValue(key, out var value) && int.TryParse(value, out var intValue))
                {
                    return intValue;
                }
                return defaultValue;
            }
        }

        /// <summary>
        /// Handle multi command - execute multiple commands in one call
        /// </summary>
    }
}
