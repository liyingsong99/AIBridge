using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AIBridge.Runtime
{
    public partial class AIBridgeRuntime
    {
        private const int UiDefaultMaxResults = 100;
        private const int UiDefaultRaycastMaxResults = 20;

        private object BuildUiSnapshotData(AIBridgeRuntimeCommand cmd)
        {
            var includeDisabled = ReadBoolParam(cmd, "includeDisabled", false);
            var maxResults = Math.Max(1, ReadIntParam(cmd, "maxResults", UiDefaultMaxResults));
            var buttonCollection = CollectButtonSnapshots(null, includeDisabled, maxResults, true);
            var canvases = BuildCanvasSnapshots(buttonCollection.CountsByCanvasPath);
            var selected = GetSelectedGameObject();
            var eventSystem = BuildEventSystemSnapshot();

            return new Dictionary<string, object>
            {
                ["action"] = RuntimeUiSnapshotAction,
                ["targetId"] = _targetId,
                ["screen"] = BuildScreenSnapshot(),
                ["eventSystem"] = eventSystem,
                ["selected"] = BuildGameObjectInfo(selected),
                ["canvases"] = canvases,
                ["buttons"] = BuildButtonSnapshots(buttonCollection.Entries),
                ["summary"] = new Dictionary<string, object>
                {
                    ["canvasCount"] = canvases.Count,
                    ["buttonCount"] = buttonCollection.TotalCount,
                    ["returnedButtonCount"] = buttonCollection.Entries.Count,
                    ["clickableButtonCount"] = CountClickableButtons(buttonCollection.Entries),
                    ["truncated"] = buttonCollection.Truncated,
                    ["hasEventSystem"] = EventSystem.current != null
                }
            };
        }

        private object BuildUiFindData(AIBridgeRuntimeCommand cmd)
        {
            var keyword = ReadStringParam(cmd, "keyword", null);
            var includeDisabled = ReadBoolParam(cmd, "includeDisabled", false);
            var maxResults = Math.Max(1, ReadIntParam(cmd, "maxResults", UiDefaultMaxResults));
            var buttonCollection = CollectButtonSnapshots(keyword, includeDisabled, maxResults, true);

            return new Dictionary<string, object>
            {
                ["action"] = RuntimeUiFindAction,
                ["targetId"] = _targetId,
                ["keyword"] = keyword,
                ["buttons"] = BuildButtonSnapshots(buttonCollection.Entries),
                ["summary"] = new Dictionary<string, object>
                {
                    ["matchCount"] = buttonCollection.TotalCount,
                    ["returnedCount"] = buttonCollection.Entries.Count,
                    ["clickableCount"] = CountClickableButtons(buttonCollection.Entries),
                    ["truncated"] = buttonCollection.Truncated
                }
            };
        }

        private object BuildUiRaycastData(AIBridgeRuntimeCommand cmd)
        {
            var target = ResolveOptionalTarget(cmd);
            Vector2 screenPoint;
            string pointSource;
            string error;
            if (!TryResolveScreenPoint(cmd, target, out screenPoint, out pointSource, out error))
            {
                throw new ArgumentException(error);
            }

            List<RaycastResult> hits;
            if (!TryRaycast(screenPoint, out hits, out error))
            {
                throw new ArgumentException(error);
            }

            var maxResults = Math.Max(1, ReadIntParam(cmd, "maxResults", UiDefaultRaycastMaxResults));
            if (hits.Count > maxResults)
            {
                hits.RemoveRange(maxResults, hits.Count - maxResults);
            }

            return new Dictionary<string, object>
            {
                ["action"] = RuntimeUiRaycastAction,
                ["targetId"] = _targetId,
                ["point"] = BuildVector2Info(screenPoint),
                ["pointSource"] = pointSource,
                ["requestedTarget"] = BuildGameObjectInfo(target),
                ["hitCount"] = hits.Count,
                ["hits"] = BuildRaycastSnapshots(hits),
                ["topHit"] = hits.Count > 0 ? BuildRaycastSnapshot(hits[0], 0) : null
            };
        }

        private object BuildUiClickData(AIBridgeRuntimeCommand cmd)
        {
            var target = ResolveOptionalTarget(cmd);
            Vector2 screenPoint;
            string pointSource;
            string error;
            if (!TryResolveScreenPoint(cmd, target, out screenPoint, out pointSource, out error))
            {
                throw new ArgumentException(error);
            }

            var selectedBefore = GetSelectedGameObject();
            if (!TryClickAtScreenPoint(screenPoint, target, out var clickState, out error))
            {
                throw new ArgumentException(error);
            }

            var selectedAfter = GetSelectedGameObject();
            var hits = clickState.RaycastResults;
            return new Dictionary<string, object>
            {
                ["action"] = RuntimeUiClickAction,
                ["targetId"] = _targetId,
                ["point"] = BuildVector2Info(screenPoint),
                ["pointSource"] = pointSource,
                ["requestedTarget"] = BuildGameObjectInfo(target),
                ["selectedBefore"] = BuildGameObjectInfo(selectedBefore),
                ["selectedAfter"] = BuildGameObjectInfo(selectedAfter),
                ["hitCount"] = hits.Count,
                ["hits"] = BuildRaycastSnapshots(hits),
                ["raycastTarget"] = BuildGameObjectInfo(clickState.RaycastTarget),
                ["pointerPress"] = BuildGameObjectInfo(clickState.PointerPress),
                ["clickHandler"] = BuildGameObjectInfo(clickState.ClickHandler),
                ["clicked"] = clickState.PointerPress != null && clickState.ClickHandler != null && clickState.PointerPress == clickState.ClickHandler,
                ["topHit"] = hits.Count > 0 ? BuildRaycastSnapshot(hits[0], 0) : null
            };
        }

        private object BuildUiKeyData(AIBridgeRuntimeCommand cmd)
        {
            var key = ReadStringParam(cmd, "key", null);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Missing required parameter: --key");
            }

            var target = ResolveOptionalTarget(cmd);
            var selectedBefore = GetSelectedGameObject();
            var selectedTarget = target != null ? target : selectedBefore;
            if (selectedTarget == null && IsSelectionKey(key))
            {
                selectedTarget = FindFirstSelectableGameObject();
            }

            var selectionError = string.Empty;
            if (target != null && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(target);
            }

            var handledBy = HandleUiKey(key, selectedTarget, out selectionError);
            if (handledBy == null)
            {
                throw new ArgumentException(selectionError);
            }

            var selectedAfter = GetSelectedGameObject();
            return new Dictionary<string, object>
            {
                ["action"] = RuntimeInputKeyAction,
                ["targetId"] = _targetId,
                ["key"] = key,
                ["normalizedKey"] = NormalizeUiKey(key),
                ["requestedTarget"] = BuildGameObjectInfo(target),
                ["selectedBefore"] = BuildGameObjectInfo(selectedBefore),
                ["selectedAfter"] = BuildGameObjectInfo(selectedAfter),
                ["handledBy"] = handledBy
            };
        }

        private Dictionary<string, object> BuildScreenSnapshot()
        {
            var safeArea = Screen.safeArea;
            return new Dictionary<string, object>
            {
                ["width"] = Screen.width,
                ["height"] = Screen.height,
                ["dpi"] = Screen.dpi,
                ["orientation"] = Screen.orientation.ToString(),
                ["safeArea"] = BuildRectInfo(safeArea)
            };
        }

        private Dictionary<string, object> BuildEventSystemSnapshot()
        {
            var eventSystem = EventSystem.current;
            return new Dictionary<string, object>
            {
                ["present"] = eventSystem != null,
                ["selected"] = BuildGameObjectInfo(GetSelectedGameObject()),
                ["currentInputModule"] = eventSystem != null && eventSystem.currentInputModule != null
                    ? eventSystem.currentInputModule.GetType().FullName
                    : null
            };
        }

        private List<object> BuildCanvasSnapshots(Dictionary<string, int> buttonCountsByCanvasPath)
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            var entries = new List<UiCanvasSnapshotEntry>(canvases == null ? 0 : canvases.Length);

            if (canvases != null)
            {
                for (var i = 0; i < canvases.Length; i++)
                {
                    var canvas = canvases[i];
                    if (canvas == null || canvas.gameObject == null)
                    {
                        continue;
                    }

                    if (!canvas.gameObject.activeInHierarchy || !canvas.enabled)
                    {
                        continue;
                    }

                    var canvasPath = GetGameObjectPath(canvas.gameObject);
                    entries.Add(new UiCanvasSnapshotEntry
                    {
                        Canvas = canvas,
                        Path = canvasPath,
                        ButtonCount = buttonCountsByCanvasPath != null && buttonCountsByCanvasPath.TryGetValue(canvasPath, out var count) ? count : 0
                    });
                }
            }

            entries.Sort(CompareCanvasEntries);

            var result = new List<object>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                result.Add(BuildCanvasSnapshot(entries[i]));
            }

            return result;
        }

        private List<object> BuildButtonSnapshots(List<UiButtonSnapshotEntry> entries)
        {
            var result = new List<object>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                result.Add(BuildButtonSnapshot(entries[i]));
            }

            return result;
        }

        private List<object> BuildRaycastSnapshots(List<RaycastResult> hits)
        {
            var result = new List<object>(hits.Count);
            for (var i = 0; i < hits.Count; i++)
            {
                result.Add(BuildRaycastSnapshot(hits[i], i));
            }

            return result;
        }

        private Dictionary<string, object> BuildCanvasSnapshot(UiCanvasSnapshotEntry entry)
        {
            var canvas = entry.Canvas;
            var worldCamera = canvas != null && canvas.worldCamera != null ? canvas.worldCamera.gameObject : null;
            var result = new Dictionary<string, object>
            {
                ["name"] = canvas != null ? canvas.name : null,
                ["path"] = entry.Path,
                ["enabled"] = canvas != null && canvas.enabled,
                ["activeInHierarchy"] = canvas != null && canvas.gameObject.activeInHierarchy,
                ["isRootCanvas"] = canvas != null && canvas.isRootCanvas,
                ["renderMode"] = canvas != null ? canvas.renderMode.ToString() : null,
                ["sortingLayerId"] = canvas != null ? canvas.sortingLayerID : 0,
                ["sortingLayerName"] = canvas != null ? SortingLayer.IDToName(canvas.sortingLayerID) : null,
                ["sortingOrder"] = canvas != null ? canvas.sortingOrder : 0,
                ["overrideSorting"] = canvas != null && canvas.overrideSorting,
                ["scaleFactor"] = canvas != null ? canvas.scaleFactor : 0f,
                ["referencePixelsPerUnit"] = canvas != null ? canvas.referencePixelsPerUnit : 0f,
                ["pixelRect"] = canvas != null ? BuildRectInfo(canvas.pixelRect) : null,
                ["worldCamera"] = BuildGameObjectInfo(worldCamera),
                ["buttonCount"] = entry.ButtonCount,
                ["hasGraphicRaycaster"] = canvas != null && canvas.GetComponent<GraphicRaycaster>() != null
            };

            AIBridgeObjectIdentity.AddSerializedId(result, canvas != null ? canvas.gameObject : null);
            return result;
        }

        private Dictionary<string, object> BuildButtonSnapshot(UiButtonSnapshotEntry entry)
        {
            var button = entry.Button;
            var result = new Dictionary<string, object>
            {
                ["name"] = button != null ? button.name : null,
                ["label"] = entry.Label,
                ["path"] = entry.Path,
                ["activeInHierarchy"] = button != null && button.gameObject.activeInHierarchy,
                ["enabled"] = button != null && button.enabled,
                ["interactable"] = button != null && button.IsInteractable(),
                ["clickable"] = entry.Clickable,
                ["screenPointAvailable"] = entry.ScreenPointAvailable,
                ["clickPoint"] = entry.ScreenPointAvailable ? BuildVector2Info(entry.ScreenPoint) : null,
                ["screenRect"] = entry.ScreenRectAvailable ? BuildRectInfo(entry.ScreenRect) : null,
                ["raycastAvailable"] = entry.RaycastAvailable,
                ["topmost"] = entry.RaycastAvailable && entry.RaycastIndex == 0,
                ["raycastIndex"] = entry.RaycastIndex,
                ["raycastCount"] = entry.RaycastCount,
                ["raycastTarget"] = BuildGameObjectInfo(entry.RaycastTarget),
                ["canvasPath"] = entry.CanvasPath,
                ["canvasName"] = entry.CanvasName,
                ["canvasSortingOrder"] = entry.CanvasSortingOrder,
                ["canvasRenderMode"] = entry.CanvasRenderMode
            };

            AIBridgeObjectIdentity.AddSerializedId(result, button != null ? button.gameObject : null);
            return result;
        }

        private Dictionary<string, object> BuildRaycastSnapshot(RaycastResult hit, int index)
        {
            var moduleName = hit.module != null ? hit.module.GetType().FullName : null;
            var moduleGameObject = hit.module != null ? hit.module.gameObject : null;
            return new Dictionary<string, object>
            {
                ["index"] = index,
                ["gameObject"] = BuildGameObjectInfo(hit.gameObject),
                ["module"] = moduleName,
                ["moduleObject"] = BuildGameObjectInfo(moduleGameObject),
                ["distance"] = hit.distance,
                ["depth"] = hit.depth,
                ["sortingLayer"] = hit.sortingLayer,
                ["sortingOrder"] = hit.sortingOrder,
                ["displayIndex"] = hit.displayIndex,
                ["screenPosition"] = BuildVector2Info(hit.screenPosition),
                ["worldPosition"] = BuildVector3Info(hit.worldPosition),
                ["worldNormal"] = BuildVector3Info(hit.worldNormal),
                ["isTopmost"] = index == 0
            };
        }

        private UiButtonSnapshotCollection CollectButtonSnapshots(string keyword, bool includeDisabled, int maxResults, bool includeRaycastDetails)
        {
            var buttons = UnityEngine.Object.FindObjectsOfType<Button>();
            var result = new UiButtonSnapshotCollection();
            if (buttons == null || buttons.Length == 0)
            {
                return result;
            }

            var raycastEnabled = includeRaycastDetails && EventSystem.current != null;
            var canvasPathCount = result.CountsByCanvasPath;
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null || button.gameObject == null)
                {
                    continue;
                }

                if (!button.gameObject.activeInHierarchy || !button.enabled)
                {
                    if (!includeDisabled)
                    {
                        continue;
                    }
                }

                if (!includeDisabled && !button.IsInteractable())
                {
                    continue;
                }

                var entry = CreateButtonSnapshotEntry(button);
                if (!MatchesButtonKeyword(entry, keyword))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.CanvasPath))
                {
                    if (canvasPathCount.ContainsKey(entry.CanvasPath))
                    {
                        canvasPathCount[entry.CanvasPath] = canvasPathCount[entry.CanvasPath] + 1;
                    }
                    else
                    {
                        canvasPathCount.Add(entry.CanvasPath, 1);
                    }
                }

                if (raycastEnabled && entry.ScreenPointAvailable)
                {
                    List<RaycastResult> hits;
                    string error;
                    if (TryRaycast(entry.ScreenPoint, out hits, out error))
                    {
                        entry.RaycastAvailable = true;
                        entry.RaycastCount = hits.Count;
                        entry.RaycastIndex = IndexOfGameObjectInRaycast(button.gameObject, hits);
                        if (hits.Count > 0)
                        {
                            entry.RaycastTarget = hits[0].gameObject;
                        }
                    }
                }

                entry.Clickable = button.IsInteractable()
                    && entry.ScreenPointAvailable
                    && (!entry.RaycastAvailable || entry.RaycastIndex == 0);
                result.Entries.Add(entry);
                result.TotalCount++;
            }

            result.Entries.Sort(CompareButtonEntries);
            if (result.Entries.Count > maxResults)
            {
                result.Truncated = true;
                result.Entries.RemoveRange(maxResults, result.Entries.Count - maxResults);
            }

            return result;
        }

        private UiButtonSnapshotEntry CreateButtonSnapshotEntry(Button button)
        {
            var entry = new UiButtonSnapshotEntry
            {
                Button = button,
                Path = GetGameObjectPath(button.gameObject),
                Label = ExtractButtonLabel(button),
                CanvasPath = null,
                CanvasName = null,
                CanvasSortingOrder = 0,
                CanvasRenderMode = null,
                Clickable = false,
                ScreenPointAvailable = false,
                ScreenRectAvailable = false,
                RaycastIndex = -1,
                RaycastCount = 0
            };

            var canvas = button.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                entry.Canvas = canvas;
                entry.CanvasPath = GetGameObjectPath(canvas.gameObject);
                entry.CanvasName = canvas.name;
                entry.CanvasSortingOrder = canvas.sortingOrder;
                entry.CanvasRenderMode = canvas.renderMode.ToString();
            }

            Rect screenRect;
            string error;
            if (TryGetScreenMetrics(button.transform as RectTransform, out var screenPoint, out screenRect, out error))
            {
                entry.ScreenPointAvailable = true;
                entry.ScreenPoint = screenPoint;
                entry.ScreenRect = screenRect;
                entry.ScreenRectAvailable = true;
            }

            entry.Clickable = button.IsInteractable() && entry.ScreenPointAvailable;
            return entry;
        }

        private static bool MatchesButtonKeyword(UiButtonSnapshotEntry entry, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return ContainsText(entry.Path, keyword)
                || ContainsText(entry.Label, keyword)
                || ContainsText(entry.Button != null ? entry.Button.name : null, keyword)
                || ContainsText(entry.CanvasPath, keyword)
                || ContainsText(entry.CanvasName, keyword);
        }

        private static bool ContainsText(string value, string keyword)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(keyword)
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CountClickableButtons(List<UiButtonSnapshotEntry> entries)
        {
            var count = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Clickable)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CompareButtonEntries(UiButtonSnapshotEntry left, UiButtonSnapshotEntry right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var clickableCompare = right.Clickable.CompareTo(left.Clickable);
            if (clickableCompare != 0)
            {
                return clickableCompare;
            }

            var leftOrder = left.Canvas != null ? left.Canvas.sortingOrder : 0;
            var rightOrder = right.Canvas != null ? right.Canvas.sortingOrder : 0;
            var orderCompare = rightOrder.CompareTo(leftOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            var leftHasPoint = left.ScreenPointAvailable;
            var rightHasPoint = right.ScreenPointAvailable;
            if (leftHasPoint != rightHasPoint)
            {
                return rightHasPoint.CompareTo(leftHasPoint);
            }

            if (leftHasPoint)
            {
                var yCompare = right.ScreenPoint.y.CompareTo(left.ScreenPoint.y);
                if (yCompare != 0)
                {
                    return yCompare;
                }

                var xCompare = left.ScreenPoint.x.CompareTo(right.ScreenPoint.x);
                if (xCompare != 0)
                {
                    return xCompare;
                }
            }

            return string.CompareOrdinal(left.Path, right.Path);
        }

        private static int CompareCanvasEntries(UiCanvasSnapshotEntry left, UiCanvasSnapshotEntry right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var leftOrder = left.Canvas != null ? left.Canvas.sortingOrder : 0;
            var rightOrder = right.Canvas != null ? right.Canvas.sortingOrder : 0;
            var orderCompare = rightOrder.CompareTo(leftOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            return string.CompareOrdinal(left.Path, right.Path);
        }

        private static Dictionary<string, object> BuildGameObjectInfo(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var result = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = GetGameObjectPath(go),
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["layer"] = go.layer,
                ["tag"] = go.tag
            };

            AIBridgeObjectIdentity.AddSerializedId(result, go);
            return result;
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

        private static Dictionary<string, object> BuildRectInfo(Rect rect)
        {
            return new Dictionary<string, object>
            {
                ["x"] = rect.x,
                ["y"] = rect.y,
                ["width"] = rect.width,
                ["height"] = rect.height,
                ["xMin"] = rect.xMin,
                ["yMin"] = rect.yMin,
                ["xMax"] = rect.xMax,
                ["yMax"] = rect.yMax,
                ["center"] = BuildVector2Info(rect.center)
            };
        }

        private static Dictionary<string, object> BuildVector2Info(Vector2 value)
        {
            return new Dictionary<string, object>
            {
                ["x"] = value.x,
                ["y"] = value.y
            };
        }

        private static Dictionary<string, object> BuildVector3Info(Vector3 value)
        {
            return new Dictionary<string, object>
            {
                ["x"] = value.x,
                ["y"] = value.y,
                ["z"] = value.z
            };
        }

        private static GameObject GetSelectedGameObject()
        {
            return EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        }

        private static GameObject ResolveOptionalTarget(AIBridgeRuntimeCommand cmd)
        {
            var path = ReadStringParam(cmd, "path", null);
            if (!string.IsNullOrEmpty(path))
            {
                return GameObject.Find(path);
            }

            var serializedId = ReadSerializedObjectIdParam(cmd, "entityId", "instanceId");
            if (AIBridgeObjectIdentity.HasSerializedId(serializedId))
            {
                return FindGameObjectBySerializedId(serializedId);
            }

            return null;
        }

        private static bool TryResolveScreenPoint(AIBridgeRuntimeCommand cmd, GameObject target, out Vector2 screenPoint, out string pointSource, out string error)
        {
            screenPoint = Vector2.zero;
            pointSource = null;
            error = null;

            var x = ReadNullableFloatParam(cmd, "x");
            var y = ReadNullableFloatParam(cmd, "y");
            if (x.HasValue || y.HasValue)
            {
                if (!x.HasValue || !y.HasValue)
                {
                    error = "Provide both --x and --y for explicit screen coordinates.";
                    return false;
                }

                screenPoint = new Vector2(x.Value, y.Value);
                pointSource = "screen";
                return true;
            }

            if (target == null)
            {
                error = "Missing target or screen point. Provide --path/--instanceId or --x/--y.";
                return false;
            }

            Rect screenRect;
            if (TryGetScreenMetrics(target.transform as RectTransform, out screenPoint, out screenRect, out error))
            {
                pointSource = "target";
                return true;
            }

            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                if (TryWorldToScreenPoint(renderer.bounds.center, out screenPoint, out error))
                {
                    pointSource = "renderer";
                    return true;
                }
            }

            var collider = target.GetComponentInChildren<Collider>();
            if (collider != null)
            {
                if (TryWorldToScreenPoint(collider.bounds.center, out screenPoint, out error))
                {
                    pointSource = "collider";
                    return true;
                }
            }

            if (TryWorldToScreenPoint(target.transform.position, out screenPoint, out error))
            {
                pointSource = "transform";
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                error = "Unable to project the target into screen coordinates.";
            }

            return false;
        }

        private static bool TryGetScreenMetrics(RectTransform rectTransform, out Vector2 screenPoint, out Rect screenRect, out string error)
        {
            screenPoint = Vector2.zero;
            screenRect = new Rect();
            error = null;

            if (rectTransform == null)
            {
                error = "Missing RectTransform.";
                return false;
            }

            var canvas = rectTransform.GetComponentInParent<Canvas>();
            var camera = GetCanvasCamera(canvas);
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null)
            {
                error = "Unable to resolve a camera for world-space UI.";
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;

            for (var i = 0; i < corners.Length; i++)
            {
                var projected = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                if (projected.x < minX)
                {
                    minX = projected.x;
                }

                if (projected.y < minY)
                {
                    minY = projected.y;
                }

                if (projected.x > maxX)
                {
                    maxX = projected.x;
                }

                if (projected.y > maxY)
                {
                    maxY = projected.y;
                }
            }

            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            screenPoint = screenRect.center;
            return true;
        }

        private static bool TryWorldToScreenPoint(Vector3 worldPosition, out Vector2 screenPoint, out string error)
        {
            screenPoint = Vector2.zero;
            error = null;

            var camera = Camera.main;
            if (camera == null)
            {
                error = "Camera.main is required to project non-UI targets.";
                return false;
            }

            var projected = camera.WorldToScreenPoint(worldPosition);
            if (projected.z < 0f)
            {
                error = "Target is behind Camera.main and cannot be projected.";
                return false;
            }

            screenPoint = new Vector2(projected.x, projected.y);
            return true;
        }

        private static bool TryRaycast(Vector2 screenPoint, out List<RaycastResult> hits, out string error)
        {
            hits = new List<RaycastResult>();
            error = null;

            if (EventSystem.current == null)
            {
                error = "runtime.ui.raycast requires an active EventSystem.";
                return false;
            }

            var data = CreatePointerEventData(screenPoint);
            EventSystem.current.RaycastAll(data, hits);
            return true;
        }

        private static bool TryClickAtScreenPoint(Vector2 screenPoint, GameObject fallbackTarget, out UiPointerPressState state, out string error)
        {
            state = null;
            error = null;

            List<RaycastResult> hits;
            if (!TryRaycast(screenPoint, out hits, out error))
            {
                return false;
            }

            var raycastTarget = GetFirstRaycastTarget(hits);
            var eventTarget = raycastTarget != null ? raycastTarget : fallbackTarget;
            if (eventTarget == null)
            {
                error = "No EventSystem raycast target found at the requested screen position.";
                return false;
            }

            var data = CreatePointerEventData(screenPoint);
            if (hits.Count > 0)
            {
                data.pointerCurrentRaycast = hits[0];
                data.pointerPressRaycast = hits[0];
            }

            data.rawPointerPress = eventTarget;
            data.pressPosition = screenPoint;
            data.position = screenPoint;
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

            var pointerPress = ExecuteEvents.ExecuteHierarchy(eventTarget, data, ExecuteEvents.pointerDownHandler);
            if (pointerPress == null)
            {
                pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            }

            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            if (pointerPress == null && clickHandler == null)
            {
                error = "No pointer click handler found for the target.";
                return false;
            }

            data.pointerPress = pointerPress;
            state = new UiPointerPressState
            {
                Data = data,
                EventTarget = eventTarget,
                RaycastTarget = raycastTarget,
                PointerPress = pointerPress,
                ClickHandler = clickHandler,
                RaycastResults = hits,
                ScreenPoint = screenPoint
            };

            FinishUiClick(state);
            return true;
        }

        private static void FinishUiClick(UiPointerPressState state)
        {
            if (state == null || state.Data == null)
            {
                return;
            }

            if (state.PointerPress != null)
            {
                ExecuteEvents.Execute(state.PointerPress, state.Data, ExecuteEvents.pointerUpHandler);
            }

            if (state.PointerPress != null
                && state.ClickHandler != null
                && state.PointerPress == state.ClickHandler
                && state.Data.eligibleForClick)
            {
                ExecuteEvents.Execute(state.PointerPress, state.Data, ExecuteEvents.pointerClickHandler);
            }

            state.Data.eligibleForClick = false;
            state.Data.pointerPress = null;
            state.Data.rawPointerPress = null;
            state.Data.dragging = false;
        }

        private static PointerEventData CreatePointerEventData(Vector2 screenPoint)
        {
            return new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                position = screenPoint,
                delta = Vector2.zero
            };
        }

        private static GameObject GetFirstRaycastTarget(List<RaycastResult> hits)
        {
            if (hits == null)
            {
                return null;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject != null)
                {
                    return hits[i].gameObject;
                }
            }

            return null;
        }

        private static int IndexOfGameObjectInRaycast(GameObject target, List<RaycastResult> hits)
        {
            if (target == null || hits == null)
            {
                return -1;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject == target)
                {
                    return i;
                }
            }

            return -1;
        }

        private static Camera GetCanvasCamera(Canvas canvas)
        {
            if (canvas == null)
            {
                return Camera.main;
            }

            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }

            if (canvas.worldCamera != null)
            {
                return canvas.worldCamera;
            }

            return Camera.main;
        }

        private static string ExtractButtonLabel(Button button)
        {
            if (button == null)
            {
                return null;
            }

            var texts = button.GetComponentsInChildren<Text>(true);
            if (texts == null)
            {
                return button.name;
            }

            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && !string.IsNullOrWhiteSpace(texts[i].text))
                {
                    return texts[i].text.Trim();
                }
            }

            return button.name;
        }

        private static string NormalizeUiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return key.Trim().Replace("-", "_").ToLowerInvariant();
        }

        private static bool IsSelectionKey(string key)
        {
            var normalized = NormalizeUiKey(key);
            return string.Equals(normalized, "tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "shift_tab", StringComparison.OrdinalIgnoreCase);
        }

        private static string HandleUiKey(string key, GameObject selectedTarget, out string error)
        {
            error = null;
            var normalized = NormalizeUiKey(key);
            if (EventSystem.current == null)
            {
                error = "runtime.input.key requires an active EventSystem.";
                return null;
            }

            if (selectedTarget != null && EventSystem.current.currentSelectedGameObject != selectedTarget)
            {
                EventSystem.current.SetSelectedGameObject(selectedTarget);
            }

            var currentSelected = GetSelectedGameObject();
            if (string.Equals(normalized, "submit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "enter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "space", StringComparison.OrdinalIgnoreCase))
            {
                if (currentSelected == null)
                {
                    error = "No selected GameObject to submit.";
                    return null;
                }

                var submitData = new BaseEventData(EventSystem.current);
                if (ExecuteEvents.ExecuteHierarchy(currentSelected, submitData, ExecuteEvents.submitHandler))
                {
                    return "submit";
                }

                error = "The selected GameObject does not handle submit.";
                return null;
            }

            if (string.Equals(normalized, "cancel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "escape", StringComparison.OrdinalIgnoreCase))
            {
                if (currentSelected == null)
                {
                    error = "No selected GameObject to cancel.";
                    return null;
                }

                var cancelData = new BaseEventData(EventSystem.current);
                if (ExecuteEvents.ExecuteHierarchy(currentSelected, cancelData, ExecuteEvents.cancelHandler))
                {
                    return "cancel";
                }

                error = "The selected GameObject does not handle cancel.";
                return null;
            }

            if (string.Equals(normalized, "tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "shift_tab", StringComparison.OrdinalIgnoreCase))
            {
                var next = GetNextSelectableForTab(currentSelected, string.Equals(normalized, "shift_tab", StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    error = "No selectable GameObject is available for tab navigation.";
                    return null;
                }

                EventSystem.current.SetSelectedGameObject(next);
                return string.Equals(normalized, "shift_tab", StringComparison.OrdinalIgnoreCase) ? "shift_tab" : "tab";
            }

            MoveDirection direction;
            if (TryParseMoveDirection(normalized, out direction))
            {
                if (currentSelected == null)
                {
                    var fallback = FindFirstSelectableGameObject();
                    if (fallback == null)
                    {
                        error = "No selectable GameObject is available for move navigation.";
                        return null;
                    }

                    EventSystem.current.SetSelectedGameObject(fallback);
                    currentSelected = fallback;
                }

                var axisData = new AxisEventData(EventSystem.current)
                {
                    moveDir = direction,
                    moveVector = GetMoveVector(direction)
                };

                if (ExecuteEvents.ExecuteHierarchy(currentSelected, axisData, ExecuteEvents.moveHandler))
                {
                    return normalized;
                }

                error = "The selected GameObject does not handle move navigation.";
                return null;
            }

            error = "Unsupported key. Supported keys: submit, cancel, enter, escape, space, tab, shift_tab, up, down, left, right.";
            return null;
        }

        private static bool TryParseMoveDirection(string key, out MoveDirection direction)
        {
            direction = MoveDirection.None;
            if (string.Equals(key, "up", StringComparison.OrdinalIgnoreCase))
            {
                direction = MoveDirection.Up;
                return true;
            }

            if (string.Equals(key, "down", StringComparison.OrdinalIgnoreCase))
            {
                direction = MoveDirection.Down;
                return true;
            }

            if (string.Equals(key, "left", StringComparison.OrdinalIgnoreCase))
            {
                direction = MoveDirection.Left;
                return true;
            }

            if (string.Equals(key, "right", StringComparison.OrdinalIgnoreCase))
            {
                direction = MoveDirection.Right;
                return true;
            }

            return false;
        }

        private static Vector2 GetMoveVector(MoveDirection direction)
        {
            switch (direction)
            {
                case MoveDirection.Up:
                    return Vector2.up;
                case MoveDirection.Down:
                    return Vector2.down;
                case MoveDirection.Left:
                    return Vector2.left;
                case MoveDirection.Right:
                    return Vector2.right;
                default:
                    return Vector2.zero;
            }
        }

        private static GameObject GetNextSelectableForTab(GameObject currentSelected, bool reverse)
        {
            var selectables = Selectable.allSelectablesArray;
            if (selectables == null || selectables.Length == 0)
            {
                return null;
            }

            var candidates = new List<Selectable>(selectables.Length);
            for (var i = 0; i < selectables.Length; i++)
            {
                var selectable = selectables[i];
                if (selectable == null || selectable.gameObject == null)
                {
                    continue;
                }

                if (!selectable.gameObject.activeInHierarchy || !selectable.enabled || !selectable.IsInteractable())
                {
                    continue;
                }

                candidates.Add(selectable);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var currentSelectable = currentSelected != null ? currentSelected.GetComponentInParent<Selectable>() : null;
            var currentIndex = -1;
            if (currentSelectable != null)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i] == currentSelectable)
                    {
                        currentIndex = i;
                        break;
                    }
                }
            }

            if (currentIndex < 0)
            {
                return reverse ? candidates[candidates.Count - 1].gameObject : candidates[0].gameObject;
            }

            if (reverse)
            {
                currentIndex--;
                if (currentIndex < 0)
                {
                    currentIndex = candidates.Count - 1;
                }
            }
            else
            {
                currentIndex++;
                if (currentIndex >= candidates.Count)
                {
                    currentIndex = 0;
                }
            }

            return candidates[currentIndex].gameObject;
        }

        private static GameObject FindFirstSelectableGameObject()
        {
            var selectables = Selectable.allSelectablesArray;
            if (selectables == null)
            {
                return null;
            }

            for (var i = 0; i < selectables.Length; i++)
            {
                var selectable = selectables[i];
                if (selectable != null
                    && selectable.gameObject != null
                    && selectable.gameObject.activeInHierarchy
                    && selectable.enabled
                    && selectable.IsInteractable())
                {
                    return selectable.gameObject;
                }
            }

            return null;
        }

        private static GameObject FindGameObjectBySerializedId(object serializedId)
        {
            var gameObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            if (gameObjects == null)
            {
                return null;
            }

            for (var i = 0; i < gameObjects.Length; i++)
            {
                var candidate = gameObjects[i];
                if (AIBridgeObjectIdentity.MatchesSerializedId(candidate, serializedId))
                {
                    return candidate;
                }
            }

            return null;
        }

        private sealed class UiButtonSnapshotCollection
        {
            public readonly List<UiButtonSnapshotEntry> Entries = new List<UiButtonSnapshotEntry>();
            public readonly Dictionary<string, int> CountsByCanvasPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int TotalCount;
            public bool Truncated;
        }

        private sealed class UiButtonSnapshotEntry
        {
            public Button Button;
            public Canvas Canvas;
            public string Path;
            public string Label;
            public string CanvasPath;
            public string CanvasName;
            public string CanvasRenderMode;
            public int CanvasSortingOrder;
            public bool ScreenPointAvailable;
            public Vector2 ScreenPoint;
            public Rect ScreenRect;
            public bool ScreenRectAvailable;
            public bool RaycastAvailable;
            public int RaycastIndex = -1;
            public int RaycastCount;
            public GameObject RaycastTarget;
            public bool Clickable;
        }

        private sealed class UiCanvasSnapshotEntry
        {
            public Canvas Canvas;
            public string Path;
            public int ButtonCount;
        }

        private sealed class UiPointerPressState
        {
            public PointerEventData Data;
            public GameObject EventTarget;
            public GameObject RaycastTarget;
            public GameObject PointerPress;
            public GameObject ClickHandler;
            public List<RaycastResult> RaycastResults;
            public Vector2 ScreenPoint;
        }
    }
}
