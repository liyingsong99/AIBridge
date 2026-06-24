using System;
using System.Collections.Generic;
using System.Text;
using AIBridgeCLI.Core;

namespace AIBridgeCLI.Commands
{
    public class ExecCommandBuilder : BaseCommandBuilder
    {
        public override string Type => "exec";
        public override string Description => "Execute external processes with structured argv/stdin JSON";

        public override string[] Actions => new[] { "run", "batch" };

        protected override Dictionary<string, List<ParameterInfo>> ActionParameters => new Dictionary<string, List<ParameterInfo>>
        {
            ["run"] = new List<ParameterInfo>
            {
                new ParameterInfo("request-file", "Path to an exec JSON request file", false),
                new ParameterInfo("stdin", "Read exec JSON request from stdin", false, "false")
            },
            ["batch"] = new List<ParameterInfo>
            {
                new ParameterInfo("request-file", "Path to an exec batch JSON request file", false),
                new ParameterInfo("stdin", "Read exec batch JSON request from stdin", false, "false")
            }
        };

        public override CommandRequest Build(string action, Dictionary<string, string> options)
        {
            throw new ArgumentException("exec is CLI-only. Use AIBridgeCLI exec run --stdin or --request-file.");
        }

        public override string GetHelp(string action = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("exec: Execute external processes with structured argv/stdin JSON");
            sb.AppendLine();
            sb.AppendLine("This command does not invoke PowerShell, cmd, or bash. It starts one executable");
            sb.AppendLine("with an argv array and optional stdin, then returns normalized JSON.");
            sb.AppendLine();
            sb.AppendLine("Agent note: --stdin means the exec request JSON is read from standard input.");
            sb.AppendLine("Do not append a raw shell command after --stdin; pipe JSON into the CLI instead.");
            sb.AppendLine("If values contain quotes, backslashes, or regex, build a PowerShell object and use ConvertTo-Json, or use --request-file.");
            sb.AppendLine();
            sb.AppendLine("Usage:");
            sb.AppendLine("  AIBridgeCLI exec run --stdin");
            sb.AppendLine("  AIBridgeCLI exec run --request-file .aibridge/exec/search.json");
            sb.AppendLine("  AIBridgeCLI exec batch --stdin");
            sb.AppendLine();
            sb.AppendLine("Single request schema:");
            sb.AppendLine("  command: string      Executable name/path only");
            sb.AppendLine("  args: string[]       Flags and positional argv");
            sb.AppendLine("  cwd: string");
            sb.AppendLine("  env: object");
            sb.AppendLine("  stdin: string");
            sb.AppendLine("  stdinFile: string");
            sb.AppendLine("  timeoutMs: number");
            sb.AppendLine("  allowedExitCodes: number[]");
            sb.AppendLine("  maxOutputChars: number");
            sb.AppendLine();
            sb.AppendLine("rg/search convenience fields:");
            sb.AppendLine("  queries: string[]      Expands to repeated -e <query>");
            sb.AppendLine("  globs: string[]        Expands to repeated --glob <glob>");
            sb.AppendLine("  types: string[]        Expands to repeated -t <type>");
            sb.AppendLine("  paths: string[]        Appended as positional search paths");
            sb.AppendLine("  patternFile: string    Expands to -f <file>");
            sb.AppendLine();
            sb.AppendLine("Batch request schema:");
            sb.AppendLine("  jobs: request[]");
            sb.AppendLine("  cwd: string");
            sb.AppendLine("  env: object");
            sb.AppendLine("  timeoutMs: number");
            sb.AppendLine("  continueOnError: bool");
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  PowerShell: $request | & ./.aibridge/cli/AIBridgeCLI.exe exec run --stdin");
            sb.AppendLine("  { \"command\": \"rg\", \"queries\": [\"ProcessStartInfo\", \"ArgumentList\"], \"globs\": [\"*.cs\"], \"paths\": [\"Packages\"] }");
            sb.AppendLine("  { \"jobs\": [{ \"command\": \"rg\", \"args\": [\"-n\"], \"queries\": [\"TODO\"], \"paths\": [\"Assets\"] }] }");
            return sb.ToString();
        }
    }
}
