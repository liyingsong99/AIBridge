using System.Collections;
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
                @params = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "action", "click" },
                    { "path", "Canvas/Button" }
                }
            });

            Assert.That(result.success, Is.True, result.error);
            Assert.That(clickCount, Is.EqualTo(1));

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
