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
    public partial class InspectorCommand
    {
        private bool TryResolveTargetContext(CommandRequest request, bool forWrite, out TargetContext context, out string error)
        {
            context = null;
            error = null;

            var assetPath = request.GetParam<string>("assetPath", null);
            var objectPath = request.GetParam<string>("objectPath", null);

            if (!string.IsNullOrEmpty(assetPath))
            {
                if (!IsUnityAssetPath(assetPath))
                {
                    error = "assetPath must start with Assets/ or Packages/";
                    return false;
                }

                if (forWrite && assetPath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Editing package assets is not supported. Copy the asset into Assets/ first.";
                    return false;
                }

                if (Path.GetExtension(assetPath).Equals(PrefabExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return TryResolvePrefabAssetContext(assetPath, objectPath, out context, out error);
                }

                if (!string.IsNullOrEmpty(objectPath))
                {
                    error = "objectPath is only supported for prefab assets";
                    return false;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                {
                    error = $"Asset not found at path: {assetPath}";
                    return false;
                }

                var assetGameObject = asset as GameObject;
                context = new TargetContext
                {
                    AssetPath = assetPath,
                    SerializedTarget = asset,
                    GameObject = assetGameObject,
                    IsSceneObject = false,
                    IsAssetObject = true
                };
                return true;
            }

            var componentInstanceId = request.GetParam("componentInstanceId", 0);
            if (componentInstanceId != 0)
            {
                var component = GetObjectByInstanceId(componentInstanceId) as Component;
                if (component == null)
                {
                    error = $"Component not found by instanceId: {componentInstanceId}";
                    return false;
                }

                context = new TargetContext
                {
                    SerializedTarget = component,
                    GameObject = component.gameObject,
                    IsSceneObject = true
                };
                return true;
            }

            var go = GetSceneGameObject(request);
            if (go == null)
            {
                error = "GameObject not found";
                return false;
            }

            context = new TargetContext
            {
                GameObject = go,
                SerializedTarget = go,
                IsSceneObject = true
            };
            return true;
        }

        private bool TryResolvePrefabAssetContext(string assetPath, string objectPath, out TargetContext context, out string error)
        {
            context = null;
            error = null;

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception ex)
            {
                error = $"Failed to load prefab contents: {ex.Message}";
                return false;
            }

            if (root == null)
            {
                error = $"Prefab not found at path: {assetPath}";
                return false;
            }

            var target = ResolvePrefabObject(root, objectPath);
            if (target == null)
            {
                PrefabUtility.UnloadPrefabContents(root);
                error = $"Object not found in prefab: {objectPath}";
                return false;
            }

            context = new TargetContext
            {
                AssetPath = assetPath,
                ObjectPath = string.IsNullOrEmpty(objectPath) ? target.name : objectPath,
                PrefabRoot = root,
                GameObject = target,
                SerializedTarget = target,
                IsPrefabAsset = true,
                IsAssetObject = true,
                IsSceneObject = false
            };
            return true;
        }

        private bool TryResolveSerializedTarget(TargetContext context, CommandRequest request, out UnityEngine.Object serializedTarget, out Component component, out string error)
        {
            serializedTarget = null;
            component = null;
            error = null;

            if (context == null)
            {
                error = "Target context is null";
                return false;
            }

            if (context.SerializedTarget is Component existingComponent)
            {
                component = existingComponent;
                serializedTarget = existingComponent;
                return true;
            }

            if (context.GameObject != null)
            {
                component = ResolveComponent(context, request);
                if (component == null)
                {
                    error = "Component not found. Provide 'componentName' or 'componentIndex'";
                    return false;
                }

                serializedTarget = component;
                return true;
            }

            if (context.SerializedTarget != null)
            {
                if (!string.IsNullOrEmpty(request.GetParam<string>("componentName", null)) || request.GetParam("componentIndex", -1) >= 0)
                {
                    error = "componentName/componentIndex can only be used with GameObject targets";
                    return false;
                }

                serializedTarget = context.SerializedTarget;
                return true;
            }

            error = "Serialized target not found";
            return false;
        }

        private Component ResolveComponent(TargetContext context, CommandRequest request)
        {
            var go = context != null ? context.GameObject : null;
            if (go == null)
            {
                return null;
            }

            var componentInstanceId = request.GetParam("componentInstanceId", 0);
            if (componentInstanceId != 0 && context.IsSceneObject)
            {
                var componentById = GetObjectByInstanceId(componentInstanceId) as Component;
                if (componentById != null && componentById.gameObject == go)
                {
                    return componentById;
                }
            }

            var componentIndex = request.GetParam("componentIndex", -1);
            if (componentIndex >= 0)
            {
                var components = go.GetComponents<Component>();
                if (componentIndex < components.Length)
                {
                    return components[componentIndex];
                }

                return null;
            }

            var componentName = request.GetParam<string>("componentName", null);
            if (string.IsNullOrEmpty(componentName))
            {
                return null;
            }

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null && (comp.GetType().Name == componentName || comp.GetType().FullName == componentName))
                {
                    return comp;
                }
            }

            return null;
        }

        private GameObject GetSceneGameObject(CommandRequest request)
        {
            var path = request.GetParam<string>("path", null);
            var instanceId = request.GetParam("instanceId", 0);

            if (instanceId != 0)
            {
                return GetObjectByInstanceId(instanceId) as GameObject;
            }

            if (!string.IsNullOrEmpty(path))
            {
                return GameObject.Find(path);
            }

            return Selection.activeGameObject;
        }

        private static UnityEngine.Object GetObjectByInstanceId(int instanceId)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }

        private static bool IsUnityAssetPath(string assetPath)
        {
            return assetPath.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase)
                   || assetPath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static GameObject ResolvePrefabObject(GameObject root, string objectPath)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(objectPath) || objectPath == "." || objectPath == "/" || objectPath == root.name)
            {
                return root;
            }

            var normalized = objectPath.Replace('\\', '/').Trim('/');
            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(root.name.Length + 1);
            }

            var child = root.transform.Find(normalized);
            if (child != null)
            {
                return child.gameObject;
            }

            // Transform.Find 需要完整路径；这里提供按名称兜底，便于 AI 在只知道对象名时定位。
            return FindChildByName(root.transform, normalized);
        }

        private static GameObject FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == name)
                {
                    return child.gameObject;
                }

                var found = FindChildByName(child, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool TrySaveModifiedTarget(TargetContext context, UnityEngine.Object serializedTarget, out string error)
        {
            error = null;
            if (context == null)
            {
                return true;
            }

            if (context.IsPrefabAsset && context.PrefabRoot != null)
            {
                bool success;
                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(context.PrefabRoot, context.AssetPath, out success);
                if (!success || savedPrefab == null)
                {
                    error = $"Failed to save prefab asset: {context.AssetPath}";
                    return false;
                }

                AssetDatabase.ImportAsset(context.AssetPath);
                return true;
            }

            if (context.IsAssetObject && serializedTarget != null)
            {
                EditorUtility.SetDirty(serializedTarget);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(context.AssetPath);
            }

            return true;
        }

        private static Type ResolveComponentType(string typeName)
        {
            var possibleNames = new[]
            {
                typeName,
                $"UnityEngine.{typeName}",
                $"UnityEngine.UI.{typeName}",
                $"TMPro.{typeName}"
            };

            foreach (var name in possibleNames)
            {
                var componentType = System.Type.GetType(name);
                if (componentType != null)
                {
                    return componentType;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    componentType = assembly.GetType(name);
                    if (componentType != null)
                    {
                        return componentType;
                    }
                }
            }

            return null;
        }
    }
}
