using System;
using System.Collections.Generic;

namespace AIBridgeCLI.Core
{
    public enum FlowStatementType
    {
        Step,
        Wait,
        Assert,
        Verify,
        End
    }

    public enum FlowExecutionTarget
    {
        Unity,
        Job
    }

    public enum FlowVerifyType
    {
        FileExists,
        DirectoryExists
    }

    public class FlowDefinition
    {
        public string Name { get; set; }
        public string SourceFilePath { get; set; }
        public string WorkingDirectory { get; set; }
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<FlowStatement> Statements { get; set; } = new List<FlowStatement>();
    }

    public class FlowStatement
    {
        public FlowStatementType Type { get; set; }
        public FlowExecutionTarget? Target { get; set; }
        public int LineNumber { get; set; }
        public string RawText { get; set; }
        public string StepId { get; set; }
        public string CommandLine { get; set; }
        public string JobType { get; set; }
        public string AssertionExpression { get; set; }
        public FlowVerifyType? VerifyType { get; set; }
        public string VerifyTarget { get; set; }
        public string UntilExpression { get; set; }
        public string FailIfExpression { get; set; }
        public int PollIntervalMs { get; set; } = 500;
        public int TimeoutMs { get; set; } = 30000;
    }

    public class FlowRunState
    {
        public string RunId { get; set; }
        public string FlowName { get; set; }
        public string SourceFilePath { get; set; }
        public string Status { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public int CurrentStatementIndex { get; set; }
        public string CurrentStepId { get; set; }
        public string Error { get; set; }
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public FlowLastResult LastResult { get; set; }
        public List<FlowStepState> Steps { get; set; } = new List<FlowStepState>();
    }

    public class FlowStepState
    {
        public string StepId { get; set; }
        public string Type { get; set; }
        public string JobType { get; set; }
        public string JobId { get; set; }
        public int LineNumber { get; set; }
        public string Status { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public string CommandId { get; set; }
        public string Message { get; set; }
        public CommandResult Result { get; set; }
    }

    public class FlowLastResult
    {
        public bool Success { get; set; }
        public string StepId { get; set; }
        public string Error { get; set; }
        public object Data { get; set; }
        public string CommandId { get; set; }
    }

    public class FlowEvent
    {
        public string TimestampUtc { get; set; }
        public string RunId { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        public string StepId { get; set; }
    }
}
