using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class AIBridgeCodeIndexSnapshotUtilityTests
    {
        [Test]
        public void ComputeAssemblyHash_AllowsNullAsmdefPath()
        {
            var projectRoot = System.IO.Path.GetTempPath();
            var record = new AIBridgeCodeIndexSnapshotUtility.AssemblyRecord
            {
                AssemblyName = "Foo",
                AssemblyId = "Foo",
                OutputPath = "Library/ScriptAssemblies/Foo.dll",
                AsmdefPath = null,
                LanguageVersion = "9.0",
                AllowUnsafe = false,
                DefinesHash = "defines",
                SourcesHash = "sources",
                ReferencesHash = "references",
                CompilerOptionsHash = "compiler-options"
            };

            var method = typeof(AIBridgeCodeIndexSnapshotUtility).GetMethod(
                "ComputeAssemblyHash",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            var result = method.Invoke(null, new object[]
            {
                record,
                projectRoot,
                "6000.0.51f1",
                "StandaloneWindows64",
                new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            }) as string;

            Assert.IsFalse(string.IsNullOrWhiteSpace(result));
        }
    }
}
