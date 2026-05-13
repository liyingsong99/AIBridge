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
        private bool TryGetValuesDictionary(CommandRequest request, out Dictionary<string, object> values, out string error)
        {
            values = null;
            error = null;

            var rawValues = request.GetParam<object>("values", null);
            if (rawValues == null)
            {
                error = "Missing 'values' parameter";
                return false;
            }

            if (rawValues is Dictionary<string, object> typedValues)
            {
                values = typedValues;
                if (values.Count > 0)
                {
                    return true;
                }

                error = "Parameter 'values' cannot be empty";
                return false;
            }

            if (rawValues is IDictionary dictionary)
            {
                values = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null)
                    {
                        continue;
                    }

                    values[entry.Key.ToString()] = entry.Value;
                }

                if (values.Count > 0)
                {
                    return true;
                }

                error = "Parameter 'values' cannot be empty";
                return false;
            }

            var valuesText = rawValues as string;
            if (!string.IsNullOrEmpty(valuesText))
            {
                try
                {
                    values = AIBridgeJson.DeserializeObject(valuesText);
                    if (values != null && values.Count > 0)
                    {
                        return true;
                    }

                    error = "Parameter 'values' cannot be empty";
                    return false;
                }
                catch (Exception ex)
                {
                    error = $"Invalid values JSON: {ex.Message}";
                    return false;
                }
            }

            error = "Parameter 'values' must be a JSON object";
            return false;
        }

        private PropertyInfo BuildPropertyInfo(SerializedProperty prop)
        {
            return new PropertyInfo
            {
                name = prop.name,
                propertyPath = prop.propertyPath,
                displayName = prop.displayName,
                propertyType = prop.propertyType.ToString(),
                editable = prop.editable,
                isExpanded = prop.isExpanded,
                hasChildren = prop.hasChildren,
                depth = prop.depth,
                value = GetPropertyValue(prop)
            };
        }

        private object GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    return new { r = prop.colorValue.r, g = prop.colorValue.g, b = prop.colorValue.b, a = prop.colorValue.a };
                case SerializedPropertyType.ObjectReference:
                    return BuildObjectReferenceValue(prop.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.Enum:
                    return prop.enumNames[prop.enumValueIndex];
                case SerializedPropertyType.Vector2:
                    return new { x = prop.vector2Value.x, y = prop.vector2Value.y };
                case SerializedPropertyType.Vector3:
                    return new { x = prop.vector3Value.x, y = prop.vector3Value.y, z = prop.vector3Value.z };
                case SerializedPropertyType.Vector4:
                    return new { x = prop.vector4Value.x, y = prop.vector4Value.y, z = prop.vector4Value.z, w = prop.vector4Value.w };
                case SerializedPropertyType.Rect:
                    return new { x = prop.rectValue.x, y = prop.rectValue.y, width = prop.rectValue.width, height = prop.rectValue.height };
                case SerializedPropertyType.ArraySize:
                    return prop.intValue;
                case SerializedPropertyType.Character:
                    return new { code = prop.intValue, value = ((char)prop.intValue).ToString() };
                case SerializedPropertyType.AnimationCurve:
                    return BuildAnimationCurveValue(prop.animationCurveValue);
                case SerializedPropertyType.Bounds:
                    return new
                    {
                        center = new { x = prop.boundsValue.center.x, y = prop.boundsValue.center.y, z = prop.boundsValue.center.z },
                        size = new { x = prop.boundsValue.size.x, y = prop.boundsValue.size.y, z = prop.boundsValue.size.z }
                    };
                case SerializedPropertyType.Gradient:
                    return BuildGradientValue(prop.gradientValue);
                case SerializedPropertyType.Quaternion:
                    return new { x = prop.quaternionValue.x, y = prop.quaternionValue.y, z = prop.quaternionValue.z, w = prop.quaternionValue.w };
                case SerializedPropertyType.ExposedReference:
                    return BuildObjectReferenceValue(prop.exposedReferenceValue);
                case SerializedPropertyType.FixedBufferSize:
                    return prop.fixedBufferSize;
                case SerializedPropertyType.Vector2Int:
                    return new { x = prop.vector2IntValue.x, y = prop.vector2IntValue.y };
                case SerializedPropertyType.Vector3Int:
                    return new { x = prop.vector3IntValue.x, y = prop.vector3IntValue.y, z = prop.vector3IntValue.z };
                case SerializedPropertyType.RectInt:
                    return new { x = prop.rectIntValue.x, y = prop.rectIntValue.y, width = prop.rectIntValue.width, height = prop.rectIntValue.height };
                case SerializedPropertyType.BoundsInt:
                    return new
                    {
                        position = new { x = prop.boundsIntValue.position.x, y = prop.boundsIntValue.position.y, z = prop.boundsIntValue.position.z },
                        size = new { x = prop.boundsIntValue.size.x, y = prop.boundsIntValue.size.y, z = prop.boundsIntValue.size.z }
                    };
                case SerializedPropertyType.ManagedReference:
                    return BuildManagedReferenceValue(prop);
#if UNITY_2022_1_OR_NEWER
                case SerializedPropertyType.Hash128:
                    return prop.hash128Value.ToString();
#endif
                default:
                    return prop.propertyType.ToString();
            }
        }

        private bool TrySetPropertyValue(SerializedProperty prop, object value, out string error)
        {
            error = null;
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Float:
                        if (!TryGetFloat(value, out var floatValue)) return false;
                        prop.floatValue = floatValue;
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value != null ? value.ToString() : string.Empty;
                        return true;
                    case SerializedPropertyType.Enum:
                        if (!SetEnumValue(prop, value))
                        {
                            error = "Invalid enum value";
                            return false;
                        }
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        UnityEngine.Object objectReference;
                        if (!TryResolveObjectReference(value, prop, out objectReference, out error))
                        {
                            return false;
                        }
                        prop.objectReferenceValue = objectReference;
                        return true;
                    case SerializedPropertyType.LayerMask:
                        prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Vector2:
                        if (!TryGetVector2(value, out var vector2Value))
                        {
                            error = "Expected Vector2 as {x,y}, [x,y], or 'x,y'";
                            return false;
                        }
                        prop.vector2Value = vector2Value;
                        return true;
                    case SerializedPropertyType.Vector3:
                        if (!TryGetVector3(value, out var vector3Value))
                        {
                            error = "Expected Vector3 as {x,y,z}, [x,y,z], or 'x,y,z'";
                            return false;
                        }
                        prop.vector3Value = vector3Value;
                        return true;
                    case SerializedPropertyType.Vector4:
                        if (!TryGetVector4(value, out var vector4Value))
                        {
                            error = "Expected Vector4 as {x,y,z,w}, [x,y,z,w], or 'x,y,z,w'";
                            return false;
                        }
                        prop.vector4Value = vector4Value;
                        return true;
                    case SerializedPropertyType.Color:
                        if (!TryGetColor(value, out var colorValue))
                        {
                            error = "Expected Color as {r,g,b,a}, [r,g,b,a], or 'r,g,b,a'";
                            return false;
                        }
                        prop.colorValue = colorValue;
                        return true;
                    case SerializedPropertyType.Rect:
                        if (!TryGetRect(value, out var rectValue))
                        {
                            error = "Expected Rect as {x,y,width,height}, [x,y,width,height], or 'x,y,width,height'";
                            return false;
                        }
                        prop.rectValue = rectValue;
                        return true;
                    case SerializedPropertyType.ArraySize:
                        prop.intValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    case SerializedPropertyType.Character:
                        if (!TryGetCharacter(value, out var characterValue))
                        {
                            error = "Expected Character as a single character or character code";
                            return false;
                        }
                        prop.intValue = characterValue;
                        return true;
                    case SerializedPropertyType.AnimationCurve:
                        if (!TryGetAnimationCurve(value, out var animationCurveValue))
                        {
                            error = "Expected AnimationCurve as {keys:[{time,value,inTangent,outTangent}],preWrapMode,postWrapMode}";
                            return false;
                        }
                        prop.animationCurveValue = animationCurveValue;
                        return true;
                    case SerializedPropertyType.Bounds:
                        if (!TryGetBounds(value, out var boundsValue))
                        {
                            error = "Expected Bounds as {center,size} or [centerX,centerY,centerZ,sizeX,sizeY,sizeZ]";
                            return false;
                        }
                        prop.boundsValue = boundsValue;
                        return true;
                    case SerializedPropertyType.Gradient:
                        if (!TryGetGradient(value, out var gradientValue))
                        {
                            error = "Expected Gradient as {colorKeys:[{time,color}],alphaKeys:[{time,alpha}],mode}";
                            return false;
                        }
                        prop.gradientValue = gradientValue;
                        return true;
                    case SerializedPropertyType.Quaternion:
                        if (!TryGetQuaternion(value, out var quaternionValue))
                        {
                            error = "Expected Quaternion as {x,y,z,w}, [x,y,z,w], or 'x,y,z,w'";
                            return false;
                        }
                        prop.quaternionValue = quaternionValue;
                        return true;
                    case SerializedPropertyType.ExposedReference:
                        UnityEngine.Object exposedReference;
                        if (!TryResolveObjectReference(value, prop, out exposedReference, out error))
                        {
                            return false;
                        }
                        prop.exposedReferenceValue = exposedReference;
                        return true;
                    case SerializedPropertyType.Vector2Int:
                        if (!TryGetVector2Int(value, out var vector2IntValue))
                        {
                            error = "Expected Vector2Int as {x,y}, [x,y], or 'x,y'";
                            return false;
                        }
                        prop.vector2IntValue = vector2IntValue;
                        return true;
                    case SerializedPropertyType.Vector3Int:
                        if (!TryGetVector3Int(value, out var vector3IntValue))
                        {
                            error = "Expected Vector3Int as {x,y,z}, [x,y,z], or 'x,y,z'";
                            return false;
                        }
                        prop.vector3IntValue = vector3IntValue;
                        return true;
                    case SerializedPropertyType.RectInt:
                        if (!TryGetRectInt(value, out var rectIntValue))
                        {
                            error = "Expected RectInt as {x,y,width,height}, [x,y,width,height], or 'x,y,width,height'";
                            return false;
                        }
                        prop.rectIntValue = rectIntValue;
                        return true;
                    case SerializedPropertyType.BoundsInt:
                        if (!TryGetBoundsInt(value, out var boundsIntValue))
                        {
                            error = "Expected BoundsInt as {position,size} or [posX,posY,posZ,sizeX,sizeY,sizeZ]";
                            return false;
                        }
                        prop.boundsIntValue = boundsIntValue;
                        return true;
                    case SerializedPropertyType.ManagedReference:
                        if (IsExplicitNullReferenceValue(value))
                        {
                            prop.managedReferenceValue = null;
                            return true;
                        }
                        error = "ManagedReference only supports clearing with null through this command";
                        return false;
#if UNITY_2022_1_OR_NEWER
                    case SerializedPropertyType.Hash128:
                        var hashText = value != null ? value.ToString() : null;
                        if (string.IsNullOrEmpty(hashText))
                        {
                            error = "Expected Hash128 as a " + Hash128HexLength + "-character hex string";
                            return false;
                        }
                        prop.hash128Value = Hash128.Parse(hashText);
                        return true;
#endif
                    default:
                        error = $"Unsupported property type: {prop.propertyType}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool SetEnumValue(SerializedProperty prop, object value)
        {
            if (TryGetInt(value, out var enumIndex))
            {
                if (enumIndex < 0 || enumIndex >= prop.enumNames.Length)
                {
                    return false;
                }

                prop.enumValueIndex = enumIndex;
                return true;
            }

            var enumName = value != null ? value.ToString() : null;
            for (var i = 0; i < prop.enumNames.Length; i++)
            {
                if (prop.enumNames[i] == enumName)
                {
                    prop.enumValueIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static object BuildObjectReferenceValue(UnityEngine.Object objectReference)
        {
            if (objectReference == null)
            {
                return null;
            }

            return new
            {
                name = objectReference.name,
                type = objectReference.GetType().Name,
                instanceId = objectReference.GetInstanceID(),
                assetPath = AssetDatabase.GetAssetPath(objectReference)
            };
        }

        private static object BuildAnimationCurveValue(AnimationCurve curve)
        {
            if (curve == null)
            {
                return null;
            }

            var keys = new List<object>();
            var curveKeys = curve.keys;
            for (var i = 0; i < curveKeys.Length; i++)
            {
                var key = curveKeys[i];
                keys.Add(new
                {
                    time = key.time,
                    value = key.value,
                    inTangent = key.inTangent,
                    outTangent = key.outTangent,
                    inWeight = key.inWeight,
                    outWeight = key.outWeight,
                    weightedMode = key.weightedMode.ToString()
                });
            }

            return new
            {
                preWrapMode = curve.preWrapMode.ToString(),
                postWrapMode = curve.postWrapMode.ToString(),
                keys = keys
            };
        }

        private static object BuildGradientValue(Gradient gradient)
        {
            if (gradient == null)
            {
                return null;
            }

            var colorKeys = new List<object>();
            var gradientColorKeys = gradient.colorKeys;
            for (var i = 0; i < gradientColorKeys.Length; i++)
            {
                var key = gradientColorKeys[i];
                colorKeys.Add(new
                {
                    time = key.time,
                    color = new { r = key.color.r, g = key.color.g, b = key.color.b, a = key.color.a }
                });
            }

            var alphaKeys = new List<object>();
            var gradientAlphaKeys = gradient.alphaKeys;
            for (var i = 0; i < gradientAlphaKeys.Length; i++)
            {
                var key = gradientAlphaKeys[i];
                alphaKeys.Add(new
                {
                    time = key.time,
                    alpha = key.alpha
                });
            }

            return new
            {
                mode = gradient.mode.ToString(),
                colorKeys = colorKeys,
                alphaKeys = alphaKeys
            };
        }

        private static object BuildManagedReferenceValue(SerializedProperty prop)
        {
            return new
            {
                fullTypeName = prop.managedReferenceFullTypename,
                isNull = string.IsNullOrEmpty(prop.managedReferenceFullTypename)
            };
        }

        private bool TryResolveObjectReference(object value, SerializedProperty prop, out UnityEngine.Object resolved, out string error)
        {
            resolved = null;
            error = null;

            if (IsExplicitNullReferenceValue(value))
            {
                return true;
            }

            if (value is double doubleId)
            {
                var id = (int)doubleId;
                return TryResolveObjectReferenceByInstanceId(id, out resolved, out error);
            }

            if (value is long longId)
            {
                var id = (int)longId;
                return TryResolveObjectReferenceByInstanceId(id, out resolved, out error);
            }

            if (value is int intId)
            {
                return TryResolveObjectReferenceByInstanceId(intId, out resolved, out error);
            }

            var str = value.ToString();
            var assetPath = str;
            if (!str.StartsWith(AssetsPathPrefix, StringComparison.OrdinalIgnoreCase)
                && !str.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(str);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    assetPath = guidPath;
                }
            }

            if (prop != null)
            {
                var expectedType = GetExpectedTypeFromProperty(prop);
                if (expectedType != null)
                {
                    var typed = AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
                    if (typed != null)
                    {
                        resolved = typed;
                        return true;
                    }
                }

                var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (allAssets != null && allAssets.Length > 1)
                {
                    var propType = prop.type;
                    foreach (var sub in allAssets)
                    {
                        if (sub == null) continue;
                        if (propType.Contains(sub.GetType().Name))
                        {
                            resolved = sub;
                            return true;
                        }
                    }
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null)
            {
                resolved = asset;
                return true;
            }

            var go = GameObject.Find(str);
            if (go != null)
            {
                resolved = go;
                return true;
            }

            error = $"Object reference target not found: {str}";
            return false;
        }

        private static bool TryResolveObjectReferenceByInstanceId(int instanceId, out UnityEngine.Object resolved, out string error)
        {
            resolved = GetObjectByInstanceId(instanceId);
            if (resolved != null)
            {
                error = null;
                return true;
            }

            error = $"Object reference instanceId not found: {instanceId}";
            return false;
        }

        private static bool IsExplicitNullReferenceValue(object value)
        {
            if (value == null)
            {
                return true;
            }

            if (value is int intValue)
            {
                return intValue == 0;
            }

            if (value is long longValue)
            {
                return longValue == 0;
            }

            if (value is double doubleValue)
            {
                return Math.Abs(doubleValue) < double.Epsilon;
            }

            var text = value.ToString();
            return string.IsNullOrEmpty(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase);
        }
    }
}
