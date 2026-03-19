using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AIBridge.Internal.Json
{
    internal static class AIBridgeJson
    {
        public static Dictionary<string, object> DeserializeObject(string json)
        {
            var value = Deserialize(json);
            return value as Dictionary<string, object>;
        }

        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            return Parser.Parse(json);
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

            private Parser(string json)
            {
                _json = json;
            }

            public static object Parse(string json)
            {
                var parser = new Parser(json);
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
                        return ParseObject();
                    case '[':
                        return ParseArray();
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
                if (isFloat)
                {
                    return double.Parse(numberText, CultureInfo.InvariantCulture);
                }

                return long.Parse(numberText, CultureInfo.InvariantCulture);
            }

            private void ConsumeDigits()
            {
                var start = _index;
                while (!IsAtEnd && char.IsDigit(_json[_index]))
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
                    if (IsAtEnd || _json[_index + i] != literal[i])
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
                while (!IsAtEnd && char.IsWhiteSpace(_json[_index]))
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
                    if (entry.Value == null)
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
