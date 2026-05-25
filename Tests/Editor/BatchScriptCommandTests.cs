using System.IO;
using AIBridge.Editor.ScriptExecution;
using AIBridge.Editor.ScriptExecution.Commands;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class BatchScriptCommandTests
    {
        [Test]
        public void ParserRecognizesExtendedBatchCommands()
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), "aibridge_batch_" + Path.GetRandomFileName() + ".txt");
            File.WriteAllText(
                scriptPath,
                "wait_compile 120000\nwait_playmode playing 30000\nassert_log_empty Error\nassert_object \"Canvas/Button\"\nset_var name value\nprint_var name\ndialog click ok | yes | Save\n");

            try
            {
                var commands = ScriptParser.Parse(scriptPath);

                Assert.That(commands[0], Is.TypeOf<WaitCompileCommand>());
                Assert.That(commands[1], Is.TypeOf<WaitPlayModeCommand>());
                Assert.That(commands[2], Is.TypeOf<AssertLogEmptyCommand>());
                Assert.That(commands[3], Is.TypeOf<AssertObjectCommand>());
                Assert.That(commands[4], Is.TypeOf<SetVarCommand>());
                Assert.That(commands[5], Is.TypeOf<PrintVarCommand>());
                Assert.That(commands[6], Is.TypeOf<DialogClickCommand>());
            }
            finally
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
        }

        [Test]
        public void DialogClickCommand_ParsesAlternativeTargets()
        {
            DialogClickCommand command;
            string error;

            var parsed = DialogClickCommand.TryParse("dialog click ok | yes | \"Don't Save\"", out command, out error);

            Assert.That(parsed, Is.True, error);
            Assert.That(command.Targets, Is.EquivalentTo(new[] { "ok", "yes", "Don't Save" }));
        }

        [Test]
        public void VariablesCanBePassedBetweenScriptCommands()
        {
            var context = new ScriptExecutionContext();

            var setResult = new SetVarCommand("token", "ready").Execute(context);
            var printResult = new PrintVarCommand("token").Execute(context);

            Assert.That(setResult.Success, Is.True);
            Assert.That(printResult.Success, Is.True);
            Assert.That(printResult.Message, Is.EqualTo("ready"));
        }
    }
}
