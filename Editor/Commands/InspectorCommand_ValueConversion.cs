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
        private static bool TryGetInt(object value, out int result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            try
            {
                if (value is int intValue)
                {
                    result = intValue;
                    return true;
                }

                if (value is long longValue)
                {
                    result = (int)longValue;
                    return true;
                }

                if (value is double doubleValue)
                {
                    result = (int)doubleValue;
                    return true;
                }

                if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetFloat(object value, out float result)
        {
            result = 0f;
            if (value == null)
            {
                return false;
            }

            try
            {
                if (value is float floatValue)
                {
                    result = floatValue;
                    return true;
                }

                if (value is double doubleValue)
                {
                    result = (float)doubleValue;
                    return true;
                }

                if (value is long longValue)
                {
                    result = longValue;
                    return true;
                }

                if (float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetEnumValue<TEnum>(object value, out TEnum result) where TEnum : struct
        {
            result = default;
            if (value == null)
            {
                return false;
            }

            if (value is TEnum enumValue)
            {
                result = enumValue;
                return true;
            }

            if (TryGetInt(value, out var intValue))
            {
                result = (TEnum)Enum.ToObject(typeof(TEnum), intValue);
                return true;
            }

            return Enum.TryParse(value.ToString(), true, out result);
        }

        private static bool TryGetCharacter(object value, out int result)
        {
            result = 0;
            if (TryGetInt(value, out result))
            {
                return true;
            }

            var text = value != null ? value.ToString() : null;
            if (text != null && text.Length == 1)
            {
                result = text[0];
                return true;
            }

            return false;
        }

        private static bool TryGetVector2(object value, out Vector2 result)
        {
            result = Vector2.zero;
            if (!TryReadFloatList(value, Vector2ElementCount, out var values))
            {
                return false;
            }

            result = new Vector2(values[0], values[1]);
            return true;
        }

        private static bool TryGetVector3(object value, out Vector3 result)
        {
            result = Vector3.zero;
            if (!TryReadFloatList(value, Vector3ElementCount, out var values))
            {
                return false;
            }

            result = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryGetVector4(object value, out Vector4 result)
        {
            result = Vector4.zero;
            if (!TryReadFloatList(value, Vector4ElementCount, out var values))
            {
                return false;
            }

            result = new Vector4(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetColor(object value, out Color result)
        {
            result = Color.white;
            if (TryReadFloatList(value, Vector4ElementCount, out var values))
            {
                result = new Color(values[0], values[1], values[2], values[3]);
                return true;
            }

            if (TryReadRgbList(value, out values))
            {
                result = new Color(values[0], values[1], values[2], 1f);
                return true;
            }

            return false;
        }

        private static bool TryGetRect(object value, out Rect result)
        {
            result = Rect.zero;
            if (!TryReadFloatList(value, Vector4ElementCount, out var values))
            {
                return false;
            }

            result = new Rect(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetQuaternion(object value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (!TryReadFloatList(value, Vector4ElementCount, out var values))
            {
                return false;
            }

            result = new Quaternion(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetVector2Int(object value, out Vector2Int result)
        {
            result = Vector2Int.zero;
            if (!TryReadIntList(value, Vector2ElementCount, out var values))
            {
                return false;
            }

            result = new Vector2Int(values[0], values[1]);
            return true;
        }

        private static bool TryGetVector3Int(object value, out Vector3Int result)
        {
            result = Vector3Int.zero;
            if (!TryReadIntList(value, Vector3ElementCount, out var values))
            {
                return false;
            }

            result = new Vector3Int(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryGetRectInt(object value, out RectInt result)
        {
            result = new RectInt();
            if (!TryReadIntList(value, Vector4ElementCount, out var values))
            {
                return false;
            }

            result = new RectInt(values[0], values[1], values[2], values[3]);
            return true;
        }

        private static bool TryGetBounds(object value, out Bounds result)
        {
            result = new Bounds();
            if (value is IDictionary dictionary)
            {
                var centerValue = GetDictionaryValue(dictionary, "center");
                var sizeValue = GetDictionaryValue(dictionary, "size");
                if (TryGetVector3(centerValue, out var center) && TryGetVector3(sizeValue, out var size))
                {
                    result = new Bounds(center, size);
                    return true;
                }
            }

            if (TryReadFloatList(value, BoundsElementCount, out var values))
            {
                result = new Bounds(
                    new Vector3(values[0], values[1], values[2]),
                    new Vector3(values[3], values[4], values[5]));
                return true;
            }

            return false;
        }

        private static bool TryGetBoundsInt(object value, out BoundsInt result)
        {
            result = new BoundsInt();
            if (value is IDictionary dictionary)
            {
                var positionValue = GetDictionaryValue(dictionary, "position");
                if (positionValue == null)
                {
                    positionValue = GetDictionaryValue(dictionary, "center");
                }

                var sizeValue = GetDictionaryValue(dictionary, "size");
                if (TryGetVector3Int(positionValue, out var position) && TryGetVector3Int(sizeValue, out var size))
                {
                    result = new BoundsInt(position, size);
                    return true;
                }
            }

            if (TryReadIntList(value, BoundsElementCount, out var values))
            {
                result = new BoundsInt(
                    new Vector3Int(values[0], values[1], values[2]),
                    new Vector3Int(values[3], values[4], values[5]));
                return true;
            }

            return false;
        }

        private static bool TryReadFloatList(object value, int expectedCount, out float[] values)
        {
            values = null;
            var collected = new List<float>();

            if (value is IDictionary dictionary)
            {
                var keySets = GetFloatKeySets(expectedCount);
                for (var setIndex = 0; setIndex < keySets.Length; setIndex++)
                {
                    collected.Clear();
                    var keys = keySets[setIndex];
                    var success = true;

                    foreach (var key in keys)
                    {
                        if (!TryGetFloat(GetDictionaryValue(dictionary, key), out var number))
                        {
                            success = false;
                            break;
                        }

                        collected.Add(number);
                    }

                    if (success)
                    {
                        values = collected.ToArray();
                        return true;
                    }
                }
            }

            if (value is IList list)
            {
                if (list.Count != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (!TryGetFloat(list[i], out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            var text = value != null ? value.ToString() : null;
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Trim().Trim('(', ')', '[', ']');
                var parts = text.Split(',');
                if (parts.Length != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < parts.Length; i++)
                {
                    if (!TryGetFloat(parts[i].Trim(), out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            return false;
        }

        private static bool TryReadIntList(object value, int expectedCount, out int[] values)
        {
            values = null;
            var collected = new List<int>();

            if (value is IDictionary dictionary)
            {
                var keySets = GetFloatKeySets(expectedCount);
                for (var setIndex = 0; setIndex < keySets.Length; setIndex++)
                {
                    collected.Clear();
                    var keys = keySets[setIndex];
                    var success = true;

                    foreach (var key in keys)
                    {
                        if (!TryGetInt(GetDictionaryValue(dictionary, key), out var number))
                        {
                            success = false;
                            break;
                        }

                        collected.Add(number);
                    }

                    if (success)
                    {
                        values = collected.ToArray();
                        return true;
                    }
                }
            }

            if (value is IList list)
            {
                if (list.Count != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (!TryGetInt(list[i], out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            var text = value != null ? value.ToString() : null;
            if (!string.IsNullOrEmpty(text))
            {
                text = text.Trim().Trim('(', ')', '[', ']');
                var parts = text.Split(',');
                if (parts.Length != expectedCount)
                {
                    return false;
                }

                for (var i = 0; i < parts.Length; i++)
                {
                    if (!TryGetInt(parts[i].Trim(), out var number))
                    {
                        return false;
                    }

                    collected.Add(number);
                }

                values = collected.ToArray();
                return true;
            }

            return false;
        }

        private static bool TryReadRgbList(object value, out float[] values)
        {
            values = null;
            if (value is IDictionary dictionary)
            {
                if (TryGetFloat(GetDictionaryValue(dictionary, "r"), out var r)
                    && TryGetFloat(GetDictionaryValue(dictionary, "g"), out var g)
                    && TryGetFloat(GetDictionaryValue(dictionary, "b"), out var b))
                {
                    values = new[] { r, g, b };
                    return true;
                }

                return false;
            }

            return TryReadFloatList(value, Vector3ElementCount, out values);
        }

        private static string[][] GetFloatKeySets(int expectedCount)
        {
            if (expectedCount == Vector2ElementCount)
            {
                return new[] { new[] { "x", "y" } };
            }

            if (expectedCount == Vector3ElementCount)
            {
                return new[] { new[] { "x", "y", "z" } };
            }

            if (expectedCount == Vector4ElementCount)
            {
                return new[]
                {
                    new[] { "x", "y", "z", "w" },
                    new[] { "r", "g", "b", "a" },
                    new[] { "x", "y", "width", "height" }
                };
            }

            return new string[0][];
        }

        private static object GetDictionaryValue(IDictionary dictionary, string key)
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

        /// <summary>
        /// Extract expected type from SerializedProperty.type (e.g. "PPtr<$Sprite>" -> Sprite).
        /// </summary>
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
            if (inner.StartsWith("$"))
            {
                inner = inner.Substring(1);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType($"UnityEngine.{inner}");
                if (type != null)
                {
                    return type;
                }

                type = assembly.GetType($"UnityEngine.UI.{inner}");
                if (type != null)
                {
                    return type;
                }

                type = assembly.GetType(inner);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
