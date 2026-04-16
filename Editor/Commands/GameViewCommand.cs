using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Game view command: manage Game window resolution (get, set, list).
    /// Uses reflection to access Unity's internal GameView and GameViewSizes APIs.
    /// </summary>
    public class GameViewCommand : ICommand
    {
        public string Type => "gameview";
        public bool RequiresRefresh => false;

        public string SkillDescription => @"### `gameview` - Game View Resolution

```bash
$CLI gameview get_resolution
$CLI gameview set_resolution --width 1920 --height 1080
$CLI gameview list_resolutions
```

**Parameters for `set_resolution`:**

| Parameter | Range | Required | Description |
|-----------|-------|----------|-------------|
| `--width` | 1-8192 | Yes | Resolution width in pixels |
| `--height` | 1-8192 | Yes | Resolution height in pixels |";

        // Cached reflection handles
        private static bool _reflectionInitialized;
        private static string _reflectionError;

        private static Type _gameViewType;
        private static Type _gameViewSizesType;
        private static Type _gameViewSizeType;
        private static Type _gameViewSizeGroupType;
        private static Type _gameViewSizeTypeEnum;

        private static MethodInfo _getMainGameView;
        private static PropertyInfo _selectedSizeIndex;
        private static PropertyInfo _sizesInstance;
        private static PropertyInfo _currentGroupType;
        private static MethodInfo _getGroup;
        private static MethodInfo _getTotalCount;
        private static MethodInfo _getGameViewSize;
        private static MethodInfo _addCustomSize;
        private static MethodInfo _saveToHardDisk;

        private static PropertyInfo _sizeWidth;
        private static PropertyInfo _sizeHeight;
        private static PropertyInfo _sizeBaseText;
        private static PropertyInfo _sizeSizeType;

        private static object _fixedResolutionValue;

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get_resolution");

            try
            {
                if (!TryInitReflection(out var error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                switch (action.ToLower())
                {
                    case "get_resolution":
                        return GetResolution(request);
                    case "set_resolution":
                        return SetResolution(request);
                    case "list_resolutions":
                        return ListResolutions(request);
                    default:
                        return CommandResult.Failure(request.id,
                            $"Unknown action: {action}. Supported: get_resolution, set_resolution, list_resolutions");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private bool TryInitReflection(out string error)
        {
            error = null;

            if (_reflectionInitialized)
            {
                error = _reflectionError;
                return _reflectionError == null;
            }

            _reflectionInitialized = true;

            try
            {
                _gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
                _gameViewSizesType = System.Type.GetType("UnityEditor.GameViewSizes,UnityEditor");
                _gameViewSizeType = System.Type.GetType("UnityEditor.GameViewSize,UnityEditor");
                _gameViewSizeGroupType = System.Type.GetType("UnityEditor.GameViewSizeGroup,UnityEditor");
                _gameViewSizeTypeEnum = System.Type.GetType("UnityEditor.GameViewSizeType,UnityEditor");

                if (_gameViewType == null || _gameViewSizesType == null ||
                    _gameViewSizeType == null || _gameViewSizeGroupType == null ||
                    _gameViewSizeTypeEnum == null)
                {
                    _reflectionError = "GameView internals not available in this Unity version.";
                    error = _reflectionError;
                    return false;
                }

                var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

                // GameView
                _getMainGameView = _gameViewType.GetMethod("GetMainGameView", allFlags)
                    ?? _gameViewType.GetMethod("GetMainPlayModeView", allFlags);
                _selectedSizeIndex = _gameViewType.GetProperty("selectedSizeIndex", allFlags);

                // GameViewSizes - Unity 6000.x uses ScriptableSingleton, check base type too
                _sizesInstance = _gameViewSizesType.GetProperty("instance", allFlags);
                if (_sizesInstance == null)
                {
                    // ScriptableSingleton<T>.instance is on the base generic type
                    var baseType = _gameViewSizesType.BaseType;
                    while (baseType != null && _sizesInstance == null)
                    {
                        _sizesInstance = baseType.GetProperty("instance", allFlags);
                        baseType = baseType.BaseType;
                    }
                }
                _currentGroupType = _gameViewSizesType.GetProperty("currentGroupType", allFlags);
                _getGroup = _gameViewSizesType.GetMethod("GetGroup", allFlags);
                // Unity 6000.x: direct currentGroup property
                _currentGroupProp = _gameViewSizesType.GetProperty("currentGroup", allFlags);
                _saveToHardDisk = _gameViewSizesType.GetMethod("SaveToHDD",
                    BindingFlags.Public | BindingFlags.Instance);

                // GameViewSizeGroup
                _getTotalCount = _gameViewSizeGroupType.GetMethod("GetTotalCount",
                    BindingFlags.Public | BindingFlags.Instance);
                _getGameViewSize = _gameViewSizeGroupType.GetMethod("GetGameViewSize",
                    BindingFlags.Public | BindingFlags.Instance);
                _addCustomSize = _gameViewSizeGroupType.GetMethod("AddCustomSize",
                    BindingFlags.Public | BindingFlags.Instance);

                // GameViewSize properties
                _sizeWidth = _gameViewSizeType.GetProperty("width",
                    BindingFlags.Public | BindingFlags.Instance);
                _sizeHeight = _gameViewSizeType.GetProperty("height",
                    BindingFlags.Public | BindingFlags.Instance);
                _sizeBaseText = _gameViewSizeType.GetProperty("baseText",
                    BindingFlags.Public | BindingFlags.Instance);
                _sizeSizeType = _gameViewSizeType.GetProperty("sizeType",
                    BindingFlags.Public | BindingFlags.Instance);

                // FixedResolution enum value
                _fixedResolutionValue = Enum.Parse(_gameViewSizeTypeEnum, "FixedResolution");

                // Validate critical members
                var missing = new List<string>();
                if (_sizesInstance == null) missing.Add("GameViewSizes.instance");
                if (_getGroup == null && _currentGroupProp == null) missing.Add("GameViewSizes.GetGroup/currentGroup");
                if (_getTotalCount == null) missing.Add("GameViewSizeGroup.GetTotalCount");
                if (_getGameViewSize == null) missing.Add("GameViewSizeGroup.GetGameViewSize");
                if (_sizeWidth == null) missing.Add("GameViewSize.width");
                if (_sizeHeight == null) missing.Add("GameViewSize.height");
                if (_selectedSizeIndex == null) missing.Add("GameView.selectedSizeIndex");
                if (missing.Count > 0)
                {
                    _reflectionError = $"Missing GameView internal members: {string.Join(", ", missing)}. This Unity version may not be supported.";
                    error = _reflectionError;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _reflectionError = $"Failed to initialize GameView reflection: {ex.Message}";
                error = _reflectionError;
                return false;
            }
        }

        private object GetSizesInstance()
        {
            return _sizesInstance.GetValue(null);
        }

        private object GetCurrentSizeGroup()
        {
            var instance = GetSizesInstance();
            if (instance == null) return null;

            // Unity 6000.x: direct currentGroup property
            if (_currentGroupProp != null)
                return _currentGroupProp.GetValue(instance);

            // Older Unity: GetGroup(currentGroupType)
            if (_currentGroupType != null && _getGroup != null)
            {
                var groupType = _currentGroupType.GetValue(null);
                return _getGroup.Invoke(instance, new[] { (object)(int)groupType });
            }

            return null;
        }

        private static PropertyInfo _currentGroupProp; // Unity 6000.x: GameViewSizes.currentGroup

        private object GetMainGameView()
        {
            if (_getMainGameView != null)
                return _getMainGameView.Invoke(null, null);
            // Fallback for Unity 6000.x: find via Resources
            var views = Resources.FindObjectsOfTypeAll(_gameViewType);
            return views != null && views.Length > 0 ? views[0] : null;
        }

        private int GetSelectedSizeIndex(object gameView)
        {
            if (_selectedSizeIndex == null || gameView == null) return -1;
            return (int)_selectedSizeIndex.GetValue(gameView);
        }

        private void SetSelectedSizeIndex(object gameView, int index)
        {
            if (_selectedSizeIndex != null && gameView != null)
            {
                _selectedSizeIndex.SetValue(gameView, index);
            }
        }

        private int GetTotalCount(object group)
        {
            return (int)_getTotalCount.Invoke(group, null);
        }

        private object GetGameViewSize(object group, int index)
        {
            return _getGameViewSize.Invoke(group, new object[] { index });
        }

        private int GetSizeWidth(object size)
        {
            return (int)_sizeWidth.GetValue(size);
        }

        private int GetSizeHeight(object size)
        {
            return (int)_sizeHeight.GetValue(size);
        }

        private string GetSizeBaseText(object size)
        {
            return _sizeBaseText != null ? (string)_sizeBaseText.GetValue(size) : "";
        }

        private string GetSizeType(object size)
        {
            if (_sizeSizeType == null) return "Unknown";
            var val = _sizeSizeType.GetValue(size);
            return val.ToString();
        }

        private CommandResult GetResolution(CommandRequest request)
        {
            var gameView = GetMainGameView();
            var group = GetCurrentSizeGroup();

            if (group == null)
            {
                return CommandResult.Failure(request.id,
                    "Could not access Game view size group.");
            }

            int selectedIndex = gameView != null ? GetSelectedSizeIndex(gameView) : -1;
            int width = 0;
            int height = 0;
            string name = "";
            string sizeType = "";

            if (selectedIndex >= 0 && selectedIndex < GetTotalCount(group))
            {
                var size = GetGameViewSize(group, selectedIndex);
                width = GetSizeWidth(size);
                height = GetSizeHeight(size);
                name = GetSizeBaseText(size);
                sizeType = GetSizeType(size);
            }
            else if (gameView != null)
            {
                // Fallback: read targetSize
                var targetSizeProp = _gameViewType.GetProperty("targetSize",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (targetSizeProp != null)
                {
                    var targetSize = (Vector2)targetSizeProp.GetValue(gameView);
                    width = (int)targetSize.x;
                    height = (int)targetSize.y;
                    name = "Unknown";
                    sizeType = "Unknown";
                }
            }

            if (width <= 0 || height <= 0)
            {
                return CommandResult.Failure(request.id,
                    "No Game view window found. Open the Game view in the Unity Editor first.");
            }

            return CommandResult.Success(request.id, new
            {
                action = "get_resolution",
                width,
                height,
                name,
                selectedIndex,
                sizeType
            });
        }

        private CommandResult SetResolution(CommandRequest request)
        {
            int width = request.GetParam("width", 0);
            int height = request.GetParam("height", 0);

            if (width <= 0 || height <= 0)
            {
                return CommandResult.Failure(request.id,
                    "Parameters 'width' and 'height' are required and must be positive integers.");
            }

            if (width > 8192 || height > 8192)
            {
                return CommandResult.Failure(request.id,
                    $"Resolution {width}x{height} exceeds maximum allowed (8192x8192).");
            }

            var gameView = GetMainGameView();
            if (gameView == null)
            {
                return CommandResult.Failure(request.id,
                    "No Game view window found. Open the Game view in the Unity Editor first.");
            }

            var group = GetCurrentSizeGroup();
            if (group == null)
            {
                return CommandResult.Failure(request.id,
                    "Could not access Game view size group.");
            }

            int foundIndex = FindResolution(group, width, height);
            bool wasAdded = false;
            string label = $"AIBridge {width}x{height}";

            if (foundIndex < 0)
            {
                // Add custom resolution
                foundIndex = AddCustomResolution(group, width, height, label);
                wasAdded = true;
            }
            else
            {
                var size = GetGameViewSize(group, foundIndex);
                label = GetSizeBaseText(size);
                if (string.IsNullOrEmpty(label))
                    label = $"{width}x{height}";
            }

            SetSelectedSizeIndex(gameView, foundIndex);

            // Repaint to apply the change
            ((EditorWindow)gameView).Repaint();

            return CommandResult.Success(request.id, new
            {
                action = "set_resolution",
                width,
                height,
                selectedIndex = foundIndex,
                wasAdded,
                label
            });
        }

        private CommandResult ListResolutions(CommandRequest request)
        {
            var group = GetCurrentSizeGroup();
            if (group == null)
            {
                return CommandResult.Failure(request.id,
                    "Could not access Game view size group.");
            }

            int totalCount = GetTotalCount(group);
            var resolutions = new List<object>();

            for (int i = 0; i < totalCount; i++)
            {
                var size = GetGameViewSize(group, i);
                resolutions.Add(new
                {
                    index = i,
                    width = GetSizeWidth(size),
                    height = GetSizeHeight(size),
                    name = GetSizeBaseText(size),
                    sizeType = GetSizeType(size)
                });
            }

            var gameView = GetMainGameView();
            int currentIndex = gameView != null ? GetSelectedSizeIndex(gameView) : -1;

            return CommandResult.Success(request.id, new
            {
                action = "list_resolutions",
                resolutions,
                count = totalCount,
                currentIndex
            });
        }

        private int FindResolution(object group, int width, int height)
        {
            int totalCount = GetTotalCount(group);

            for (int i = 0; i < totalCount; i++)
            {
                var size = GetGameViewSize(group, i);
                if (GetSizeType(size) == "FixedResolution" &&
                    GetSizeWidth(size) == width &&
                    GetSizeHeight(size) == height)
                {
                    return i;
                }
            }

            return -1;
        }

        private int AddCustomResolution(object group, int width, int height, string label)
        {
            // Create GameViewSize via reflection constructor
            var ctor = _gameViewSizeType.GetConstructor(new[]
            {
                _gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string)
            });

            object newSize;
            if (ctor != null)
            {
                newSize = ctor.Invoke(new[] { _fixedResolutionValue, width, height, label });
            }
            else
            {
                // Fallback: parameterless constructor + set properties
                newSize = Activator.CreateInstance(_gameViewSizeType);
                _sizeSizeType?.SetValue(newSize, _fixedResolutionValue);
                _sizeWidth.SetValue(newSize, width);
                _sizeHeight.SetValue(newSize, height);
                _sizeBaseText?.SetValue(newSize, label);
            }

            _addCustomSize.Invoke(group, new[] { newSize });

            // Save to disk
            if (_saveToHardDisk != null)
            {
                var instance = GetSizesInstance();
                _saveToHardDisk.Invoke(instance, null);
            }

            return GetTotalCount(group) - 1;
        }
    }
}
