using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    /// <summary>
    /// Input command builder: simulate runtime pointer input in Play Mode.
    /// </summary>
    public class InputCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "input";
        public override string Description => "Runtime input simulation for Play Mode UI automation";

        public override string[] Actions => new[]
        {
            "click", "click_at", "drag", "long_press"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["click"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject in hierarchy", false),
                new ParameterInfo("instanceId", "Instance ID of the GameObject", false)
            },
            ["click_at"] = new List<ParameterInfo>
            {
                new ParameterInfo("x", "Screen X coordinate (bottom-left origin)", true),
                new ParameterInfo("y", "Screen Y coordinate (bottom-left origin)", true)
            },
            ["drag"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Source GameObject path", false),
                new ParameterInfo("instanceId", "Source GameObject instance ID", false),
                new ParameterInfo("toPath", "Destination GameObject path", false),
                new ParameterInfo("toInstanceId", "Destination GameObject instance ID", false),
                new ParameterInfo("toX", "Destination screen X coordinate", false),
                new ParameterInfo("toY", "Destination screen Y coordinate", false),
                new ParameterInfo("frames", "Drag duration in editor update frames (3-60)", false, "12")
            },
            ["long_press"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject in hierarchy", false),
                new ParameterInfo("instanceId", "Instance ID of the GameObject", false),
                new ParameterInfo("duration-ms", "Hold duration in milliseconds", false, "1000")
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            if (string.IsNullOrEmpty(action))
            {
                action = "click";
            }

            var request = base.Build(action, options);

            ApplyAliases(request.@params);
            ValidateInputRules(action, request.@params);
            return request;
        }

        protected override void ValidateParameters(string action, Dictionary<string, object> @params)
        {
            ApplyAliases(@params);
            base.ValidateParameters(action, @params);
            ValidateInputRules(string.IsNullOrEmpty(action) ? "click" : action, @params);
        }

        private static void ValidateInputRules(string action, Dictionary<string, object> @params)
        {
            if (string.Equals(action, "click", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "long_press", StringComparison.OrdinalIgnoreCase))
            {
                RequireTarget(@params, "path", "instanceId");
            }
            else if (string.Equals(action, "drag", StringComparison.OrdinalIgnoreCase))
            {
                RequireTarget(@params, "path", "instanceId");
                var hasDestinationObject = HasValue(@params, "toPath") || HasValue(@params, "toInstanceId");
                var hasDestinationCoordinates = HasValue(@params, "toX") && HasValue(@params, "toY");
                if (!hasDestinationObject && !hasDestinationCoordinates)
                {
                    throw new ArgumentException("Missing drag target. Provide --toPath/--toInstanceId or --toX --toY.");
                }

                if (HasValue(@params, "frames"))
                {
                    var frames = Convert.ToInt32(@params["frames"]);
                    if (frames < 3 || frames > 60)
                    {
                        throw new ArgumentException("--frames must be between 3 and 60.");
                    }
                }
            }
        }

        private static void RequireTarget(Dictionary<string, object> @params, string pathKey, string instanceIdKey)
        {
            if (!HasValue(@params, pathKey) && !HasValue(@params, instanceIdKey))
            {
                throw new ArgumentException($"Missing target. Provide --{pathKey} or --{instanceIdKey}.");
            }
        }

        private static bool HasValue(Dictionary<string, object> @params, string key)
        {
            if (@params == null || !@params.TryGetValue(key, out var value) || value == null)
            {
                return false;
            }

            var text = value as string;
            return text == null || !string.IsNullOrWhiteSpace(text);
        }

        private static void ApplyAliases(Dictionary<string, object> @params)
        {
            RenameParam(@params, "duration-ms", "durationMs");
            RenameParam(@params, "to-path", "toPath");
            RenameParam(@params, "to-instance-id", "toInstanceId");
            RenameParam(@params, "to-x", "toX");
            RenameParam(@params, "to-y", "toY");
        }

        private static void RenameParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
            @params.Remove(sourceKey);
        }
    }
}
