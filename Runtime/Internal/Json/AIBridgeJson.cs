using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AIBridge.Internal.Json
{
    public static class AIBridgeJson
    {
        public static Dictionary<string, object> DeserializeObject(string json)
        {
            var value = Deserialize(json);
            return value as Dictionary<string, object>;
        }

        /// <summary>用于外部协议/持久状态的有界解析；拒绝歧义重复键，不改变既有宽松入口。</summary>
        public static Dictionary<string, object> DeserializeObjectStrict(string json, int maxDepth = 64)
        {
            if (maxDepth < 1 || maxDepth > 256) throw new ArgumentOutOfRangeException(nameof(maxDepth));
            if (string.IsNullOrEmpty(json)) return null;
            return Parser.Parse(json, maxDepth, true) as Dictionary<string, object>;
        }

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            return Parser.Parse(json);
        }

        // 仅协议结果树保留对象中的显式 null；普通 DTO 继续省略空字段，避免改变既有线协议。
        private sealed class NullPreservingDictionary : Dictionary<string, object> { }

        public static object PreserveObjectNulls(object value)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                var preserved = new NullPreservingDictionary();
                foreach (var item in dictionary) preserved.Add(item.Key, PreserveObjectNulls(item.Value));
                return preserved;
            }
            if (value is IList list)
            {
                var preserved = new List<object>();
                foreach (var item in list) preserved.Add(PreserveObjectNulls(item));
                return preserved;
            }
            return value;
        }

        public static string Serialize(object value, bool pretty = false)
        {
            var builder = new StringBuilder();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Serializer.SerializeValue(value, builder, pretty, 0, visited);
            return builder.ToString();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;
            private int _depth;
            private readonly int _maxDepth;
            private readonly bool _rejectDuplicateKeys;

            private Parser(string json, int maxDepth, bool rejectDuplicateKeys)
            {
                _json = json;
                _maxDepth = maxDepth;
                _rejectDuplicateKeys = rejectDuplicateKeys;
            }

            public static object Parse(string json, int maxDepth = int.MaxValue, bool rejectDuplicateKeys = false)
            {
                var parser = new Parser(json, maxDepth, rejectDuplicateKeys);
                var value = parser.ParseValue();
                parser.SkipWhitespace();
                if (!parser.IsAtEnd)
                {
                    throw new FormatException("Unexpected trailing characters in JSON.");
                }

                return value;
            }

            private bool IsAtEnd => _index >= _json.Length;

            private object ParseValue()
            {
                SkipWhitespace();
                if (IsAtEnd)
                {
                    throw new FormatException("Unexpected end of JSON input.");
                }

                switch (_json[_index])
                {
                    case '{':
                        if (++_depth > _maxDepth) throw new FormatException("JSON nesting limit exceeded.");
                        try { return ParseObject(); }
                        finally { _depth--; }
                    case '[':
                        if (++_depth > _maxDepth) throw new FormatException("JSON nesting limit exceeded.");
                        try { return ParseArray(); }
                        finally { _depth--; }
                    case '"':
                        return ParseString();
                    case 't':
                        ConsumeLiteral("true");
                        return true;
                    case 'f':
                        ConsumeLiteral("false");
                        return false;
                    case 'n':
                        ConsumeLiteral("null");
                        return null;
                    default:
                        return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                Consume('{');
                SkipWhitespace();

                if (TryConsume('}'))
                {
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    Consume(':');
                    if (_rejectDuplicateKeys && result.ContainsKey(key))
                        throw new FormatException("Duplicate JSON key.");
                    result[key] = ParseValue();
                    SkipWhitespace();

                    if (TryConsume('}'))
                    {
                        return result;
                    }

                    Consume(',');
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                Consume('[');
                SkipWhitespace();

                if (TryConsume(']'))
                {
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();

                    if (TryConsume(']'))
                    {
                        return result;
                    }

                    Consume(',');
                }
            }

            private string ParseString()
            {
                Consume('"');
                var builder = new StringBuilder();

                while (!IsAtEnd)
                {
                    var c = _json[_index++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c != '\\')
                    {
                        if (_rejectDuplicateKeys && c < 0x20) throw new FormatException("Unescaped JSON control character.");
                        builder.Append(c);
                        continue;
                    }

                    if (IsAtEnd)
                    {
                        throw new FormatException("Invalid escape sequence in JSON string.");
                    }

                    var escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            builder.Append(ParseUnicodeEscape());
                            break;
                        default:
                            throw new FormatException($"Unsupported escape sequence: \\{escaped}");
                    }
                }

                throw new FormatException("Unterminated JSON string.");
            }

            private char ParseUnicodeEscape()
            {
                if (_index + 4 > _json.Length)
                {
                    throw new FormatException("Invalid unicode escape in JSON string.");
                }

                var hex = _json.Substring(_index, 4);
                if (_rejectDuplicateKeys)
                    foreach (var c in hex)
                        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                            throw new FormatException("Invalid JSON unicode escape.");
                _index += 4;
                return (char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            private object ParseNumber()
            {
                var start = _index;

                if (_json[_index] == '-')
                {
                    _index++;
                }

                ConsumeDigits();

                var isFloat = false;
                if (!IsAtEnd && _json[_index] == '.')
                {
                    isFloat = true;
                    _index++;
                    ConsumeDigits();
                }

                if (!IsAtEnd && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    isFloat = true;
                    _index++;
                    if (!IsAtEnd && (_json[_index] == '+' || _json[_index] == '-'))
                    {
                        _index++;
                    }

                    ConsumeDigits();
                }

                var numberText = _json.Substring(start, _index - start);
                var integerStart = numberText.StartsWith("-", StringComparison.Ordinal) ? 1 : 0;
                if (_rejectDuplicateKeys && numberText.Length > integerStart + 1
                    && numberText[integerStart] == '0' && char.IsDigit(numberText[integerStart + 1]))
                    throw new FormatException("JSON leading zero is invalid.");
                if (isFloat)
                {
                    if (_rejectDuplicateKeys && !double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw new FormatException("JSON number exceeds supported range.");
                    var number = double.Parse(numberText, CultureInfo.InvariantCulture);
                    if (_rejectDuplicateKeys && (double.IsNaN(number) || double.IsInfinity(number)))
                        throw new FormatException("JSON number exceeds supported range.");
                    return number;
                }

                if (_rejectDuplicateKeys && !long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new FormatException("JSON integer exceeds supported range.");
                return long.Parse(numberText, CultureInfo.InvariantCulture);
            }

            private void ConsumeDigits()
            {
                var start = _index;
                while (!IsAtEnd && (_rejectDuplicateKeys
                    ? _json[_index] >= '0' && _json[_index] <= '9'
                    : char.IsDigit(_json[_index])))
                {
                    _index++;
                }

                if (start == _index)
                {
                    throw new FormatException("Invalid JSON number.");
                }
            }

            private void ConsumeLiteral(string literal)
            {
                for (var i = 0; i < literal.Length; i++)
                {
                    if (_index + i >= _json.Length || _json[_index + i] != literal[i])
                    {
                        throw new FormatException($"Expected '{literal}' in JSON.");
                    }
                }

                _index += literal.Length;
            }

            private void Consume(char expected)
            {
                SkipWhitespace();
                if (IsAtEnd || _json[_index] != expected)
                {
                    throw new FormatException($"Expected '{expected}' in JSON.");
                }

                _index++;
            }

            private bool TryConsume(char expected)
            {
                SkipWhitespace();
                if (!IsAtEnd && _json[_index] == expected)
                {
                    _index++;
                    return true;
                }

                return false;
            }

            private void SkipWhitespace()
            {
                while (!IsAtEnd && (_rejectDuplicateKeys
                    ? _json[_index] == ' ' || _json[_index] == '\t' || _json[_index] == '\r' || _json[_index] == '\n'
                    : char.IsWhiteSpace(_json[_index])))
                {
                    _index++;
                }
            }
        }

        private static class Serializer
        {
            public static void SerializeValue(object value, StringBuilder builder, bool pretty, int depth, HashSet<object> visited)
            {
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                if (value is string str)
                {
                    SerializeString(str, builder);
                    return;
                }

                if (value is bool boolValue)
                {
                    builder.Append(boolValue ? "true" : "false");
                    return;
                }

                if (value is Enum)
                {
                    SerializeString(value.ToString(), builder);
                    return;
                }

                if (IsNumeric(value))
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }

                if (value is IDictionary dictionary)
                {
                    SerializeDictionary(dictionary, builder, pretty, depth, visited);
                    return;
                }

                if (value is IEnumerable enumerable && !(value is string))
                {
                    SerializeEnumerable(enumerable, builder, pretty, depth, visited);
                    return;
                }

                SerializeObject(value, builder, pretty, depth, visited);
            }

            private static void SerializeDictionary(IDictionary dictionary, StringBuilder builder, bool pretty, int depth, HashSet<object> visited)
            {
                if (!TryEnterReference(dictionary, visited))
                {
                    builder.Append("null");
                    return;
                }

                builder.Append('{');
                var wroteAny = false;

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value == null && !(dictionary is NullPreservingDictionary))
                    {
                        continue;
                    }

                    WriteSeparator(builder, pretty, depth, ref wroteAny);
                    SerializeString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), builder);
                    builder.Append(pretty ? ": " : ":");
                    SerializeValue(entry.Value, builder, pretty, depth + 1, visited);
                }

                WriteClosingBrace(builder, pretty, depth, wroteAny, '}');
                visited.Remove(dictionary);
            }

            private static void SerializeEnumerable(IEnumerable enumerable, StringBuilder builder, bool pretty, int depth, HashSet<object> visited)
            {
                if (!TryEnterReference(enumerable, visited))
                {
                    builder.Append("null");
                    return;
                }

                builder.Append('[');
                var wroteAny = false;

                foreach (var item in enumerable)
                {
                    WriteSeparator(builder, pretty, depth, ref wroteAny);
                    SerializeValue(item, builder, pretty, depth + 1, visited);
                }

                WriteClosingBrace(builder, pretty, depth, wroteAny, ']');
                visited.Remove(enumerable);
            }

            private static void SerializeObject(object value, StringBuilder builder, bool pretty, int depth, HashSet<object> visited)
            {
                if (!TryEnterReference(value, visited))
                {
                    builder.Append("null");
                    return;
                }

                builder.Append('{');
                var wroteAny = false;
                var type = value.GetType();

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var fieldValue = field.GetValue(value);
                    if (fieldValue == null)
                    {
                        continue;
                    }

                    WriteSeparator(builder, pretty, depth, ref wroteAny);
                    SerializeString(field.Name, builder);
                    builder.Append(pretty ? ": " : ":");
                    SerializeValue(fieldValue, builder, pretty, depth + 1, visited);
                }

                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    var propertyValue = property.GetValue(value, null);
                    if (propertyValue == null)
                    {
                        continue;
                    }

                    WriteSeparator(builder, pretty, depth, ref wroteAny);
                    SerializeString(property.Name, builder);
                    builder.Append(pretty ? ": " : ":");
                    SerializeValue(propertyValue, builder, pretty, depth + 1, visited);
                }

                WriteClosingBrace(builder, pretty, depth, wroteAny, '}');
                visited.Remove(value);
            }

            private static bool TryEnterReference(object value, HashSet<object> visited)
            {
                var type = value.GetType();
                if (type.IsValueType)
                {
                    return true;
                }

                return visited.Add(value);
            }

            private static void WriteSeparator(StringBuilder builder, bool pretty, int depth, ref bool wroteAny)
            {
                if (wroteAny)
                {
                    builder.Append(',');
                }

                if (pretty)
                {
                    builder.Append('\n');
                    builder.Append(' ', (depth + 1) * 2);
                }

                wroteAny = true;
            }

            private static void WriteClosingBrace(StringBuilder builder, bool pretty, int depth, bool wroteAny, char closing)
            {
                if (pretty && wroteAny)
                {
                    builder.Append('\n');
                    builder.Append(' ', depth * 2);
                }

                builder.Append(closing);
            }

            private static void SerializeString(string value, StringBuilder builder)
            {
                builder.Append('"');
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 32)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(c);
                            }
                            break;
                    }
                }

                builder.Append('"');
            }

            private static bool IsNumeric(object value)
            {
                switch (Type.GetTypeCode(value.GetType()))
                {
                    case TypeCode.Byte:
                    case TypeCode.SByte:
                    case TypeCode.UInt16:
                    case TypeCode.UInt32:
                    case TypeCode.UInt64:
                    case TypeCode.Int16:
                    case TypeCode.Int32:
                    case TypeCode.Int64:
                    case TypeCode.Decimal:
                    case TypeCode.Double:
                    case TypeCode.Single:
                        return true;
                    default:
                        return false;
                }
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return obj == null ? 0 : obj.GetHashCode();
            }
        }
    }
}
