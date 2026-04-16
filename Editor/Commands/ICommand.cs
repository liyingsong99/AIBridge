namespace AIBridge.Editor
{
    /// <summary>
    /// Command handler interface.
    /// Implement this interface to add custom commands to AI Bridge.
    /// Commands are auto-discovered via reflection.
    /// </summary>
    public interface ICommand
    {
        /// <summary>
        /// Command type identifier (e.g., "execute_code", "menu_item").
        /// This must match the "type" field in the command request JSON.
        /// </summary>
        string Type { get; }

        /// <summary>
        /// Whether to call AssetDatabase.Refresh() after command execution.
        /// Set to true for commands that modify assets (prefabs, scenes, etc.).
        /// </summary>
        bool RequiresRefresh { get; }

        /// <summary>
        /// Markdown documentation snippet for this command, used to generate SKILL.md.
        /// Return a self-contained markdown section (starting with a ### heading).
        /// Return null or empty string to exclude this command from SKILL.md generation.
        /// </summary>
        string SkillDescription { get; }

        /// <summary>
        /// Execute the command and return result.
        /// This method is called on the main thread.
        /// </summary>
        /// <param name="request">The command request containing parameters</param>
        /// <returns>Command execution result</returns>
        CommandResult Execute(CommandRequest request);
    }
}
