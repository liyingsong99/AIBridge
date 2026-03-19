using System;
using System.Collections.Generic;

namespace AIBridge.Runtime
{
    /// <summary>
    /// Runtime command data structure for communication between Editor and Runtime.
    /// </summary>
    [Serializable]
    public class AIBridgeRuntimeCommand
    {
        /// <summary>
        /// Unique command ID for tracking
        /// </summary>
        public string Id;

        /// <summary>
        /// Command action type (e.g., trigger_event, get_panels, custom actions)
        /// </summary>
        public string Action;

        /// <summary>
        /// Panel name for targeting UI elements (optional, for UI frameworks)
        /// </summary>
        public string PanelName;

        /// <summary>
        /// Event name to trigger (optional, for UI frameworks)
        /// </summary>
        public string EventName;

        /// <summary>
        /// View name (optional, for targeting View events)
        /// </summary>
        public string ViewName;

        /// <summary>
        /// Generic parameters dictionary for extensibility
        /// </summary>
        public Dictionary<string, object> Params;

        /// <summary>
        /// Timestamp when command was created
        /// </summary>
        public long Timestamp;

        public static AIBridgeRuntimeCommand FromDictionary(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            var command = new AIBridgeRuntimeCommand
            {
                Id = GetString(data, "Id") ?? GetString(data, "id"),
                Action = GetString(data, "Action") ?? GetString(data, "action"),
                PanelName = GetString(data, "PanelName") ?? GetString(data, "panelName"),
                EventName = GetString(data, "EventName") ?? GetString(data, "eventName"),
                ViewName = GetString(data, "ViewName") ?? GetString(data, "viewName"),
                Params = GetDictionary(data, "Params") ?? GetDictionary(data, "params"),
                Timestamp = GetLong(data, "Timestamp") ?? GetLong(data, "timestamp") ?? 0L
            };

            return command;
        }

        /// <summary>
        /// Get parameter value with type conversion
        /// </summary>
        public T GetParam<T>(string key, T defaultValue = default)
        {
            if (Params == null || !Params.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            try
            {
                if (value is T typedValue)
                {
                    return typedValue;
                }

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value))
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        private static long? GetLong(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is double doubleValue)
            {
                return (long)doubleValue;
            }

            if (long.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }

    /// <summary>
    /// Runtime command execution result.
    /// </summary>
    [Serializable]
    public class AIBridgeRuntimeCommandResult
    {
        /// <summary>
        /// Corresponding command ID
        /// </summary>
        public string CommandId;

        /// <summary>
        /// Whether command execution was successful
        /// </summary>
        public bool Success;

        /// <summary>
        /// Error message if failed
        /// </summary>
        public string Error;

        /// <summary>
        /// Result data (JSON serializable)
        /// </summary>
        public object Data;

        /// <summary>
        /// Whether command has completed execution
        /// </summary>
        public bool Completed;

        /// <summary>
        /// Execution timestamp
        /// </summary>
        public long Timestamp;

        public AIBridgeRuntimeCommandResult()
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public AIBridgeRuntimeCommandResult(string commandId) : this()
        {
            CommandId = commandId;
        }

        public static AIBridgeRuntimeCommandResult FromSuccess(string commandId, object data = null)
        {
            return new AIBridgeRuntimeCommandResult(commandId)
            {
                Success = true,
                Completed = true,
                Data = data
            };
        }

        public static AIBridgeRuntimeCommandResult FromFailure(string commandId, string error)
        {
            return new AIBridgeRuntimeCommandResult(commandId)
            {
                Success = false,
                Completed = true,
                Error = error
            };
        }
    }

    /// <summary>
    /// Panel information for query results (compatible with UI framework integrations).
    /// </summary>
    [Serializable]
    public class AIBridgePanelInfo
    {
        public string PanelName;
        public string PkgName;
        public string ResName;
        public string Layer;
        public bool IsActive;
        public string[] Events;
    }
}
