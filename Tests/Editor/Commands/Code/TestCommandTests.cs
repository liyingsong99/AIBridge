using System.Collections.Generic;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class TestCommandTests
    {
        [Test]
        public void Execute_WhenEditorIsInPlayMode_ReturnsExplicitPlayModeFailure()
        {
            var result = new TestCommand(() => true).Execute(new CommandRequest
            {
                id = "test-playmode-state-failure",
                type = "test",
                @params = new Dictionary<string, object>
                {
                    { "action", "run" },
                    { "mode", "EditMode" }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("Play Mode"));
            Assert.That(result.error, Does.Contain("Edit Mode"));
        }
    }
}
