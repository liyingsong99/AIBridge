using System;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class PipelineTestExecutionGateTests
    {
        [Test]
        public void ProbeRequiresExactBooleanStaticSignatureAndFailsClosed()
        {
            Assert.That(PipelineTestExecutionGate.ProbeNative(typeof(Idle)), Is.Null);
            Assert.That(PipelineTestExecutionGate.ProbeNative(typeof(Active)), Is.Not.Null);
            foreach (var type in new[] { typeof(Missing), typeof(WrongReturn), typeof(WrongArgs), typeof(Throws), typeof(Instance) })
                Assert.That(PipelineTestExecutionGate.ProbeNative(type), Is.Not.Null, type.Name);
            Assert.That(PipelineTestExecutionGate.ProbeNative(null), Is.Not.Null);
        }
        private static class Idle { private static bool IsRunActive() { return false; } }
        private static class Active { private static bool IsRunActive() { return true; } }
        private static class Missing { }
        private static class WrongReturn { private static int IsRunActive() { return 0; } }
        private static class WrongArgs { private static bool IsRunActive(string guid) { return false; } }
        private static class Throws { private static bool IsRunActive() { throw new InvalidOperationException(); } }
        private sealed class Instance { private bool IsRunActive() { return false; } }
    }
}
