using System;
using System.Collections.Generic;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;

namespace AIBridge.Editor
{
    public static class WorkflowSceneBulkCreateRunner
    {
        public static WorkflowJobState Execute(string jobId, Dictionary<string, object> inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentException("scene.bulk_create requires inputs.");
            }

            var manifestPathInput = WorkflowBuildSupport.GetRequiredString(inputs, "manifestPath");
            if (string.IsNullOrWhiteSpace(manifestPathInput))
            {
                throw new ArgumentException("scene.bulk_create requires manifestPath.");
            }

            var manifestAbsolutePath = WorkflowBuildSupport.NormalizeOutputPath(manifestPathInput);
            if (!File.Exists(manifestAbsolutePath))
            {
                throw new FileNotFoundException("scene.bulk_create manifest file not found.", manifestAbsolutePath);
            }

            var manifestData = AIBridgeJson.DeserializeObject(File.ReadAllText(manifestAbsolutePath));
            if (manifestData == null)
            {
                throw new ArgumentException("scene.bulk_create manifest must be a JSON object.");
            }

            if (!manifestData.TryGetValue("objects", out var objectsRaw) || !(objectsRaw is List<object> objects) || objects.Count == 0)
            {
                throw new ArgumentException("scene.bulk_create manifest requires a non-empty 'objects' array.");
            }

            if (EditorApplication.isCompiling)
            {
                throw new InvalidOperationException("Cannot start scene.bulk_create while Unity is compiling.");
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("scene.bulk_create supports Edit Mode only in the MVP.");
            }

            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw new InvalidOperationException("scene.bulk_create does not support Prefab Stage in the MVP; use an open scene context.");
            }

            if (UnityEditor.BuildPipeline.isBuildingPlayer)
            {
                throw new InvalidOperationException("Cannot start scene.bulk_create while Unity is building a player.");
            }

            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("AIBridge Scene Bulk Create");

            var previousCount = CountSceneObjects();
            var rootPath = EnsureRoot(inputs);
            var created = new List<object>();

            try
            {
                foreach (var item in objects)
                {
                    if (!(item is Dictionary<string, object> entry))
                    {
                        throw new ArgumentException("scene.bulk_create manifest contains a non-object item in 'objects'.");
                    }

                    created.Add(CreateObject(entry, rootPath));
                }

                Undo.CollapseUndoOperations(undoGroup);

                return new WorkflowJobState
                {
                    jobId = jobId,
                    jobType = "scene.bulk_create",
                    status = "success",
                    phase = "completed",
                    startedAtUtc = DateTime.UtcNow.ToString("O"),
                    completedAtUtc = DateTime.UtcNow.ToString("O"),
                    inputs = inputs,
                    result = new Dictionary<string, object>
                    {
                        ["status"] = "success",
                        ["manifestPath"] = manifestAbsolutePath,
                        ["rootPath"] = rootPath,
                        ["createdCount"] = created.Count,
                        ["sceneObjectCountBefore"] = previousCount,
                        ["sceneObjectCountAfter"] = CountSceneObjects(),
                        ["created"] = created
                    }
                };
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        private static string EnsureRoot(Dictionary<string, object> inputs)
        {
            var rootName = WorkflowBuildSupport.GetRequiredString(inputs, "rootName");
            if (string.IsNullOrWhiteSpace(rootName))
            {
                return null;
            }

            var rootParentPath = WorkflowBuildSupport.GetRequiredString(inputs, "rootParentPath");
            var rootPath = string.IsNullOrWhiteSpace(rootParentPath) ? rootName : rootParentPath + "/" + rootName;
            var existing = GameObject.Find(rootPath);
            if (existing != null)
            {
                return GetGameObjectPath(existing);
            }

            var root = new GameObject(rootName);
            if (!string.IsNullOrWhiteSpace(rootParentPath))
            {
                var parent = GameObject.Find(rootParentPath);
                if (parent == null)
                {
                    throw new ArgumentException($"scene.bulk_create rootParentPath not found: {rootParentPath}");
                }

                root.transform.SetParent(parent.transform, false);
            }

            Undo.RegisterCreatedObjectUndo(root, $"Create {rootName}");
            return GetGameObjectPath(root);
        }

        private static object CreateObject(Dictionary<string, object> entry, string defaultRootPath)
        {
            var name = GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("scene.bulk_create object entry requires 'name'.");
            }

            var parentPath = GetString(entry, "parentPath");
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                parentPath = defaultRootPath;
            }

            var expectedPath = string.IsNullOrWhiteSpace(parentPath) ? name : parentPath + "/" + name;
            if (GameObject.Find(expectedPath) != null)
            {
                throw new InvalidOperationException($"scene.bulk_create refuses to create duplicate object path: {expectedPath}");
            }

            var primitiveType = GetString(entry, "primitiveType");
            GameObject gameObject;
            if (!string.IsNullOrWhiteSpace(primitiveType))
            {
                if (!Enum.TryParse<PrimitiveType>(primitiveType, true, out var primitive))
                {
                    throw new ArgumentException($"scene.bulk_create unknown primitiveType: {primitiveType}");
                }

                gameObject = GameObject.CreatePrimitive(primitive);
                gameObject.name = name;
            }
            else
            {
                gameObject = new GameObject(name);
            }

            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                var parent = GameObject.Find(parentPath);
                if (parent == null)
                {
                    throw new ArgumentException($"scene.bulk_create parentPath not found: {parentPath}");
                }

                gameObject.transform.SetParent(parent.transform, false);
            }

            Undo.RegisterCreatedObjectUndo(gameObject, $"Create {name}");

            if (TryGetVector3(entry, "localPosition", out var localPosition))
            {
                Undo.RecordObject(gameObject.transform, $"Set Position {name}");
                gameObject.transform.localPosition = localPosition;
            }

            if (TryGetVector3(entry, "localRotation", out var localRotation))
            {
                Undo.RecordObject(gameObject.transform, $"Set Rotation {name}");
                gameObject.transform.localEulerAngles = localRotation;
            }

            if (TryGetVector3(entry, "localScale", out var localScale))
            {
                Undo.RecordObject(gameObject.transform, $"Set Scale {name}");
                gameObject.transform.localScale = localScale;
            }

            if (entry.TryGetValue("active", out var activeRaw))
            {
                var active = ToBool(activeRaw);
                Undo.RecordObject(gameObject, $"Set Active {name}");
                gameObject.SetActive(active);
            }

            var addedComponents = new List<object>();
            if (entry.TryGetValue("components", out var componentsRaw) && componentsRaw is List<object> components)
            {
                foreach (var componentRaw in components)
                {
                    if (!(componentRaw is Dictionary<string, object> componentEntry))
                    {
                        throw new ArgumentException($"scene.bulk_create components for '{name}' must be objects.");
                    }

                    addedComponents.Add(AddComponent(gameObject, componentEntry));
                }
            }

            return new Dictionary<string, object>
            {
                ["name"] = gameObject.name,
                ["path"] = GetGameObjectPath(gameObject),
                ["instanceId"] = gameObject.GetInstanceID(),
                ["componentCountAdded"] = addedComponents.Count,
                ["components"] = addedComponents
            };
        }

        private static object AddComponent(GameObject gameObject, Dictionary<string, object> componentEntry)
        {
            var typeName = GetString(componentEntry, "typeName");
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentException($"scene.bulk_create component on '{gameObject.name}' requires 'typeName'.");
            }

            var componentType = ComponentTypeResolver.Resolve(typeName);
            if (componentType == null)
            {
                throw new ArgumentException($"scene.bulk_create component type not found: {typeName}");
            }

            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                throw new ArgumentException($"scene.bulk_create type is not a Component: {typeName}");
            }

            var component = Undo.AddComponent(gameObject, componentType);

            if (componentEntry.TryGetValue("properties", out var propertiesRaw) && propertiesRaw is Dictionary<string, object> properties)
            {
                var serializedObject = new SerializedObject(component);
                foreach (var property in properties)
                {
                    var serializedProperty = serializedObject.FindProperty(property.Key);
                    if (serializedProperty == null)
                    {
                        throw new ArgumentException($"scene.bulk_create property not found on {typeName}: {property.Key}");
                    }

                    if (!SetSerializedPropertyValue(serializedProperty, property.Value))
                    {
                        throw new ArgumentException($"scene.bulk_create unsupported property type for {typeName}.{property.Key}: {serializedProperty.propertyType}");
                    }
                }

                serializedObject.ApplyModifiedProperties();
            }

            return new Dictionary<string, object>
            {
                ["typeName"] = component.GetType().Name,
                ["instanceId"] = component.GetInstanceID()
            };
        }

        private static bool SetSerializedPropertyValue(SerializedProperty prop, object value)
        {
            if (value == null)
            {
                return false;
            }

            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = Convert.ToInt32(value);
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = ToBool(value);
                        return true;
                    case SerializedPropertyType.Float:
                        prop.floatValue = Convert.ToSingle(ToDouble(value));
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value.ToString();
                        return true;
                    case SerializedPropertyType.Enum:
                        if (value is long longValue)
                        {
                            prop.enumValueIndex = (int)longValue;
                            return true;
                        }

                        if (value is double doubleValue)
                        {
                            prop.enumValueIndex = (int)doubleValue;
                            return true;
                        }

                        var enumName = value.ToString();
                        for (var i = 0; i < prop.enumNames.Length; i++)
                        {
                            if (prop.enumNames[i] == enumName)
                            {
                                prop.enumValueIndex = i;
                                return true;
                            }
                        }

                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            return value.ToString();
        }

        private static bool TryGetVector3(Dictionary<string, object> data, string key, out Vector3 result)
        {
            result = default;
            if (!data.TryGetValue(key, out var value) || !(value is Dictionary<string, object> dict))
            {
                return false;
            }

            result = new Vector3(
                Convert.ToSingle(ToDouble(dict, "x")),
                Convert.ToSingle(ToDouble(dict, "y")),
                Convert.ToSingle(ToDouble(dict, "z")));
            return true;
        }

        private static double ToDouble(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
            {
                throw new ArgumentException($"scene.bulk_create vector is missing '{key}'.");
            }

            return ToDouble(value);
        }

        private static double ToDouble(object value)
        {
            switch (value)
            {
                case double doubleValue:
                    return doubleValue;
                case long longValue:
                    return longValue;
                case int intValue:
                    return intValue;
                case float floatValue:
                    return floatValue;
                default:
                    return Convert.ToDouble(value);
            }
        }

        private static bool ToBool(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is long longValue)
            {
                return longValue != 0;
            }

            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }

        private static int CountSceneObjects()
        {
#if UNITY_2021_3_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
#else
            return UnityEngine.Object.FindObjectsOfType<GameObject>().Length;
#endif
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }
    }
}
