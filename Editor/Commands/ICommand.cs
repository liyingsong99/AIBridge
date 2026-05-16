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
        /// Markdown documentation snippet for this command, used to generate Skill reference files.
        /// Return a self-contained markdown section (starting with a ### heading).
        /// Return null or empty string to exclude this command from Skill reference generation.
        /// Implement ICommandSkillDocProvider when the command should write to a non-default reference file.
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

    /// <summary>
    /// 命令文档生成元数据，用于把不同职责的命令说明写入对应 Skill 的 reference 文件。
    /// </summary>
    public sealed class CommandSkillDoc
    {
        public const string DefaultTargetSkillName = "aibridge";
        public const string DefaultReferenceFileName = "command-reference.md";

        public CommandSkillDoc(string content)
        {
            Content = content;
            TargetSkillName = DefaultTargetSkillName;
            TargetReferenceFileName = DefaultReferenceFileName;
        }

        public string Content { get; set; }
        public string TargetSkillName { get; set; }
        public string TargetReferenceFileName { get; set; }
        public int Order { get; set; }
    }

    /// <summary>
    /// 可选接口：命令实现后可覆盖自动文档的目标 Skill 和 reference 文件名。
    /// 未实现该接口的命令会继续使用 ICommand.SkillDescription 旧逻辑。
    /// </summary>
    public interface ICommandSkillDocProvider
    {
        CommandSkillDoc SkillDoc { get; }
    }
}
