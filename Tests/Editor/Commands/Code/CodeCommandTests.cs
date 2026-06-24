using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using NUnit.Framework;
using UnityEngine;

namespace AIBridge.Editor.Tests
{
    public class CodeCommandTests
    {
        [Test]
        public void Execute_WhenDisabled_ReturnsSettingsFailure()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var previousEnabled = settings.EnableCodeExecution;
            var previousAccepted = settings.CodeExecutionRiskAccepted;
            settings.EnableCodeExecution = false;
            settings.CodeExecutionRiskAccepted = false;

            try
            {
                var result = new CodeCommand().Execute(new CommandRequest
                {
                    id = "code-disabled-test",
                    type = "code",
                    @params = new Dictionary<string, object>
                    {
                        { "action", "execute" },
                        { "code", "return 1;" },
                        { "allowExperimental", true }
                    }
                });

                Assert.That(result.success, Is.False);
                Assert.That(result.error, Does.Contain("disabled"));
                Assert.That(result.data, Is.Not.Null);
            }
            finally
            {
                settings.EnableCodeExecution = previousEnabled;
                settings.CodeExecutionRiskAccepted = previousAccepted;
            }
        }

        [Test]
        public void SkillDescriptionDocumentsSafetyGates()
        {
            var description = new CodeCommand().SkillDescription;

            Assert.That(description, Does.Contain("enabled by default"));
            Assert.That(description, Does.Not.Contain("--allow-experimental"));
            Assert.That(description, Does.Contain(".aibridge/code"));
            Assert.That(description, Does.Contain("Runtime/Public API"));
            Assert.That(description, Does.Contain("prefab patch --dryRun true"));
            Assert.That(description, Does.Contain("code status"));
            Assert.That(description, Does.Contain("code cancel"));
        }

        [Test]
        public void ProjectSettings_DefaultsToCodeExecutionEnabledAndCodeIndexPackageIgnores()
        {
            var settings = ScriptableObject.CreateInstance<AIBridgeProjectSettings>();

            try
            {
                Assert.That(settings.EnableCodeExecution, Is.True);
                Assert.That(settings.CodeExecutionRiskAccepted, Is.True);
                Assert.That(settings.CodeIndex.EnableCodeIndex, Is.False);
                Assert.That(settings.CodeIndex.IgnoredAssemblyPatterns, Is.EqualTo("Unity.*"));
                Assert.That(settings.CodeIndex.IgnoredSourcePathPatterns, Does.Contain("Library/PackageCache/com.unity.*"));
                Assert.That(settings.CodeIndex.IgnoredSourcePathPatterns, Does.Contain("Packages/com.unity.*"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Status_WhenNoExecution_ReturnsIdle()
        {
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "code-status-test",
                type = "code",
                @params = new Dictionary<string, object>
                {
                    { "action", "status" }
                }
            });

            Assert.That(result.success, Is.True);
            var json = AIBridgeJson.Serialize(result.data, true);
            StringAssert.Contains("\"active\": false", json);
            StringAssert.Contains("\"status\": \"idle\"", json);
        }

        [Test]
        public void Cancel_WhenNoExecution_ReturnsIdle()
        {
            var result = new CodeCommand().Execute(new CommandRequest
            {
                id = "code-cancel-idle-test",
                type = "code",
                @params = new Dictionary<string, object>
                {
                    { "action", "cancel" }
                }
            });

            Assert.That(result.success, Is.True);
            var json = AIBridgeJson.Serialize(result.data, true);
            StringAssert.Contains("\"canceled\": false", json);
            StringAssert.Contains("\"status\": \"idle\"", json);
        }

        [Test]
        public void Execute_WhenRiskNotAccepted_ReturnsSettingsFailureEvenWithCliAllow()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var previousEnabled = settings.EnableCodeExecution;
            var previousAccepted = settings.CodeExecutionRiskAccepted;
            settings.EnableCodeExecution = true;
            settings.CodeExecutionRiskAccepted = false;

            try
            {
                var result = ExecuteInline("return 1;", true);

                Assert.That(result.success, Is.False);
                Assert.That(result.error, Does.Contain("disabled"));
                Assert.That(result.data, Is.Not.Null);
            }
            finally
            {
                settings.EnableCodeExecution = previousEnabled;
                settings.CodeExecutionRiskAccepted = previousAccepted;
            }
        }

        [Test]
        public void Execute_WhenAllowExperimentalMissing_ContinuesToSourceValidation()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var previousEnabled = settings.EnableCodeExecution;
            var previousAccepted = settings.CodeExecutionRiskAccepted;
            settings.EnableCodeExecution = true;
            settings.CodeExecutionRiskAccepted = true;

            try
            {
                var result = new CodeCommand().Execute(new CommandRequest
                {
                    id = "code-without-allow-test",
                    type = "code",
                    @params = new Dictionary<string, object>
                    {
                        { "action", "execute" }
                    }
                });

                Assert.That(result.success, Is.False);
                Assert.That(result.error, Does.Contain("Provide exactly one source"));
            }
            finally
            {
                settings.EnableCodeExecution = previousEnabled;
                settings.CodeExecutionRiskAccepted = previousAccepted;
            }
        }

        [Test]
        public void Execute_WhenFileOutsideCodeDirectory_ReturnsSourceFailure()
        {
            var settings = AIBridgeProjectSettings.Instance;
            var previousEnabled = settings.EnableCodeExecution;
            var previousAccepted = settings.CodeExecutionRiskAccepted;
            settings.EnableCodeExecution = true;
            settings.CodeExecutionRiskAccepted = true;

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var outsideFile = Path.Combine(projectRoot, ".aibridge", "outside.csx");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outsideFile));
                File.WriteAllText(outsideFile, "return 1;");

                var result = new CodeCommand().Execute(new CommandRequest
                {
                    id = "code-outside-file-test",
                    type = "code",
                    @params = new Dictionary<string, object>
                    {
                        { "action", "execute" },
                        { "file", outsideFile },
                        { "allowExperimental", true }
                    }
                });

                Assert.That(result.success, Is.False);
                Assert.That(result.error, Does.Contain(".aibridge/code"));
            }
            finally
            {
                if (File.Exists(outsideFile))
                {
                    File.Delete(outsideFile);
                }

                settings.EnableCodeExecution = previousEnabled;
                settings.CodeExecutionRiskAccepted = previousAccepted;
            }
        }

        [TestCase("2019.4.40f1", "7.3")]
        [TestCase("2020.1.17f1", "7.3")]
        [TestCase("2020.2.7f1", "8.0")]
        [TestCase("2020.3.48f1", "8.0")]
        [TestCase("2021.1.28f1", "8.0")]
        [TestCase("2021.2.0f1", "9.0")]
        [TestCase("2022.3.20f1", "9.0")]
        [TestCase("6000.3.0f1", "9.0")]
        public void GetSupportedCSharpLanguageVersion_MatchesUnityCompilerVersion(string unityVersion, string expected)
        {
            Assert.That(CodeCommand.GetSupportedCSharpLanguageVersion(unityVersion), Is.EqualTo(expected));
        }

        [Test]
        public void NormalizeReturnValue_ExpandsGenerationResult()
        {
            var generationResult = new AIBridgeGenerationResult()
                .AddAsset("Assets/AIBridgeGenerated/Test/A.mat")
                .AddPrefab("Assets/AIBridgeGenerated/Test/A.prefab")
                .AddScene("Assets/AIBridgeGenerated/Test/A.unity")
                .AddWarning("check budget")
                .AddMessage("done");

            var json = AIBridgeJson.Serialize(CodeCommand.NormalizeReturnValue(generationResult), true);

            StringAssert.Contains("\"assets\"", json);
            StringAssert.Contains("\"prefabs\"", json);
            StringAssert.Contains("\"scenes\"", json);
            StringAssert.Contains("\"warnings\"", json);
            StringAssert.Contains("\"messages\"", json);
            StringAssert.Contains("Assets/AIBridgeGenerated/Test/A.prefab", json);
        }

        [Test]
        public void NormalizeReturnValue_ExpandsSerializableObjectRecursively()
        {
            var report = new SerializableReport
            {
                summary = "ok",
                counts = new Dictionary<string, object>
                {
                    { "renderers", 3 },
                    { "position", new Vector3(1, 2, 3) }
                },
                nested = new SerializableNested
                {
                    path = "Assets/AIBridgeGenerated/Test"
                }
            };

            var json = AIBridgeJson.Serialize(CodeCommand.NormalizeReturnValue(report), true);

            StringAssert.Contains("\"summary\"", json);
            StringAssert.Contains("\"renderers\"", json);
            StringAssert.Contains("\"position\"", json);
            StringAssert.Contains("\"path\"", json);
        }

        [Test]
        public void NormalizeReturnValue_ExpandsAnonymousObject()
        {
            var value = new
            {
                summary = "ok",
                counts = new[] { 1, 2, 3 }
            };

            var json = AIBridgeJson.Serialize(CodeCommand.NormalizeReturnValue(value), true);

            StringAssert.Contains("\"summary\"", json);
            StringAssert.Contains("\"counts\"", json);
        }

        [Test]
        public void GetCompilerCandidatePaths_PrioritizesUnity2019ToolsRoslyn()
        {
            var contentsPath = Path.Combine("UnityRoot", "Editor", "Data");
            var candidates = CodeCommand.GetCompilerCandidatePaths(contentsPath);

            Assert.That(candidates[0], Is.EqualTo(Path.Combine(contentsPath, "Tools", "Roslyn", "csc.exe")));
            Assert.That(candidates[1], Is.EqualTo(Path.Combine(contentsPath, "DotNetSdkRoslyn", "csc.dll")));
            CollectionAssert.Contains(
                candidates,
                Path.Combine(contentsPath, "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn", "csc.exe"));
        }

        [Test]
        public void ResolveDotNetProcessPath_PrefersBundledUnityRuntime()
        {
            var contentsPath = Path.Combine(Path.GetTempPath(), "AIBridgeCodeCommandTests", Guid.NewGuid().ToString("N"));
#if UNITY_EDITOR_WIN
            var dotNetFileName = "dotnet.exe";
#else
            var dotNetFileName = "dotnet";
#endif
            var dotNetPath = Path.Combine(contentsPath, "NetCoreRuntime", dotNetFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dotNetPath));
            File.WriteAllText(dotNetPath, string.Empty);

            try
            {
                Assert.That(CodeCommand.ResolveDotNetProcessPath(contentsPath), Is.EqualTo(dotNetPath));
            }
            finally
            {
                Directory.Delete(contentsPath, true);
            }
        }

        [Test]
        public void ResolveDotNetProcessPath_FallsBackToDotNetCommand()
        {
            var contentsPath = Path.Combine(Path.GetTempPath(), "AIBridgeCodeCommandTests", Guid.NewGuid().ToString("N"));

            Assert.That(CodeCommand.ResolveDotNetProcessPath(contentsPath), Is.EqualTo("dotnet"));
        }

        [Test]
        public void ResolveCompilerOutputEncoding_ReturnsEncoding()
        {
            Assert.That(CodeCommand.ResolveCompilerOutputEncoding().WebName, Is.EqualTo("utf-8"));
        }

        [Test]
        public void ShouldAppendFallbackReturn_DetectsTerminalReturnOrThrow()
        {
            Assert.That(CodeCommand.ShouldAppendFallbackReturn("return 1;"), Is.False);
            Assert.That(CodeCommand.ShouldAppendFallbackReturn("Debug.Log(\"x\");\nreturn new { value = 1 }; // done"), Is.False);
            Assert.That(CodeCommand.ShouldAppendFallbackReturn("throw new System.Exception(\"stop\");"), Is.False);
            Assert.That(CodeCommand.ShouldAppendFallbackReturn("Debug.Log(\"x\");"), Is.True);
            Assert.That(CodeCommand.ShouldAppendFallbackReturn("if (ready) return 1;"), Is.True);
        }

        [Test]
        public void ParseCompilerDiagnostics_ExtractsStructuredFields()
        {
            var output = "Assets/Test.cs(12,34): error CS1002: ; expected\n"
                         + "Assets/Test.cs(13,2): warning CS0162: Unreachable code detected";

            var diagnostics = CodeCommand.ParseCompilerDiagnostics(output);

            Assert.That(diagnostics.Count, Is.EqualTo(2));
            Assert.That(diagnostics[0].file, Is.EqualTo("Assets/Test.cs"));
            Assert.That(diagnostics[0].line, Is.EqualTo(12));
            Assert.That(diagnostics[0].column, Is.EqualTo(34));
            Assert.That(diagnostics[0].severity, Is.EqualTo("error"));
            Assert.That(diagnostics[0].code, Is.EqualTo("CS1002"));
            Assert.That(diagnostics[1].severity, Is.EqualTo("warning"));
            Assert.That(diagnostics[1].code, Is.EqualTo("CS0162"));
        }

        [Test]
        public void CodeCacheCleaner_RemovesExpiredScriptsAndCompiledArtifacts()
        {
            var root = Path.Combine(Path.GetTempPath(), "AIBridgeCodeCacheCleanerTests", Guid.NewGuid().ToString("N"));
            var bridgeDirectory = Path.Combine(root, ".aibridge");
            var codeDirectory = Path.Combine(bridgeDirectory, "code");
            var compiledDirectory = Path.Combine(codeDirectory, ".compiled");
            var nestedDirectory = Path.Combine(codeDirectory, "nested");
            var now = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc);
            var staleTime = now.AddDays(-4);
            var recentTime = now.AddDays(-2);

            var staleScript = Path.Combine(codeDirectory, "old.csx");
            var staleSource = Path.Combine(codeDirectory, "old_source.cs");
            var recentScript = Path.Combine(codeDirectory, "recent.csx");
            var ignoredText = Path.Combine(codeDirectory, "old.txt");
            var nestedScript = Path.Combine(nestedDirectory, "nested_old.csx");
            var staleGenerated = Path.Combine(compiledDirectory, "AIBridgeCode_old.generated.cs");
            var staleDll = Path.Combine(compiledDirectory, "AIBridgeCode_old.dll");
            var staleResponse = Path.Combine(compiledDirectory, "AIBridgeCode_old.rsp");
            var recentDll = Path.Combine(compiledDirectory, "AIBridgeCode_recent.dll");
            var ignoredPdb = Path.Combine(compiledDirectory, "AIBridgeCode_old.pdb");

            try
            {
                Directory.CreateDirectory(compiledDirectory);
                Directory.CreateDirectory(nestedDirectory);

                WriteFile(staleScript, staleTime);
                WriteFile(staleSource, staleTime);
                WriteFile(recentScript, recentTime);
                WriteFile(ignoredText, staleTime);
                WriteFile(nestedScript, staleTime);
                WriteFile(staleGenerated, staleTime);
                WriteFile(staleDll, staleTime);
                WriteFile(staleResponse, staleTime);
                WriteFile(recentDll, recentTime);
                WriteFile(ignoredPdb, staleTime);

                var cleanedCount = CodeCacheCleaner.CleanupIfNeeded(bridgeDirectory, now, TimeSpan.FromDays(3));

                Assert.That(cleanedCount, Is.EqualTo(5));
                Assert.That(File.Exists(staleScript), Is.False);
                Assert.That(File.Exists(staleSource), Is.False);
                Assert.That(File.Exists(staleGenerated), Is.False);
                Assert.That(File.Exists(staleDll), Is.False);
                Assert.That(File.Exists(staleResponse), Is.False);
                Assert.That(File.Exists(recentScript), Is.True);
                Assert.That(File.Exists(ignoredText), Is.True);
                Assert.That(File.Exists(nestedScript), Is.True);
                Assert.That(File.Exists(recentDll), Is.True);
                Assert.That(File.Exists(ignoredPdb), Is.True);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void CodeCacheCleaner_WhenCodeDirectoryMissing_ReturnsZero()
        {
            var root = Path.Combine(Path.GetTempPath(), "AIBridgeCodeCacheCleanerTests", Guid.NewGuid().ToString("N"));
            var bridgeDirectory = Path.Combine(root, ".aibridge");

            try
            {
                Directory.CreateDirectory(bridgeDirectory);

                var cleanedCount = CodeCacheCleaner.CleanupIfNeeded(bridgeDirectory, DateTime.UtcNow, TimeSpan.FromDays(3));

                Assert.That(cleanedCount, Is.EqualTo(0));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static CommandResult ExecuteInline(string code, bool allowExperimental)
        {
            var request = new CommandRequest
            {
                id = "code-inline-test",
                type = "code",
                @params = new Dictionary<string, object>
                {
                    { "action", "execute" },
                    { "code", code }
                }
            };

            if (allowExperimental)
            {
                request.@params["allowExperimental"] = true;
            }

            return new CodeCommand().Execute(request);
        }

        private static void WriteFile(string path, DateTime lastWriteTimeUtc)
        {
            File.WriteAllText(path, "return 1;");
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        }

        [Serializable]
        private sealed class SerializableReport
        {
            public string summary;
            public Dictionary<string, object> counts;
            public SerializableNested nested;
        }

        [Serializable]
        private sealed class SerializableNested
        {
            public string path;
        }
    }
}
