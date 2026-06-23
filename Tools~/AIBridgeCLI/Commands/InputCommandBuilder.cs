using System;
using System.Collections.Generic;
using System.Globalization;
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
            "click", "click_at", "click_pct", "drag", "long_press"
        };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["click"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject in hierarchy", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false)
            },
            ["click_at"] = new List<ParameterInfo>
            {
                new ParameterInfo("x", "Screen X coordinate (bottom-left origin)", true),
                new ParameterInfo("y", "Screen Y coordinate (bottom-left origin)", true)
            },
            ["click_pct"] = new List<ParameterInfo>
            {
                new ParameterInfo("x", "Normalized Unity screen X coordinate, 0 to 1 (bottom-left origin)", true),
                new ParameterInfo("y", "Normalized Unity screen Y coordinate, 0 to 1 (bottom-left origin)", true)
            },
            ["drag"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Source GameObject path", false),
                new ParameterInfo("entityId", "Source GameObject entity ID on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Source entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("toPath", "Destination GameObject path", false),
                new ParameterInfo("toEntityId", "Destination GameObject entity ID on Unity 6000.4+", false),
                new ParameterInfo("toInstanceId", "Destination entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
                new ParameterInfo("toX", "Destination screen X coordinate", false),
                new ParameterInfo("toY", "Destination screen Y coordinate", false),
                new ParameterInfo("frames", "Drag duration in editor update frames (3-60)", false, "12")
            },
            ["long_press"] = new List<ParameterInfo>
            {
                new ParameterInfo("path", "Path to the GameObject in hierarchy", false),
                new ParameterInfo("entityId", "Entity ID of the GameObject on Unity 6000.4+", false),
                new ParameterInfo("instanceId", "Entity ID on Unity 6000.4+ or legacy instance ID on older Unity", false),
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
            else if (string.Equals(action, "click_pct", StringComparison.OrdinalIgnoreCase))
            {
                if (HasValue(@params, "origin"))
                {
                    throw new ArgumentException("click_pct uses Unity normalized screen coordinates with bottom-left origin. --origin is not supported.");
                }

                ValidateNormalizedCoordinate(@params, "x");
                ValidateNormalizedCoordinate(@params, "y");
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

        private static void ValidateNormalizedCoordinate(Dictionary<string, object> @params, string key)
        {
            if (!HasValue(@params, key))
            {
                return;
            }

            double value;
            try
            {
                value = Convert.ToDouble(@params[key], CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"--{key} must be a number between 0 and 1.", ex);
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d || value > 1d)
            {
                throw new ArgumentException($"--{key} must be between 0 and 1.");
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
            RenameParam(@params, "to-entity-id", "toEntityId");
            RenameParam(@params, "to-instance-id", "toInstanceId");
            RenameParam(@params, "to-x", "toX");
            RenameParam(@params, "to-y", "toY");
            CopyParam(@params, "entityId", "instanceId");
            CopyParam(@params, "toEntityId", "toInstanceId");
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

        private static void CopyParam(Dictionary<string, object> @params, string sourceKey, string targetKey)
        {
            if (@params == null || !@params.ContainsKey(sourceKey) || @params.ContainsKey(targetKey))
            {
                return;
            }

            @params[targetKey] = @params[sourceKey];
        }
    }
}
