using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AIBridge.Editor
{
    /// <summary>
    /// Runtime input simulation command for Play Mode UI automation.
    /// </summary>
    public class InputCommand : ICommand
    {
        private const int MinDragFrames = 3;
        private const int MaxDragFrames = 60;
        private const int DefaultDragFrames = 12;
        private const int DefaultLongPressDurationMs = 1000;
        private const int MinLongPressDurationMs = 1;
        private const int MaxLongPressDurationMs = 60000;
        private const int LeftMousePointerId = -1;
        private const string NormalizedCoordinateSpace = "unity-screen-normalized";
        private const string UnityScreenOrigin = "bottom-left";

        private static InputAsyncOperation _activeOperation;

        public string Type => "input";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `input` - Runtime Input Simulation (Play Mode)

**Requires Play mode and an active EventSystem.** Uses Unity screen coordinates (bottom-left origin).

```bash
$CLI input click --path ""Canvas/StartButton""
$CLI input click_at --x 960 --y 540
$CLI input click_pct --x 0.5 --y 0.5
$CLI input drag --path ""Canvas/Item"" --toPath ""Canvas/Slot"" --frames 12
$CLI input long_press --instanceId 12345 --duration-ms 800
```

**Actions:**
- `click`: click a GameObject by `--path` or `--instanceId`
- `click_at`: click screen coordinates with `--x` and `--y`
- `click_pct`: click normalized Unity screen coordinates with `--x` and `--y` in `[0, 1]`; origin is always bottom-left
- `drag`: drag from `--path`/`--instanceId` to `--toPath`/`--toInstanceId` or `--toX --toY`
- `long_press`: hold a target for `--duration-ms` milliseconds

Recommended flow: `editor play` -> `scene get_hierarchy` -> `input click` -> `get_logs --logType Error` -> `screenshot game` -> `editor stop`.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "click");

            try
            {
                switch (action.ToLower())
                {
                    case "click":
                        return Click(request);
                    case "click_at":
                        return ClickAt(request);
                    case "click_pct":
                        return ClickPct(request);
                    case "drag":
                        return Drag(request);
                    case "long_press":
                        return LongPress(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: click, click_at, click_pct, drag, long_press");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult Click(CommandRequest request)
        {
            CommandResult validation;
            if (!ValidateRuntimeInput(request.id, out validation))
            {
                return validation;
            }

            GameObject target;
            string targetError;
            if (!TryResolveTarget(request, "path", "instanceId", true, out target, out targetError))
            {
                return CommandResult.Failure(request.id, targetError);
            }

            Vector2 position;
            string positionMode;
            if (!TryResolveScreenPoint(target, out position, out positionMode, out targetError))
            {
                return CommandResult.Failure(request.id, targetError);
            }

            PointerPressState pressState;
            if (!TryCreatePointerPress(position, target, true, out pressState, out targetError))
            {
                return CommandResult.Failure(request.id, targetError);
            }

            FinishClick(pressState);

            return CommandResult.Success(request.id, new
            {
                action = "click",
                target = BuildObjectInfo(target),
                position = BuildVector2Info(position),
                positionMode = positionMode,
                raycastTarget = BuildObjectInfo(pressState.RaycastTarget),
                pointerPress = BuildObjectInfo(pressState.PointerPress),
                clickHandler = BuildObjectInfo(pressState.ClickHandler)
            });
        }

        private CommandResult ClickAt(CommandRequest request)
        {
            CommandResult validation;
            if (!ValidateRuntimeInput(request.id, out validation))
            {
                return validation;
            }

            float x;
            float y;
            string error;
            if (!TryGetRequiredFloat(request, "x", out x, out error) || !TryGetRequiredFloat(request, "y", out y, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            var position = new Vector2(x, y);
            PointerPressState pressState;
            if (!TryCreatePointerPress(position, null, false, out pressState, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            FinishClick(pressState);

            return CommandResult.Success(request.id, new
            {
                action = "click_at",
                position = BuildVector2Info(position),
                raycastTarget = BuildObjectInfo(pressState.RaycastTarget),
                pointerPress = BuildObjectInfo(pressState.PointerPress),
                clickHandler = BuildObjectInfo(pressState.ClickHandler)
            });
        }

        private CommandResult ClickPct(CommandRequest request)
        {
            Vector2 normalizedPosition;
            string error;
            if (!TryGetNormalizedPosition(request, out normalizedPosition, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            CommandResult validation;
            if (!ValidateRuntimeInput(request.id, out validation))
            {
                return validation;
            }

            Vector2 screenPosition;
            if (!TryConvertNormalizedPositionToScreenPoint(normalizedPosition, out screenPosition, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            PointerPressState pressState;
            if (!TryCreatePointerPress(screenPosition, null, false, out pressState, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            FinishClick(pressState);

            return CommandResult.Success(request.id, new
            {
                action = "click_pct",
                normalizedPosition = BuildVector2Info(normalizedPosition),
                coordinateSpace = NormalizedCoordinateSpace,
                origin = UnityScreenOrigin,
                screenSize = BuildScreenSizeInfo(),
                screenPosition = BuildVector2Info(screenPosition),
                raycastTarget = BuildObjectInfo(pressState.RaycastTarget),
                pointerPress = BuildObjectInfo(pressState.PointerPress),
                clickHandler = BuildObjectInfo(pressState.ClickHandler)
            });
        }

        private CommandResult Drag(CommandRequest request)
        {
            CommandResult validation;
            if (!ValidateRuntimeInput(request.id, out validation))
            {
                return validation;
            }

            if (_activeOperation != null)
            {
                return CommandResult.Failure(request.id, "Another input simulation is already running.");
            }

            GameObject source;
            string error;
            if (!TryResolveTarget(request, "path", "instanceId", true, out source, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            Vector2 startPosition;
            string startPositionMode;
            if (!TryResolveScreenPoint(source, out startPosition, out startPositionMode, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            GameObject destination;
            Vector2 endPosition;
            string endPositionMode;
            if (!TryResolveDestination(request, out destination, out endPosition, out endPositionMode, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            PointerPressState pressState;
            if (!TryCreatePointerPress(startPosition, source, true, out pressState, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            if (pressState.PointerDrag == null)
            {
                FinishPointerUp(pressState);
                return CommandResult.Failure(request.id, "Source target has no IDragHandler in its event handler hierarchy.");
            }

            var frames = Mathf.Clamp(request.GetParam("frames", DefaultDragFrames), MinDragFrames, MaxDragFrames);
            _activeOperation = InputAsyncOperation.CreateDrag(
                request.id,
                pressState,
                source,
                destination,
                startPosition,
                endPosition,
                startPositionMode,
                endPositionMode,
                frames);
            EditorApplication.update -= OnAsyncUpdate;
            EditorApplication.update += OnAsyncUpdate;
            return null;
        }

        private CommandResult LongPress(CommandRequest request)
        {
            CommandResult validation;
            if (!ValidateRuntimeInput(request.id, out validation))
            {
                return validation;
            }

            if (_activeOperation != null)
            {
                return CommandResult.Failure(request.id, "Another input simulation is already running.");
            }

            GameObject target;
            string error;
            if (!TryResolveTarget(request, "path", "instanceId", true, out target, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            Vector2 position;
            string positionMode;
            if (!TryResolveScreenPoint(target, out position, out positionMode, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            PointerPressState pressState;
            if (!TryCreatePointerPress(position, target, true, out pressState, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            var durationMs = Mathf.Clamp(
                request.GetParam("durationMs", DefaultLongPressDurationMs),
                MinLongPressDurationMs,
                MaxLongPressDurationMs);

            _activeOperation = InputAsyncOperation.CreateLongPress(
                request.id,
                pressState,
                target,
                position,
                positionMode,
                durationMs);
            EditorApplication.update -= OnAsyncUpdate;
            EditorApplication.update += OnAsyncUpdate;
            return null;
        }

        private static bool ValidateRuntimeInput(string requestId, out CommandResult result)
        {
            if (!Application.isPlaying)
            {
                result = CommandResult.Failure(requestId, "Input simulation requires Play mode. Start Play mode before using the input command.");
                return false;
            }

            if (EventSystem.current == null)
            {
                result = CommandResult.Failure(requestId, "Input simulation requires an active EventSystem in the scene.");
                return false;
            }

            result = null;
            return true;
        }

        private static bool TryResolveTarget(
            CommandRequest request,
            string pathParam,
            string instanceIdParam,
            bool requireActive,
            out GameObject target,
            out string error)
        {
            target = null;
            error = null;

            var path = request.GetParam<string>(pathParam, null);
            var instanceId = request.GetParam(instanceIdParam, 0);

            if (instanceId != 0)
            {
#if UNITY_6000_3_OR_NEWER
                var obj = EditorUtility.EntityIdToObject(instanceId);
#else
                var obj = EditorUtility.InstanceIDToObject(instanceId);
#endif
                target = ObjectToGameObject(obj);
            }
            else if (!string.IsNullOrEmpty(path))
            {
                target = GameObject.Find(path);
            }

            if (target == null)
            {
                error = $"GameObject not found. Provide --{pathParam} or --{instanceIdParam}.";
                return false;
            }

            if (requireActive && !target.activeInHierarchy)
            {
                error = $"GameObject is inactive and cannot receive runtime input: {GetGameObjectPath(target)}";
                return false;
            }

            return true;
        }

        private static bool TryResolveDestination(
            CommandRequest request,
            out GameObject destination,
            out Vector2 position,
            out string positionMode,
            out string error)
        {
            destination = null;
            position = Vector2.zero;
            positionMode = null;
            error = null;

            if (request.HasParam("toPath") || request.HasParam("toInstanceId"))
            {
                if (!TryResolveTarget(request, "toPath", "toInstanceId", true, out destination, out error))
                {
                    return false;
                }

                return TryResolveScreenPoint(destination, out position, out positionMode, out error);
            }

            float x;
            float y;
            if (TryGetRequiredFloat(request, "toX", out x, out error) && TryGetRequiredFloat(request, "toY", out y, out error))
            {
                position = new Vector2(x, y);
                positionMode = "coordinates";
                return true;
            }

            error = "Drag target is required. Provide --toPath/--toInstanceId or --toX --toY.";
            return false;
        }

        private static bool TryGetNormalizedPosition(
            CommandRequest request,
            out Vector2 normalizedPosition,
            out string error)
        {
            normalizedPosition = Vector2.zero;

            if (request.HasParam("origin"))
            {
                error = "click_pct uses Unity normalized screen coordinates with bottom-left origin. The --origin option is not supported.";
                return false;
            }

            float x;
            float y;
            if (!TryGetRequiredFloat(request, "x", out x, out error) || !TryGetRequiredFloat(request, "y", out y, out error))
            {
                return false;
            }

            if (!IsNormalizedCoordinate(x))
            {
                error = "--x must be between 0 and 1 for click_pct.";
                return false;
            }

            if (!IsNormalizedCoordinate(y))
            {
                error = "--y must be between 0 and 1 for click_pct.";
                return false;
            }

            normalizedPosition = new Vector2(x, y);
            error = null;
            return true;
        }

        private static bool TryConvertNormalizedPositionToScreenPoint(
            Vector2 normalizedPosition,
            out Vector2 screenPosition,
            out string error)
        {
            screenPosition = Vector2.zero;

            if (Screen.width <= 0 || Screen.height <= 0)
            {
                error = "Screen size is unavailable for click_pct.";
                return false;
            }

            // click_pct 使用 Unity 屏幕坐标约定：左下为原点，避免截图原点和 Unity 原点混用。
            screenPosition = new Vector2(normalizedPosition.x * Screen.width, normalizedPosition.y * Screen.height);
            error = null;
            return true;
        }

        private static bool TryGetRequiredFloat(CommandRequest request, string key, out float value, out string error)
        {
            value = request.GetParam(key, float.NaN);
            if (float.IsNaN(value))
            {
                error = $"Missing required parameter: --{key}";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsNormalizedCoordinate(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 1f;
        }

        private static bool TryResolveScreenPoint(GameObject target, out Vector2 position, out string positionMode, out string error)
        {
            position = Vector2.zero;
            positionMode = null;
            error = null;

            if (target == null)
            {
                error = "Target GameObject is null.";
                return false;
            }

            var rectTransform = target.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                var camera = GetCanvasCamera(rectTransform);
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                var center = (corners[0] + corners[2]) * 0.5f;
                position = RectTransformUtility.WorldToScreenPoint(camera, center);
                positionMode = "rectTransform";
                return true;
            }

            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                return TryWorldToScreenPoint(renderer.bounds.center, "rendererBounds", out position, out positionMode, out error);
            }

            var collider = target.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                return TryWorldToScreenPoint(collider.bounds.center, "colliderBounds", out position, out positionMode, out error);
            }

            return TryWorldToScreenPoint(target.transform.position, "transformPosition", out position, out positionMode, out error);
        }

        private static Camera GetCanvasCamera(RectTransform rectTransform)
        {
            var canvas = rectTransform.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }

            if (canvas.worldCamera != null)
            {
                return canvas.worldCamera;
            }

            return Camera.main;
        }

        private static bool TryWorldToScreenPoint(
            Vector3 worldPosition,
            string mode,
            out Vector2 position,
            out string positionMode,
            out string error)
        {
            position = Vector2.zero;
            positionMode = null;
            error = null;

            var camera = Camera.main;
            if (camera == null)
            {
                error = "Camera.main is required to resolve non-UI target screen position.";
                return false;
            }

            var screenPoint = camera.WorldToScreenPoint(worldPosition);
            if (screenPoint.z < 0f)
            {
                error = "Target is behind Camera.main and cannot be converted to a screen position.";
                return false;
            }

            position = new Vector2(screenPoint.x, screenPoint.y);
            positionMode = mode;
            return true;
        }

        private static bool TryCreatePointerPress(
            Vector2 position,
            GameObject fallbackTarget,
            bool allowFallbackTarget,
            out PointerPressState state,
            out string error)
        {
            state = null;
            error = null;

            List<RaycastResult> raycastResults;
            var raycast = Raycast(position, out raycastResults);
            var raycastTarget = raycast.gameObject;
            var eventTarget = raycastTarget != null ? raycastTarget : fallbackTarget;

            if (eventTarget == null)
            {
                error = "No EventSystem raycast target found at the requested screen position.";
                return false;
            }

            if (raycastTarget == null && !allowFallbackTarget)
            {
                error = "No EventSystem raycast target found at the requested screen position.";
                return false;
            }

            var data = CreatePointerData(position);
            data.pointerCurrentRaycast = raycast;
            data.pointerPressRaycast = raycast;
            data.rawPointerPress = eventTarget;
            data.pressPosition = position;
            data.eligibleForClick = true;
            data.clickTime = Time.unscaledTime;
            data.clickCount = 1;
            data.useDragThreshold = true;
            data.pointerEnter = eventTarget;

            var selected = ExecuteEvents.GetEventHandler<ISelectHandler>(eventTarget);
            if (selected != null)
            {
                EventSystem.current.SetSelectedGameObject(selected, data);
            }

            // Unity StandaloneInputModule 的按下流程会先找 PointerDown，找不到再退到 PointerClick。
            var pointerPress = ExecuteEvents.ExecuteHierarchy(eventTarget, data, ExecuteEvents.pointerDownHandler);
            if (pointerPress == null)
            {
                pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            }

            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            var pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(eventTarget);
            if (pointerDrag != null)
            {
                ExecuteEvents.Execute(pointerDrag, data, ExecuteEvents.initializePotentialDrag);
            }

            if (pointerPress == null && clickHandler == null && pointerDrag == null)
            {
                error = $"No pointer input handler found for target: {GetGameObjectPath(eventTarget)}";
                return false;
            }

            data.pointerPress = pointerPress;
            data.pointerDrag = pointerDrag;

            state = new PointerPressState
            {
                Data = data,
                RaycastTarget = raycastTarget,
                EventTarget = eventTarget,
                PointerPress = pointerPress,
                ClickHandler = clickHandler,
                PointerDrag = pointerDrag,
                Position = position
            };
            return true;
        }

        private static PointerEventData CreatePointerData(Vector2 position)
        {
            return new PointerEventData(EventSystem.current)
            {
                pointerId = LeftMousePointerId,
                button = PointerEventData.InputButton.Left,
                position = position,
                delta = Vector2.zero
            };
        }

        private static RaycastResult Raycast(Vector2 position, out List<RaycastResult> results)
        {
            results = new List<RaycastResult>();
            var data = CreatePointerData(position);
            EventSystem.current.RaycastAll(data, results);

            for (var i = 0; i < results.Count; i++)
            {
                if (results[i].gameObject != null)
                {
                    return results[i];
                }
            }

            return new RaycastResult();
        }

        private static void FinishClick(PointerPressState state)
        {
            FinishPointerUp(state);
            if (state.PointerPress != null && state.ClickHandler != null && state.PointerPress == state.ClickHandler && state.Data.eligibleForClick)
            {
                ExecuteEvents.Execute(state.PointerPress, state.Data, ExecuteEvents.pointerClickHandler);
            }

            ClearPointerState(state);
        }

        private static void FinishPointerUp(PointerPressState state)
        {
            if (state != null && state.PointerPress != null)
            {
                ExecuteEvents.Execute(state.PointerPress, state.Data, ExecuteEvents.pointerUpHandler);
            }
        }

        private static void ClearPointerState(PointerPressState state)
        {
            if (state == null || state.Data == null)
            {
                return;
            }

            state.Data.eligibleForClick = false;
            state.Data.pointerPress = null;
            state.Data.rawPointerPress = null;
            state.Data.pointerDrag = null;
            state.Data.dragging = false;
        }

        private static void OnAsyncUpdate()
        {
            if (_activeOperation == null)
            {
                EditorApplication.update -= OnAsyncUpdate;
                return;
            }

            if (!_activeOperation.Step())
            {
                return;
            }

            var result = _activeOperation.BuildResult();
            _activeOperation = null;
            EditorApplication.update -= OnAsyncUpdate;
            WriteResultFile(result);
        }

        private static void WriteResultFile(CommandResult result)
        {
            try
            {
                var resultsDir = Path.Combine(AIBridge.BridgeDirectory, "results");
                if (!Directory.Exists(resultsDir))
                {
                    Directory.CreateDirectory(resultsDir);
                }

                var filePath = Path.Combine(resultsDir, $"{result.id}.json");
                var json = AIBridgeJson.Serialize(result, true);
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogError($"Failed to write input result for {result.id}: {ex.Message}");
            }
        }

        private static GameObject ObjectToGameObject(UnityEngine.Object obj)
        {
            var go = obj as GameObject;
            if (go != null)
            {
                return go;
            }

            var component = obj as Component;
            return component != null ? component.gameObject : null;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static object BuildObjectInfo(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            return new
            {
                name = go.name,
                path = GetGameObjectPath(go),
                instanceId = go.GetInstanceID()
            };
        }

        private static object BuildVector2Info(Vector2 value)
        {
            return new
            {
                x = value.x,
                y = value.y
            };
        }

        private static object BuildScreenSizeInfo()
        {
            return new
            {
                width = Screen.width,
                height = Screen.height
            };
        }

        private sealed class PointerPressState
        {
            public PointerEventData Data;
            public GameObject RaycastTarget;
            public GameObject EventTarget;
            public GameObject PointerPress;
            public GameObject ClickHandler;
            public GameObject PointerDrag;
            public Vector2 Position;
        }

        private sealed class InputAsyncOperation
        {
            private string _requestId;
            private string _action;
            private PointerPressState _pressState;
            private GameObject _source;
            private GameObject _destination;
            private Vector2 _startPosition;
            private Vector2 _endPosition;
            private Vector2 _lastPosition;
            private string _startPositionMode;
            private string _endPositionMode;
            private int _frames;
            private int _currentFrame;
            private int _durationMs;
            private double _releaseTime;
            private bool _beganDrag;
            private CommandResult _result;

            public static InputAsyncOperation CreateDrag(
                string requestId,
                PointerPressState pressState,
                GameObject source,
                GameObject destination,
                Vector2 startPosition,
                Vector2 endPosition,
                string startPositionMode,
                string endPositionMode,
                int frames)
            {
                return new InputAsyncOperation
                {
                    _requestId = requestId,
                    _action = "drag",
                    _pressState = pressState,
                    _source = source,
                    _destination = destination,
                    _startPosition = startPosition,
                    _endPosition = endPosition,
                    _lastPosition = startPosition,
                    _startPositionMode = startPositionMode,
                    _endPositionMode = endPositionMode,
                    _frames = frames
                };
            }

            public static InputAsyncOperation CreateLongPress(
                string requestId,
                PointerPressState pressState,
                GameObject target,
                Vector2 position,
                string positionMode,
                int durationMs)
            {
                return new InputAsyncOperation
                {
                    _requestId = requestId,
                    _action = "long_press",
                    _pressState = pressState,
                    _source = target,
                    _startPosition = position,
                    _endPosition = position,
                    _startPositionMode = positionMode,
                    _durationMs = durationMs,
                    _releaseTime = EditorApplication.timeSinceStartup + durationMs / 1000.0
                };
            }

            public bool Step()
            {
                try
                {
                    if (_action == "drag")
                    {
                        return StepDrag();
                    }

                    return StepLongPress();
                }
                catch (Exception ex)
                {
                    _result = CommandResult.Failure(_requestId, $"Input simulation failed: {ex.GetType().Name}: {ex.Message}");
                    ClearPointerState(_pressState);
                    return true;
                }
            }

            public CommandResult BuildResult()
            {
                return _result ?? CommandResult.Failure(_requestId, "Input simulation ended without a result.");
            }

            private bool StepDrag()
            {
                _currentFrame++;
                var t = Mathf.Clamp01(_currentFrame / (float)_frames);
                var position = Vector2.Lerp(_startPosition, _endPosition, t);
                var data = _pressState.Data;

                data.delta = position - _lastPosition;
                data.position = position;
                data.eligibleForClick = false;

                List<RaycastResult> results;
                var raycast = Raycast(position, out results);
                data.pointerCurrentRaycast = raycast;
                if (raycast.gameObject != null)
                {
                    data.pointerEnter = raycast.gameObject;
                }

                // BeginDrag 只触发一次；之后每帧更新 delta，保证 ScrollRect/自定义拖拽能识别位移。
                if (!_beganDrag)
                {
                    _beganDrag = true;
                    data.dragging = true;
                    ExecuteEvents.Execute(_pressState.PointerDrag, data, ExecuteEvents.beginDragHandler);
                }

                ExecuteEvents.Execute(_pressState.PointerDrag, data, ExecuteEvents.dragHandler);
                _lastPosition = position;

                if (_currentFrame < _frames)
                {
                    return false;
                }

                FinishDrag(raycast.gameObject);
                return true;
            }

            private void FinishDrag(GameObject currentRaycastTarget)
            {
                var data = _pressState.Data;
                var dropTarget = currentRaycastTarget != null ? currentRaycastTarget : _destination;
                var pointerDragInfo = BuildObjectInfo(_pressState.PointerDrag);

                FinishPointerUp(_pressState);
                if (dropTarget != null)
                {
                    ExecuteEvents.ExecuteHierarchy(dropTarget, data, ExecuteEvents.dropHandler);
                }

                if (_pressState.PointerDrag != null)
                {
                    ExecuteEvents.Execute(_pressState.PointerDrag, data, ExecuteEvents.endDragHandler);
                }

                ClearPointerState(_pressState);

                _result = CommandResult.Success(_requestId, new
                {
                    action = "drag",
                    source = BuildObjectInfo(_source),
                    destination = BuildObjectInfo(_destination),
                    startPosition = BuildVector2Info(_startPosition),
                    endPosition = BuildVector2Info(_endPosition),
                    startPositionMode = _startPositionMode,
                    endPositionMode = _endPositionMode,
                    frames = _frames,
                    dropTarget = BuildObjectInfo(dropTarget),
                    pointerDrag = pointerDragInfo
                });
            }

            private bool StepLongPress()
            {
                if (EditorApplication.timeSinceStartup < _releaseTime)
                {
                    return false;
                }

                var pointerPressInfo = BuildObjectInfo(_pressState.PointerPress);
                var clickHandlerInfo = BuildObjectInfo(_pressState.ClickHandler);

                FinishPointerUp(_pressState);
                ClearPointerState(_pressState);
                _result = CommandResult.Success(_requestId, new
                {
                    action = "long_press",
                    target = BuildObjectInfo(_source),
                    position = BuildVector2Info(_startPosition),
                    positionMode = _startPositionMode,
                    durationMs = _durationMs,
                    pointerPress = pointerPressInfo,
                    clickHandler = clickHandlerInfo
                });
                return true;
            }
        }
    }
}
