using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using AIBridge.Internal.Json;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// 执行事务式 Prefab Patch：一次加载、连续修改、一次保存，失败时不写回资源。
    /// </summary>
    internal static class PrefabPatchExecutor
    {
        private const string AssetsPathPrefix = "Assets/";
        private const string PackagesPathPrefix = "Packages/";
        private const int SnapshotDepth = 3;

        public static CommandResult Execute(string requestId, CommandRequest request)
        {
            var prefabPath = request.GetParam<string>("prefabPath", null);
            if (string.IsNullOrEmpty(prefabPath))
            {
                return CommandResult.Failure(requestId, "Missing 'prefabPath' parameter");
            }

            if (!prefabPath.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(requestId, "prefabPath must start with Assets/");
            }

            if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Failure(requestId, "prefabPath must point to a .prefab asset");
            }

            var dryRun = request.GetParam("dryRun", false) || request.GetParam("dry-run", false);

            IList operations;
            string parseError;
            if (!TryGetOperations(request, out operations, out parseError))
            {
                return CommandResult.Failure(requestId, parseError);
            }

            GameObject root = null;
            var context = new PatchContext
            {
                PrefabPath = prefabPath,
                DryRun = dryRun
            };

            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                {
                    return CommandResult.Failure(requestId, $"Prefab not found at path: {prefabPath}");
                }

                context.Root = root;

                for (var i = 0; i < operations.Count; i++)
                {
                    var operation = operations[i] as IDictionary;
                    if (operation == null)
                    {
                        throw new PatchException($"Operation #{i + 1} must be an object");
                    }

                    ExecuteOperation(context, operation, i);
                }

                if (!dryRun && context.Changed)
                {
                    bool success;
                    var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out success);
                    if (!success || savedPrefab == null)
                    {
                        throw new PatchException($"Failed to save prefab asset: {prefabPath}");
                    }

                    AssetDatabase.ImportAsset(prefabPath);
                }

                return CommandResult.Success(requestId, new
                {
                    prefabPath = prefabPath,
                    dryRun = dryRun,
                    changed = context.Changed,
                    operationCount = operations.Count,
                    operations = context.Results,
                    hierarchy = BuildHierarchySnapshot(root, root.name, SnapshotDepth)
                });
            }
            catch (PatchException ex)
            {
                return CommandResult.Failure(requestId, ex.Message);
            }
            catch (Exception ex)
            {
                return CommandResult.FromException(requestId, ex);
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        private static void ExecuteOperation(PatchContext context, IDictionary operation, int index)
        {
            var op = GetString(operation, "op");
            if (string.IsNullOrEmpty(op))
            {
                throw new PatchException($"Operation #{index + 1} is missing 'op'");
            }

            switch (op)
            {
                case "ensure_child":
                    EnsureChild(context, operation, index, op);
                    break;
                case "ensure_component":
                    EnsureComponent(context, operation, index, op);
                    break;
                case "set_property":
                    SetProperty(context, operation, index, op);
                    break;
                case "set_properties":
                    SetProperties(context, operation, index, op);
                    break;
                case "set_array":
                    SetArray(context, operation, index, op);
                    break;
                case "append_array":
                    SetArray(context, operation, index, op);
                    break;
                case "clear_array":
                    ClearArray(context, operation, index, op);
                    break;
                default:
                    throw new PatchException($"Operation #{index + 1} has unsupported op: {op}");
            }
        }

        private static void EnsureChild(PatchContext context, IDictionary operation, int index, string op)
        {
            var path = GetString(operation, "path");
            if (string.IsNullOrEmpty(path))
            {
                path = GetTargetPath(operation);
            }

            var beforeChanged = context.Changed;
            var child = EnsureGameObjectPath(context, path);
            if (SetOptionalGameObjectState(child, operation))
            {
                context.Changed = true;
            }

            AddResult(context, index, op, path, context.Changed != beforeChanged, "ok");
        }

        private static void EnsureComponent(PatchContext context, IDictionary operation, int index, string op)
        {
            var path = GetTargetPath(operation);
            var typeName = GetString(operation, "typeName");
            if (string.IsNullOrEmpty(typeName))
            {
                typeName = GetString(operation, "componentName");
            }

            if (string.IsNullOrEmpty(typeName))
            {
                throw new PatchException($"Operation #{index + 1} is missing 'typeName'");
            }

            var go = ResolveGameObject(context, path);
            var componentType = ComponentTypeResolver.Resolve(typeName);
            if (componentType == null)
            {
                throw new PatchException($"Component type not found: {typeName}");
            }

            if (!typeof(Component).IsAssignableFrom(componentType))
            {
                throw new PatchException($"Type is not a Component: {typeName}");
            }

            var existing = go.GetComponent(componentType);
            var changed = false;
            if (existing == null)
            {
                go.AddComponent(componentType);
                context.Changed = true;
                changed = true;
            }

            AddResult(context, index, op, path, changed, componentType.Name);
        }

        private static void SetProperty(PatchContext context, IDictionary operation, int index, string op)
        {
            var target = ResolveSerializedTarget(context, operation, index);
            var propertyName = GetString(operation, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                throw new PatchException($"Operation #{index + 1} is missing 'propertyName'");
            }

            var value = GetValue(operation, "value");
            var changed = WriteProperties(context, target.TargetObject, new Dictionary<string, object> { { propertyName, value } });
            AddResult(context, index, op, target.Path, changed, propertyName);
        }

        private static void SetProperties(PatchContext context, IDictionary operation, int index, string op)
        {
            var target = ResolveSerializedTarget(context, operation, index);
            var values = GetDictionary(operation, "values");
            if (values == null)
            {
                throw new PatchException($"Operation #{index + 1} is missing 'values'");
            }

            var propertyValues = new Dictionary<string, object>();
            foreach (DictionaryEntry entry in values)
            {
                propertyValues[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
            }

            var changed = WriteProperties(context, target.TargetObject, propertyValues);
            AddResult(context, index, op, target.Path, changed, propertyValues.Count + " properties");
        }

        private static void SetArray(PatchContext context, IDictionary operation, int index, string op)
        {
            var target = ResolveSerializedTarget(context, operation, index);
            var propertyName = GetString(operation, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                throw new PatchException($"Operation #{index + 1} is missing 'propertyName'");
            }

            var items = GetList(operation, "items");
            if (items == null)
            {
                throw new PatchException($"Operation #{index + 1} is missing 'items'");
            }

            var mode = GetString(operation, "mode");
            if (string.IsNullOrEmpty(mode))
            {
                mode = op == "append_array" ? "append" : "replace";
            }

            var so = new SerializedObject(target.TargetObject);
            so.Update();

            var arrayProp = so.FindProperty(propertyName);
            if (arrayProp == null)
            {
                throw new PatchException($"Property not found: {propertyName}");
            }

            if (!arrayProp.isArray || arrayProp.propertyType == SerializedPropertyType.String)
            {
                throw new PatchException($"Property is not an array: {propertyName}");
            }

            var startIndex = 0;
            if (string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = arrayProp.arraySize;
                arrayProp.arraySize = arrayProp.arraySize + items.Count;
            }
            else if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
            {
                arrayProp.arraySize = items.Count;
            }
            else
            {
                throw new PatchException($"Unsupported array mode: {mode}");
            }

            for (var i = 0; i < items.Count; i++)
            {
                var element = arrayProp.GetArrayElementAtIndex(startIndex + i);
                WriteArrayElement(context, element, items[i]);
            }

            var changed = so.ApplyModifiedProperties();
            if (changed)
            {
                context.Changed = true;
            }

            AddResult(context, index, op, target.Path, changed, propertyName + "[" + items.Count + "]");
        }

        private static void ClearArray(PatchContext context, IDictionary operation, int index, string op)
        {
            var target = ResolveSerializedTarget(context, operation, index);
            var propertyName = GetString(operation, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                throw new PatchException($"Operation #{index + 1} is missing 'propertyName'");
            }

            var so = new SerializedObject(target.TargetObject);
            so.Update();

            var arrayProp = so.FindProperty(propertyName);
            if (arrayProp == null)
            {
                throw new PatchException($"Property not found: {propertyName}");
            }

            if (!arrayProp.isArray || arrayProp.propertyType == SerializedPropertyType.String)
            {
                throw new PatchException($"Property is not an array: {propertyName}");
            }

            arrayProp.arraySize = 0;
            var changed = so.ApplyModifiedProperties();
            if (changed)
            {
                context.Changed = true;
            }

            AddResult(context, index, op, target.Path, changed, propertyName);
        }

        private static bool WriteProperties(PatchContext context, UnityEngine.Object serializedTarget, Dictionary<string, object> values)
        {
            var so = new SerializedObject(serializedTarget);
            so.Update();

            foreach (var pair in values)
            {
                var prop = so.FindProperty(pair.Key);
                if (prop == null)
                {
                    throw new PatchException($"Property not found: {pair.Key}");
                }

                WritePropertyValue(context, prop, pair.Value);
            }

            var changed = so.ApplyModifiedProperties();
            if (changed)
            {
                context.Changed = true;
            }

            return changed;
        }

        private static void WriteArrayElement(PatchContext context, SerializedProperty element, object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary == null)
            {
                WritePropertyValue(context, element, value);
                return;
            }

            // 结构体/类数组按字段名写入，避免依赖 YAML 中的 fileID 排列。
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(key) || key.StartsWith("$", StringComparison.Ordinal))
                {
                    continue;
                }

                var child = element.FindPropertyRelative(key);
                if (child == null)
                {
                    throw new PatchException($"Array element field not found: {key}");
                }

                WritePropertyValue(context, child, entry.Value);
            }
        }

        private static void WritePropertyValue(PatchContext context, SerializedProperty prop, object value)
        {
            if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
            {
                var list = value as IList;
                if (list == null)
                {
                    throw new PatchException($"Expected array value for property: {prop.propertyPath}");
                }

                prop.arraySize = list.Count;
                for (var i = 0; i < list.Count; i++)
                {
                    WriteArrayElement(context, prop.GetArrayElementAtIndex(i), list[i]);
                }

                return;
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.Float:
                    prop.floatValue = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.String:
                    prop.stringValue = value != null ? value.ToString() : string.Empty;
                    return;
                case SerializedPropertyType.Enum:
                    SetEnumValue(prop, value);
                    return;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = ResolveObjectReference(context, value, prop);
                    return;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = ReadVector2(value);
                    return;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = ReadVector3(value);
                    return;
                case SerializedPropertyType.Vector4:
                    prop.vector4Value = ReadVector4(value);
                    return;
                case SerializedPropertyType.Color:
                    prop.colorValue = ReadColor(value);
                    return;
                case SerializedPropertyType.Rect:
                    prop.rectValue = ReadRect(value);
                    return;
                case SerializedPropertyType.ArraySize:
                    prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case SerializedPropertyType.Bounds:
                    prop.boundsValue = ReadBounds(value);
                    return;
                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = ReadQuaternion(value);
                    return;
                default:
                    throw new PatchException($"Unsupported property type: {prop.propertyType} ({prop.propertyPath})");
            }
        }

        private static UnityEngine.Object ResolveObjectReference(PatchContext context, object value, SerializedProperty prop)
        {
            if (IsNullReferenceValue(value))
            {
                return null;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                if (HasKey(dictionary, "$gameObject"))
                {
                    return ValidateReferenceType(ResolveGameObject(context, GetString(dictionary, "$gameObject")), prop);
                }

                if (HasKey(dictionary, "$component"))
                {
                    var componentSpec = GetValue(dictionary, "$component") as IDictionary;
                    if (componentSpec == null)
                    {
                        throw new PatchException("$component reference must be an object");
                    }

                    var path = GetString(componentSpec, "path");
                    var componentName = GetString(componentSpec, "componentName");
                    if (string.IsNullOrEmpty(componentName))
                    {
                        componentName = GetString(componentSpec, "typeName");
                    }

                    var componentIndex = GetInt(componentSpec, "componentIndex", -1);
                    var go = ResolveGameObject(context, path);
                    var component = ResolveComponent(go, componentName, componentIndex);
                    return ValidateReferenceType(component, prop);
                }

                if (HasKey(dictionary, "$asset"))
                {
                    return ResolveAssetReference(GetString(dictionary, "$asset"), prop);
                }

                if (HasKey(dictionary, "$guid"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(GetString(dictionary, "$guid"));
                    return ResolveAssetReference(assetPath, prop);
                }
            }

            if (value is double || value is long || value is int)
            {
                return ValidateReferenceType(AIBridgeEditorObjectIdentity.ResolveObject(value), prop);
            }

            var text = value.ToString();
            if (text.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase)
                || text.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveAssetReference(text, prop);
            }

            var guidPath = AssetDatabase.GUIDToAssetPath(text);
            if (!string.IsNullOrEmpty(guidPath))
            {
                return ResolveAssetReference(guidPath, prop);
            }

            return ValidateReferenceType(ResolveGameObject(context, text), prop);
        }

        private static UnityEngine.Object ResolveAssetReference(string assetPath, SerializedProperty prop)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new PatchException("Asset reference path is empty");
            }

            var expectedType = GetExpectedTypeFromProperty(prop);
            if (expectedType != null)
            {
                var typed = AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
                if (typed != null)
                {
                    return typed;
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
            {
                throw new PatchException($"Object reference target not found: {assetPath}");
            }

            return ValidateReferenceType(asset, prop);
        }

        private static UnityEngine.Object ValidateReferenceType(UnityEngine.Object resolved, SerializedProperty prop)
        {
            if (resolved == null)
            {
                return null;
            }

            var expectedType = GetExpectedTypeFromProperty(prop);
            if (expectedType != null && !expectedType.IsAssignableFrom(resolved.GetType()))
            {
                throw new PatchException($"Reference type mismatch for {prop.propertyPath}: expected {expectedType.Name}, got {resolved.GetType().Name}");
            }

            return resolved;
        }

        private static SerializedTarget ResolveSerializedTarget(PatchContext context, IDictionary operation, int index)
        {
            var target = GetDictionary(operation, "target");
            var path = GetTargetPath(operation);
            if (target != null && !string.IsNullOrEmpty(GetString(target, "path")))
            {
                path = GetString(target, "path");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new PatchException($"Operation #{index + 1} is missing target path");
            }

            var go = ResolveGameObject(context, path);

            IDictionary source = target != null ? target : operation;
            var componentName = GetString(source, "componentName");
            if (string.IsNullOrEmpty(componentName))
            {
                componentName = GetString(source, "typeName");
            }

            var componentIndex = GetInt(source, "componentIndex", -1);
            if (string.IsNullOrEmpty(componentName) && componentIndex < 0)
            {
                return new SerializedTarget
                {
                    Path = GetGameObjectPath(go),
                    TargetObject = go
                };
            }

            var component = ResolveComponent(go, componentName, componentIndex);
            return new SerializedTarget
            {
                Path = GetGameObjectPath(go),
                TargetObject = component
            };
        }

        private static GameObject EnsureGameObjectPath(PatchContext context, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new PatchException("GameObject path is empty");
            }

            var normalized = NormalizePath(context.Root, path);
            if (string.IsNullOrEmpty(normalized))
            {
                return context.Root;
            }

            var current = context.Root.transform;
            var segments = normalized.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var child = FindDirectChildUnique(current, segment);
                if (child == null)
                {
                    var go = new GameObject(segment);
                    go.transform.SetParent(current, false);
                    current = go.transform;
                    context.Changed = true;
                    continue;
                }

                current = child;
            }

            return current.gameObject;
        }

        private static GameObject ResolveGameObject(PatchContext context, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new PatchException("GameObject path is empty");
            }

            var normalized = NormalizePath(context.Root, path);
            if (string.IsNullOrEmpty(normalized))
            {
                return context.Root;
            }

            var current = context.Root.transform;
            var segments = normalized.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                current = FindDirectChildUnique(current, segments[i]);
                if (current == null)
                {
                    throw new PatchException($"GameObject path not found: {path}");
                }
            }

            return current.gameObject;
        }

        private static Transform FindDirectChildUnique(Transform parent, string childName)
        {
            Transform found = null;
            var count = 0;

            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name != childName)
                {
                    continue;
                }

                found = child;
                count++;
            }

            if (count > 1)
            {
                throw new PatchException($"Ambiguous child name '{childName}' under '{GetGameObjectPath(parent.gameObject)}'");
            }

            return found;
        }

        private static string NormalizePath(GameObject root, string path)
        {
            var normalized = path.Replace('\\', '/').Trim('/');
            if (normalized == "." || normalized == root.name)
            {
                return string.Empty;
            }

            if (normalized.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                return normalized.Substring(root.name.Length + 1);
            }

            return normalized;
        }

        private static Component ResolveComponent(GameObject go, string componentName, int componentIndex)
        {
            if (componentIndex >= 0)
            {
                var componentsByIndex = go.GetComponents<Component>();
                if (componentIndex < componentsByIndex.Length && componentsByIndex[componentIndex] != null)
                {
                    return componentsByIndex[componentIndex];
                }

                throw new PatchException($"Component index not found on {GetGameObjectPath(go)}: {componentIndex}");
            }

            if (string.IsNullOrEmpty(componentName))
            {
                throw new PatchException($"Component name is empty on {GetGameObjectPath(go)}");
            }

            Component found = null;
            var count = 0;
            var components = go.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (type.Name == componentName || type.FullName == componentName)
                {
                    found = component;
                    count++;
                }
            }

            if (count == 0)
            {
                throw new PatchException($"Component not found on {GetGameObjectPath(go)}: {componentName}");
            }

            if (count > 1)
            {
                throw new PatchException($"Ambiguous component '{componentName}' on {GetGameObjectPath(go)}; use componentIndex");
            }

            return found;
        }

        private static void SetEnumValue(SerializedProperty prop, object value)
        {
            var text = value != null ? value.ToString() : string.Empty;
            int intValue;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
            {
                if (intValue < 0 || intValue >= prop.enumNames.Length)
                {
                    throw new PatchException($"Invalid enum index for {prop.propertyPath}: {intValue}");
                }

                prop.enumValueIndex = intValue;
                return;
            }

            for (var i = 0; i < prop.enumNames.Length; i++)
            {
                if (string.Equals(prop.enumNames[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    prop.enumValueIndex = i;
                    return;
                }
            }

            throw new PatchException($"Invalid enum value for {prop.propertyPath}: {text}");
        }

        private static Vector2 ReadVector2(object value)
        {
            var values = ReadFloatArray(value, 2);
            return new Vector2(values[0], values[1]);
        }

        private static Vector3 ReadVector3(object value)
        {
            var values = ReadFloatArray(value, 3);
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Vector4 ReadVector4(object value)
        {
            var values = ReadFloatArray(value, 4);
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        private static Quaternion ReadQuaternion(object value)
        {
            var values = ReadFloatArray(value, 4);
            return new Quaternion(values[0], values[1], values[2], values[3]);
        }

        private static Color ReadColor(object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var r = GetValue(dictionary, "r");
                var g = GetValue(dictionary, "g");
                var b = GetValue(dictionary, "b");
                if (r != null && g != null && b != null)
                {
                    var a = GetValue(dictionary, "a");
                    return new Color(
                        Convert.ToSingle(r, CultureInfo.InvariantCulture),
                        Convert.ToSingle(g, CultureInfo.InvariantCulture),
                        Convert.ToSingle(b, CultureInfo.InvariantCulture),
                        a != null ? Convert.ToSingle(a, CultureInfo.InvariantCulture) : 1f);
                }
            }

            var values = ReadFloatArray(value, 4);
            return new Color(values[0], values[1], values[2], values[3]);
        }

        private static Rect ReadRect(object value)
        {
            var values = ReadFloatArray(value, 4);
            return new Rect(values[0], values[1], values[2], values[3]);
        }

        private static Bounds ReadBounds(object value)
        {
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                return new Bounds(ReadVector3(GetValue(dictionary, "center")), ReadVector3(GetValue(dictionary, "size")));
            }

            var values = ReadFloatArray(value, 6);
            return new Bounds(new Vector3(values[0], values[1], values[2]), new Vector3(values[3], values[4], values[5]));
        }

        private static float[] ReadFloatArray(object value, int count)
        {
            var result = new float[count];
            var list = value as IList;
            if (list != null)
            {
                if (list.Count != count)
                {
                    throw new PatchException($"Expected {count} values, got {list.Count}");
                }

                for (var i = 0; i < count; i++)
                {
                    result[i] = Convert.ToSingle(list[i], CultureInfo.InvariantCulture);
                }

                return result;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var keys = count == 4 ? new[] { "x", "y", "z", "w" } : count == 3 ? new[] { "x", "y", "z" } : new[] { "x", "y" };
                for (var i = 0; i < count; i++)
                {
                    result[i] = Convert.ToSingle(GetValue(dictionary, keys[i]), CultureInfo.InvariantCulture);
                }

                return result;
            }

            var parts = value.ToString().Trim('(', ')', '[', ']').Split(',');
            if (parts.Length != count)
            {
                throw new PatchException($"Expected {count} comma-separated values");
            }

            for (var i = 0; i < count; i++)
            {
                result[i] = Convert.ToSingle(parts[i].Trim(), CultureInfo.InvariantCulture);
            }

            return result;
        }

        private static Type GetExpectedTypeFromProperty(SerializedProperty prop)
        {
            var typeName = prop.type;
            var start = typeName.IndexOf('<');
            var end = typeName.IndexOf('>');
            if (start < 0 || end <= start)
            {
                return null;
            }

            var inner = typeName.Substring(start + 1, end - start - 1);
            if (inner.StartsWith("$", StringComparison.Ordinal))
            {
                inner = inner.Substring(1);
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var type = assemblies[i].GetType("UnityEngine." + inner);
                if (type != null)
                {
                    return type;
                }

                type = assemblies[i].GetType("UnityEngine.UI." + inner);
                if (type != null)
                {
                    return type;
                }

                type = assemblies[i].GetType("TMPro." + inner);
                if (type != null)
                {
                    return type;
                }

                type = assemblies[i].GetType(inner);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool TryGetOperations(CommandRequest request, out IList operations, out string error)
        {
            operations = null;
            error = null;

            object rawOps = null;
            if (request.@params != null)
            {
                if (request.@params.TryGetValue("ops", out rawOps) && rawOps is IList)
                {
                    operations = rawOps as IList;
                    return true;
                }

                object opsJson;
                if (request.@params.TryGetValue("opsJson", out opsJson) && opsJson != null)
                {
                    try
                    {
                        rawOps = AIBridgeJson.Deserialize(opsJson.ToString());
                    }
                    catch (Exception ex)
                    {
                        error = "Failed to parse opsJson: " + ex.Message;
                        return false;
                    }
                }
            }

            var dictionary = rawOps as IDictionary;
            if (dictionary != null)
            {
                rawOps = GetValue(dictionary, "ops");
                if (rawOps == null)
                {
                    rawOps = GetValue(dictionary, "operations");
                }
            }

            operations = rawOps as IList;
            if (operations == null)
            {
                error = "Missing patch operations. Use --ops <file> or --ops-json <json array>.";
                return false;
            }

            return true;
        }

        private static string GetTargetPath(IDictionary operation)
        {
            var target = GetDictionary(operation, "target");
            if (target != null)
            {
                var targetPath = GetString(target, "path");
                if (!string.IsNullOrEmpty(targetPath))
                {
                    return targetPath;
                }
            }

            return GetString(operation, "path");
        }

        private static bool SetOptionalGameObjectState(GameObject go, IDictionary operation)
        {
            var changed = false;
            if (HasKey(operation, "active"))
            {
                var active = GetBool(operation, "active", true);
                if (go.activeSelf != active)
                {
                    go.SetActive(active);
                    changed = true;
                }
            }

            if (HasKey(operation, "tag"))
            {
                var tag = GetString(operation, "tag");
                if (go.tag != tag)
                {
                    go.tag = tag;
                    changed = true;
                }
            }

            if (HasKey(operation, "layer"))
            {
                var layerValue = GetValue(operation, "layer");
                int layerIndex;
                if (int.TryParse(layerValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out layerIndex))
                {
                    if (go.layer != layerIndex)
                    {
                        go.layer = layerIndex;
                        changed = true;
                    }

                    return changed;
                }

                layerIndex = LayerMask.NameToLayer(layerValue.ToString());
                if (layerIndex < 0)
                {
                    throw new PatchException("Unknown layer: " + layerValue);
                }

                if (go.layer != layerIndex)
                {
                    go.layer = layerIndex;
                    changed = true;
                }
            }

            return changed;
        }

        private static void AddResult(PatchContext context, int index, string op, string path, bool changed, string message)
        {
            context.Results.Add(new PatchOperationResult
            {
                index = index,
                op = op,
                path = path,
                changed = changed,
                message = message
            });
        }

        private static PrefabHierarchyNode BuildHierarchySnapshot(GameObject go, string path, int remainingDepth)
        {
            var node = new PrefabHierarchyNode
            {
                name = go.name,
                path = path,
                active = go.activeSelf,
                childCount = go.transform.childCount,
                components = new List<string>(),
                children = new List<PrefabHierarchyNode>()
            };

            var components = go.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] != null)
                {
                    node.components.Add(components[i].GetType().Name);
                }
            }

            if (remainingDepth <= 0)
            {
                return node;
            }

            foreach (Transform child in go.transform)
            {
                node.children.Add(BuildHierarchySnapshot(child.gameObject, path + "/" + child.name, remainingDepth - 1));
            }

            return node;
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

        private static bool IsNullReferenceValue(object value)
        {
            if (value == null)
            {
                return true;
            }

            var text = value.ToString();
            return string.IsNullOrEmpty(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase);
        }

        private static IDictionary GetDictionary(IDictionary dictionary, string key)
        {
            return GetValue(dictionary, key) as IDictionary;
        }

        private static IList GetList(IDictionary dictionary, string key)
        {
            return GetValue(dictionary, key) as IList;
        }

        private static string GetString(IDictionary dictionary, string key)
        {
            var value = GetValue(dictionary, key);
            return value != null ? value.ToString() : null;
        }

        private static int GetInt(IDictionary dictionary, string key, int defaultValue)
        {
            var value = GetValue(dictionary, key);
            if (value == null)
            {
                return defaultValue;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(IDictionary dictionary, string key, bool defaultValue)
        {
            var value = GetValue(dictionary, key);
            if (value == null)
            {
                return defaultValue;
            }

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static bool HasKey(IDictionary dictionary, string key)
        {
            return GetValue(dictionary, key) != null;
        }

        private static object GetValue(IDictionary dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key != null && string.Equals(entry.Key.ToString(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private sealed class PatchContext
        {
            public string PrefabPath;
            public GameObject Root;
            public bool DryRun;
            public bool Changed;
            public readonly List<PatchOperationResult> Results = new List<PatchOperationResult>();
        }

        private sealed class SerializedTarget
        {
            public string Path;
            public UnityEngine.Object TargetObject;
        }

        [Serializable]
        private sealed class PatchOperationResult
        {
            public int index;
            public string op;
            public string path;
            public bool changed;
            public string message;
        }

        [Serializable]
        private sealed class PrefabHierarchyNode
        {
            public string name;
            public string path;
            public bool active;
            public int childCount;
            public List<string> components;
            public List<PrefabHierarchyNode> children;
        }

        private sealed class PatchException : Exception
        {
            public PatchException(string message) : base(message)
            {
            }
        }
    }
}
