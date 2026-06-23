using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Inspector operations: component and SerializedProperty read/write.
    /// Supports multiple sub-commands via "action" parameter.
    /// </summary>
    public partial class InspectorCommand : ICommand, ICommandSkillDocProvider
    {
        private const string AssetsPathPrefix = "Assets/";
        private const string PackagesPathPrefix = "Packages/";
        private const string LibraryPathPrefix = "Library/";
        private const string PrefabExtension = ".prefab";
        private const int Vector2ElementCount = 2;
        private const int Vector3ElementCount = 3;
        private const int Vector4ElementCount = 4;
        private const int BoundsElementCount = 6;
        private const int Hash128HexLength = 32;

        public string Type => "inspector";
        public bool RequiresRefresh => true;

        public CommandSkillDoc SkillDoc => new CommandSkillDoc(SkillDescription)
        {
            TargetReferenceFileName = "inspector-property-reference.md"
        };

        public string SkillDescription => @"### `inspector` - Serialized Component/Asset Properties

```bash
$CLI inspector get_components --path ""Player""
$CLI inspector get_properties --path ""Player"" --componentName ""Transform""
$CLI inspector set_property --path ""Player"" --componentName ""Rigidbody"" --propertyName ""mass"" --value 10
$CLI inspector set_property --path ""Player"" --componentName ""MeshRenderer"" --propertyName ""m_Materials.Array.data[0]"" --value ""Assets/Materials/MyMat.mat""
$CLI inspector get_components --assetPath ""Assets/UI/LoginPanel.prefab"" --objectPath ""Root/Button""
$CLI inspector set_property --assetPath ""Assets/UI/LoginPanel.prefab"" --objectPath ""Root/Button"" --componentName ""RectTransform"" --propertyName ""m_AnchoredPosition.x"" --value 100
$CLI inspector set_property --assetPath ""Assets/Data/Config.asset"" --propertyName ""maxCount"" --value 20
$CLI inspector add_component --path ""Player"" --typeName ""Rigidbody""
$CLI inspector remove_component --path ""Player"" --componentName ""Rigidbody""
```

PowerShell JSON recommendation:
```powershell
$values = (@{ 'm_AnchoredPosition.x' = 100; 'm_AnchoredPosition.y' = -40; 'm_LocalPosition.z' = 0 } | ConvertTo-Json -Compress) -replace '""', '\""'
& $CLI inspector set_properties --assetPath 'Assets/UI/LoginPanel.prefab' --objectPath 'Root/Button' --componentName RectTransform --values $values
```

Use `assetPath + objectPath` for prefab asset editing. Prefer SerializedProperty paths over YAML text edits; YAML patching should only be a last-resort dry-run workflow.
For prefab assets, prefer `componentName` or `componentIndex`; `componentInstanceId` is scene-only because prefab assets are loaded in a temporary editing stage.
Avoid inline complex `--json` in PowerShell; build a JSON variable for `--values` and escape embedded quotes for native EXE argument passing, or pipe JSON through stdin when the command supports it.";

        public CommandResult Execute(CommandRequest request)
        {
            var action = request.GetParam("action", "get_components");

            try
            {
                switch (action.ToLower())
                {
                    case "get_components":
                        return GetComponents(request);
                    case "get_properties":
                        return GetProperties(request);
                    case "get_property":
                        return GetProperty(request);
                    case "find_property":
                        return FindProperty(request);
                    case "set_property":
                        return SetProperty(request);
                    case "set_properties":
                        return SetProperties(request);
                    case "add_component":
                        return AddComponent(request);
                    case "remove_component":
                        return RemoveComponent(request);
                    default:
                        return CommandResult.Failure(request.id, $"Unknown action: {action}. Supported: get_components, get_properties, get_property, find_property, set_property, set_properties, add_component, remove_component");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(request.id, ex);
            }
        }

        private CommandResult GetComponents(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var components = new List<ComponentInfo>();
                var allComponents = go.GetComponents<Component>();

                for (var i = 0; i < allComponents.Length; i++)
                {
                    var comp = allComponents[i];
                    if (comp == null) continue;

                    var info = new ComponentInfo
                    {
                        index = i,
                        typeName = comp.GetType().Name,
                        fullTypeName = comp.GetType().FullName,
                        instanceId = AIBridgeEditorObjectIdentity.GetSerializedId(comp)
                    };

                    if (comp is Behaviour behaviour)
                    {
                        info.enabled = behaviour.enabled;
                    }

                    components.Add(info);
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    components = components,
                    count = components.Count
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult GetProperties(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var includeChildren = request.GetParam("includeChildren", false);
                var properties = new List<PropertyInfo>();
                var so = new SerializedObject(serializedTarget);
                var iterator = so.GetIterator();
                var enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = includeChildren;
                    properties.Add(BuildPropertyInfo(iterator));
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    gameObjectName = context.GameObject != null ? context.GameObject.name : null,
                    componentName = component != null ? component.GetType().Name : null,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    properties = properties
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult GetProperty(CommandRequest request)
        {
            var propertyName = request.GetParam<string>("propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandResult.Failure(request.id, "Missing 'propertyName' parameter");
            }

            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var so = new SerializedObject(serializedTarget);
                var prop = so.FindProperty(propertyName);
                if (prop == null)
                {
                    return CommandResult.Failure(request.id, $"Property not found: {propertyName}");
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    componentName = component != null ? component.GetType().Name : null,
                    propertyName = propertyName,
                    propertyType = prop.propertyType.ToString(),
                    value = GetPropertyValue(prop),
                    editable = prop.editable,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult FindProperty(CommandRequest request)
        {
            var keyword = request.GetParam<string>("keyword", null);
            if (string.IsNullOrEmpty(keyword))
            {
                return CommandResult.Failure(request.id, "Missing 'keyword' parameter");
            }

            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, false, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                var matches = new List<PropertyInfo>();
                var comparison = StringComparison.OrdinalIgnoreCase;
                var so = new SerializedObject(serializedTarget);
                var iterator = so.GetIterator();
                var enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyPath.IndexOf(keyword, comparison) < 0
                        && iterator.name.IndexOf(keyword, comparison) < 0
                        && iterator.displayName.IndexOf(keyword, comparison) < 0)
                    {
                        continue;
                    }

                    matches.Add(BuildPropertyInfo(iterator));
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    componentName = component != null ? component.GetType().Name : null,
                    keyword = keyword,
                    count = matches.Count,
                    matches = matches,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult SetProperty(CommandRequest request)
        {
            var propertyName = request.GetParam<string>("propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandResult.Failure(request.id, "Missing 'propertyName' parameter");
            }

            var values = new Dictionary<string, object>();
            values[propertyName] = request.GetParam<object>("value", null);
            return SetPropertiesInternal(request, values);
        }

        private CommandResult SetProperties(CommandRequest request)
        {
            Dictionary<string, object> values;
            string error;
            if (!TryGetValuesDictionary(request, out values, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            return SetPropertiesInternal(request, values);
        }

        private CommandResult SetPropertiesInternal(CommandRequest request, Dictionary<string, object> values)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                UnityEngine.Object serializedTarget;
                Component component;
                if (!TryResolveSerializedTarget(context, request, out serializedTarget, out component, out error))
                {
                    return CommandResult.Failure(request.id, error);
                }

                if (serializedTarget == null)
                {
                    return CommandResult.Failure(request.id, "Serialized target not found");
                }

                var so = new SerializedObject(serializedTarget);
                so.Update();

                if (context.IsSceneObject)
                {
                    Undo.RecordObject(serializedTarget, "Set Serialized Properties");
                }

                var changes = new List<PropertyChangeInfo>();
                foreach (var pair in values)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                    {
                        return CommandResult.Failure(request.id, "Property name cannot be empty");
                    }

                    var prop = so.FindProperty(pair.Key);
                    if (prop == null)
                    {
                        return CommandResult.Failure(request.id, $"Property not found: {pair.Key}");
                    }

                    if (!prop.editable)
                    {
                        return CommandResult.Failure(request.id, $"Property is not editable: {pair.Key}");
                    }

                    var oldValue = GetPropertyValue(prop);
                    string setError;
                    if (!TrySetPropertyValue(prop, pair.Value, out setError))
                    {
                        return CommandResult.Failure(request.id, $"Failed to set property value for '{pair.Key}' ({prop.propertyType}): {setError}");
                    }

                    changes.Add(new PropertyChangeInfo
                    {
                        propertyName = pair.Key,
                        propertyType = prop.propertyType.ToString(),
                        oldValue = oldValue,
                        newValue = GetPropertyValue(prop)
                    });
                }

                var changed = so.ApplyModifiedProperties();
                if (changed)
                {
                    string saveError;
                    if (!TrySaveModifiedTarget(context, serializedTarget, out saveError))
                    {
                        return CommandResult.Failure(request.id, saveError);
                    }
                }

                return CommandResult.Success(request.id, new
                {
                    targetName = serializedTarget.name,
                    targetType = serializedTarget.GetType().Name,
                    gameObjectName = context.GameObject != null ? context.GameObject.name : null,
                    componentName = component != null ? component.GetType().Name : null,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    changed = changed,
                    changes = changes
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult AddComponent(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var typeName = request.GetParam<string>("typeName");
                if (string.IsNullOrEmpty(typeName))
                {
                    return CommandResult.Failure(request.id, "Missing 'typeName' parameter");
                }

                var componentType = ComponentTypeResolver.Resolve(typeName);
                if (componentType == null)
                {
                    return CommandResult.Failure(request.id, $"Component type not found: {typeName}");
                }

                if (!typeof(Component).IsAssignableFrom(componentType))
                {
                    return CommandResult.Failure(request.id, $"Type is not a Component: {typeName}");
                }

                Component newComponent;
                if (context.IsSceneObject)
                {
                    newComponent = Undo.AddComponent(go, componentType);
                }
                else
                {
                    newComponent = go.AddComponent(componentType);
                    string saveError;
                    if (!TrySaveModifiedTarget(context, newComponent, out saveError))
                    {
                        return CommandResult.Failure(request.id, saveError);
                    }
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    addedComponent = newComponent.GetType().Name,
                    instanceId = AIBridgeEditorObjectIdentity.GetSerializedId(newComponent)
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private CommandResult RemoveComponent(CommandRequest request)
        {
            TargetContext context;
            string error;
            if (!TryResolveTargetContext(request, true, out context, out error))
            {
                return CommandResult.Failure(request.id, error);
            }

            try
            {
                var go = context.GameObject;
                if (go == null)
                {
                    return CommandResult.Failure(request.id, "Target does not contain components");
                }

                var component = ResolveComponent(context, request);
                if (component == null)
                {
                    return CommandResult.Failure(request.id, "Component not found");
                }

                if (component is Transform)
                {
                    return CommandResult.Failure(request.id, "Cannot remove Transform component");
                }

                var removedTypeName = component.GetType().Name;
                if (context.IsSceneObject)
                {
                    Undo.DestroyObjectImmediate(component);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(component, true);
                    string saveError;
                    if (!TrySaveModifiedTarget(context, go, out saveError))
                    {
                        return CommandResult.Failure(request.id, saveError);
                    }
                }

                return CommandResult.Success(request.id, new
                {
                    gameObjectName = go.name,
                    assetPath = context.AssetPath,
                    objectPath = context.ObjectPath,
                    removedComponent = removedTypeName
                });
            }
            finally
            {
                context.Dispose();
            }
        }

        private sealed class TargetContext : IDisposable
        {
            public string AssetPath;
            public string ObjectPath;
            public GameObject PrefabRoot;
            public GameObject GameObject;
            public UnityEngine.Object SerializedTarget;
            public bool IsPrefabAsset;
            public bool IsAssetObject;
            public bool IsSceneObject;

            public void Dispose()
            {
                if (IsPrefabAsset && PrefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(PrefabRoot);
                    PrefabRoot = null;
                }
            }
        }

        [Serializable]
        private class ComponentInfo
        {
            public int index;
            public string typeName;
            public string fullTypeName;
            public object instanceId;
            public bool enabled = true;
        }

        [Serializable]
        private class PropertyInfo
        {
            public string name;
            public string propertyPath;
            public string displayName;
            public string propertyType;
            public object value;
            public bool editable;
            public bool isExpanded;
            public bool hasChildren;
            public int depth;
        }

        [Serializable]
        private class PropertyChangeInfo
        {
            public string propertyName;
            public string propertyType;
            public object oldValue;
            public object newValue;
        }
    }
}
