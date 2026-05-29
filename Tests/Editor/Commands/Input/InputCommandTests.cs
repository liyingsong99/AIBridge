using System.Collections;
using System.Collections.Generic;
using AIBridge.Internal.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AIBridge.Editor.Tests
{
    public class InputCommandTests
    {
        [Test]
        public void Execute_WhenNotInPlayMode_ReturnsClearFailure()
        {
            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-test",
                type = "input",
                @params = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "action", "click" },
                    { "path", "Canvas/Button" }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("Play mode"));
        }

        [Test]
        public void SkillDescription_IncludesPlayModeAndEventSystemRequirements()
        {
            var description = new InputCommand().SkillDescription;

            Assert.That(description, Does.Contain("Play mode"));
            Assert.That(description, Does.Contain("EventSystem"));
            Assert.That(description, Does.Contain("$CLI input click"));
            Assert.That(description, Does.Contain("click_pct"));
            Assert.That(description, Does.Contain("bottom-left"));
        }

        [Test]
        public void ClickPct_WhenCoordinateOutOfRange_ReturnsClearFailure()
        {
            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-click-pct-range-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click_pct" },
                    { "x", 1.2f },
                    { "y", 0.5f }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("--x"));
            Assert.That(result.error, Does.Contain("0 and 1"));
        }

        [Test]
        public void ClickPct_WhenOriginParameterProvided_ReturnsClearFailure()
        {
            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-click-pct-origin-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click_pct" },
                    { "x", 0.5f },
                    { "y", 0.5f },
                    { "origin", "screenshot-top-left" }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("--origin"));
            Assert.That(result.error, Does.Contain("bottom-left"));
        }

        [Test]
        public void ClickPct_WhenNotInPlayMode_ReturnsClearFailure()
        {
            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-click-pct-not-playmode-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click_pct" },
                    { "x", 0.5f },
                    { "y", 0.5f }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("Play mode"));
        }

        [UnityTest]
        public IEnumerator Click_InPlayMode_InvokesUguiButton()
        {
            yield return new EnterPlayMode();

            var clickCount = 0;
            CreateEventSystem();
            CreateButton("Canvas", "Button", () => clickCount++);

            yield return null;

            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-playmode-click-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click" },
                    { "path", "Canvas/Button" }
                }
            });

            Assert.That(result.success, Is.True, result.error);
            Assert.That(clickCount, Is.EqualTo(1));

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ClickPct_InPlayMode_InvokesCenteredUguiButton()
        {
            yield return new EnterPlayMode();

            var clickCount = 0;
            CreateEventSystem();
            CreateButton("Canvas", "Button", () => clickCount++);

            yield return null;

            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-playmode-click-pct-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click_pct" },
                    { "x", 0.5f },
                    { "y", 0.5f }
                }
            });

            Assert.That(result.success, Is.True, result.error);
            Assert.That(clickCount, Is.EqualTo(1));

            var json = AIBridgeJson.Serialize(result.data, true);
            StringAssert.Contains("\"action\": \"click_pct\"", json);
            StringAssert.Contains("\"normalizedPosition\"", json);
            StringAssert.Contains("\"coordinateSpace\": \"unity-screen-normalized\"", json);
            StringAssert.Contains("\"origin\": \"bottom-left\"", json);
            StringAssert.Contains("\"screenSize\"", json);
            StringAssert.Contains("\"screenPosition\"", json);
            StringAssert.Contains("\"clickHandler\"", json);

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ClickPct_InPlayModeWithoutEventSystem_ReturnsClearFailure()
        {
            yield return new EnterPlayMode();

            DestroyExistingEventSystems();
            yield return null;

            var command = new InputCommand();
            var result = command.Execute(new CommandRequest
            {
                id = "input-playmode-click-pct-no-event-system-test",
                type = "input",
                @params = new Dictionary<string, object>
                {
                    { "action", "click_pct" },
                    { "x", 0.5f },
                    { "y", 0.5f }
                }
            });

            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("EventSystem"));

            yield return new ExitPlayMode();
        }

        private static void CreateEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        private static void DestroyExistingEventSystems()
        {
            var eventSystems = UnityEngine.Object.FindObjectsOfType<EventSystem>();
            for (var i = 0; i < eventSystems.Length; i++)
            {
                if (eventSystems[i] != null)
                {
                    UnityEngine.Object.Destroy(eventSystems[i].gameObject);
                }
            }
        }

        private static void CreateButton(string canvasName, string buttonName, UnityEngine.Events.UnityAction onClick)
        {
            var canvasObject = new GameObject(canvasName, typeof(RectTransform));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            var canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(800f, 600f);

            var buttonObject = new GameObject(buttonName, typeof(RectTransform));
            buttonObject.transform.SetParent(canvasObject.transform, false);

            var rectTransform = buttonObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(160f, 60f);
            rectTransform.anchoredPosition = Vector2.zero;

            var image = buttonObject.AddComponent<Image>();
            image.raycastTarget = true;

            var button = buttonObject.AddComponent<Button>();
            button.onClick.AddListener(onClick);
        }
    }
}
