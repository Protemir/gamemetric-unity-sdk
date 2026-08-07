using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameMetricSDK
{
    /// <summary>
    /// Minimal, dependency-free JSON reader. Unity's JsonUtility can't deserialize
    /// arbitrary objects (the remote-config "config"/"activeExperiments" maps have
    /// dynamic keys), so we parse by hand into plain CLR types:
    /// Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// Only used at fetch time, never on the logging hot path.
    /// </summary>
    internal static class JsonReader
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new FormatException("Empty JSON.");
            }

            var index = 0;
            var value = ParseValue(json, ref index);
            SkipWhitespace(json, ref index);
            if (index != json.Length)
            {
                throw new FormatException("Trailing characters after JSON value.");
            }

            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("Unexpected end of JSON.");
            }

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                case 'f': return ParseBool(s, ref i);
                case 'n': ParseLiteral(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return obj;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                {
                    throw new FormatException("Expected object key string.");
                }

                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                {
                    throw new FormatException("Expected ':' after object key.");
                }

                i++; // consume ':'
                obj[key] = ParseValue(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    throw new FormatException("Unterminated object.");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == '}')
                {
                    i++;
                    break;
                }

                throw new FormatException("Expected ',' or '}' in object.");
            }

            return obj;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var arr = new List<object>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return arr;
            }

            while (true)
            {
                arr.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                {
                    throw new FormatException("Unterminated array.");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == ']')
                {
                    i++;
                    break;
                }

                throw new FormatException("Expected ',' or ']' in array.");
            }

            return arr;
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // consume opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    if (i >= s.Length)
                    {
                        break;
                    }

                    var esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                            {
                                throw new FormatException("Bad \\u escape.");
                            }

                            var hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException("Bad escape \\" + esc + ".");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            throw new FormatException("Unterminated string.");
        }

        private static bool ParseBool(string s, ref int i)
        {
            if (s[i] == 't')
            {
                ParseLiteral(s, ref i, "true");
                return true;
            }

            ParseLiteral(s, ref i, "false");
            return false;
        }

        private static void ParseLiteral(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
            {
                throw new FormatException("Expected '" + literal + "'.");
            }

            i += literal.Length;
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            var slice = s.Substring(start, i - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw new FormatException("Invalid number '" + slice + "'.");
            }

            return number;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                var c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }
        }
    }
}