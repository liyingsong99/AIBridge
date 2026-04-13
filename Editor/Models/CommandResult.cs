using System;

namespace AIBridge.Editor
{
    /// <summary>
    /// Command execution result returned to AI Code assistant
    /// </summary>
    [Serializable]
    public class CommandResult
    {
        /// <summary>
        /// Command ID (matches request ID)
        /// </summary>
        public string id;

        /// <summary>
        /// Whether command executed successfully
        /// </summary>
        public bool success;

        /// <summary>
        /// Result data (command-specific)
        /// </summary>
        public object data;

        /// <summary>
        /// Error message if failed
        /// </summary>
        public string error;

        /// <summary>
        /// Execution time in milliseconds
        /// </summary>
        public long? executionTime;

        /// <summary>
        /// Create a successful result
        /// </summary>
        public static CommandResult Success(string id, object data = null)
        {
            return new CommandResult
            {
                id = id,
                success = true,
                data = data,
                error = null
            };
        }

        /// <summary>
        /// Create a failed result
        /// </summary>
        public static CommandResult Failure(string id, string error)
        {
            return new CommandResult
            {
                id = id,
                success = false,
                data = null,
                error = error
            };
        }

        /// <summary>
        /// Create a failed result from exception
        /// </summary>
        public static CommandResult FromException(string id, Exception ex)
        {
            return new CommandResult
            {
                id = id,
                success = false,
                data = null,
                error = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
            };
        }
    }
}
