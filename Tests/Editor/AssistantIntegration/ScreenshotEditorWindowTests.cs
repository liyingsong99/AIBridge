using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AIBridge.Editor.Tests
{
    public class ScreenshotEditorWindowTests
    {
        private readonly List<EditorWindow> _windows = new List<EditorWindow>();

        private sealed class FirstTestWindow : EditorWindow
        {
        }

        private sealed class SecondTestWindow : EditorWindow
        {
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i] != null)
                {
                    Object.DestroyImmediate(_windows[i]);
                }
            }

            _windows.Clear();
            EditorWindowFocusTracker.LastFocusedWindow = null;
        }

        [Test]
        public void Resolve_TargetEditor_ReturnsMainEditorTarget()
        {
            var result = Resolve(new Dictionary<string, object>
            {
                { "target", "editor" }
            });

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Target.CaptureMainEditor, Is.True);
            Assert.That(result.Target.Window, Is.Null);
        }

        [Test]
        public void Resolve_TargetActive_ReturnsTrackedUnityWindow()
        {
            var window = CreateWindow<FirstTestWindow>("Active Tool");

            var result = Resolve(
                new Dictionary<string, object> { { "target", "active" } },
                window);

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Target.Window, Is.SameAs(window));
        }

        [Test]
        public void Resolve_TypeAndTitle_UsesAndSemanticsAndCaseInsensitiveTitle()
        {
            var expected = CreateWindow<FirstTestWindow>("My Tool");
            CreateWindow<FirstTestWindow>("Other Tool");
            CreateWindow<SecondTestWindow>("My Tool");

            var result = Resolve(new Dictionary<string, object>
            {
                { "windowType", nameof(FirstTestWindow) },
                { "title", "my tool" }
            });

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Target.Window, Is.SameAs(expected));
        }

        [Test]
        public void Resolve_InstanceId_DisambiguatesSameTypeWindows()
        {
            CreateWindow<FirstTestWindow>("One");
            var expected = CreateWindow<FirstTestWindow>("Two");

            var result = Resolve(new Dictionary<string, object>
            {
                { "windowType", nameof(FirstTestWindow) },
                { "instanceId", expected.GetInstanceID() }
            });

            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.Target.Window, Is.SameAs(expected));
        }

        [Test]
        public void Resolve_MultipleMatches_ReturnsCompactAmbiguousError()
        {
            CreateWindow<FirstTestWindow>("One");
            CreateWindow<FirstTestWindow>("Two");

            var result = Resolve(new Dictionary<string, object>
            {
                { "windowType", nameof(FirstTestWindow) }
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(EditorWindowCaptureErrorCodes.TargetAmbiguous));
            Assert.That(result.ErrorMessage, Does.Not.Contain(nameof(FirstTestWindow)));
        }

        [Test]
        public void Resolve_TargetCombinedWithFilter_ReturnsCaptureFailed()
        {
            var result = Resolve(new Dictionary<string, object>
            {
                { "target", "active" },
                { "title", "Tool" }
            });

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo(EditorWindowCaptureErrorCodes.CaptureFailed));
        }

        [Test]
        public void ScreenshotSkillDescription_DocumentsEditorWindowWithoutVerboseResultFields()
        {
            var description = new ScreenshotCommand().SkillDescription;

            Assert.That(description, Does.Contain("screenshot editor_window --target editor"));
            Assert.That(description, Does.Contain("--windowType"));
            Assert.That(description, Does.Contain("Unity-owned window pixels"));
            Assert.That(description, Does.Not.Contain("captureBackend"));
            Assert.That(description, Does.Not.Contain("pixelRect"));
        }

        [Test]
        public void CliBuilder_RegistersEditorWindowSelectors()
        {
            var source = ReadCliSource("Commands/ScreenshotCommandBuilder.cs");

            Assert.That(source, Does.Contain("\"editor_window\""));
            Assert.That(source, Does.Contain("new ParameterInfo(\"target\""));
            Assert.That(source, Does.Contain("new ParameterInfo(\"windowType\""));
            Assert.That(source, Does.Contain("new ParameterInfo(\"title\""));
            Assert.That(source, Does.Contain("new ParameterInfo(\"instanceId\""));
        }

        private EditorWindowCaptureResolution Resolve(
            Dictionary<string, object> parameters,
            EditorWindow activeWindow = null)
        {
            return EditorWindowCaptureTargetResolver.Resolve(
                new CommandRequest
                {
                    id = "editor-window-test",
                    type = "screenshot",
                    @params = parameters
                },
                _windows,
                activeWindow,
                resolveScreenRect: false);
        }

        private T CreateWindow<T>(string title) where T : EditorWindow
        {
            var window = ScriptableObject.CreateInstance<T>();
            window.titleContent = new GUIContent(title);
            _windows.Add(window);
            return window;
        }

        private static string ReadCliSource(string relativePath)
        {
            var packageInfo = PackageManagerPackageInfo.FindForAssembly(typeof(AIBridgeProjectSettings).Assembly);
            var packageRoot = packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath)
                ? packageInfo.resolvedPath
                : Directory.GetCurrentDirectory();
            return File.ReadAllText(Path.Combine(
                packageRoot,
                "Tools~",
                "AIBridgeCLI",
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
