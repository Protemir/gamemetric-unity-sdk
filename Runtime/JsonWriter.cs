using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameMetricSDK
{
    /// <summary>
    /// Minimal, dependency-free JSON serializer for the exact shape the backend
    /// ingests. Unity's JsonUtility can't emit a Dictionary of arbitrary values
    /// (arbitrary "properties") or a top-level array, so we write it by hand.
    /// Only used at flush time, never on the logging hot path.
    /// </summary>
    internal static class JsonWriter
    {
        /// <summary>Serializes one event object: {"user_id":..,"properties":{..}}.</summary>
        public static void WriteEvent(StringBuilder sb, GameMetricEvent evt)
        {
            sb.Append('{');
            WriteMember(sb, "event_id", evt.EventId);
            sb.Append(',');
            WriteMember(sb, "user_id", evt.UserId);
            sb.Append(',');
            WriteMember(sb, "session_id", evt.SessionId);
            sb.Append(',');
            WriteMember(sb, "event_name", evt.EventName);
            sb.Append(',');
            WriteString(sb, "event_time");
            sb.Append(':');
            WriteString(sb, evt.EventTimeUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            sb.Append(',');
            WriteMember(sb, "platform", evt.Platform);
            sb.Append(',');
            WriteMember(sb, "version", evt.Version);
            sb.Append(',');
            WriteString(sb, "properties");
            sb.Append(':');
            WriteObject(sb, evt.Properties);
            sb.Append('}');
        }

        private static void WriteMember(StringBuilder sb, string key, string value)
        {
            WriteString(sb, key);
            sb.Append(':');
            WriteString(sb, value);
        }

        // Bound on nesting depth so a cyclic reference in a property value degrades
        // to null instead of overflowing the stack.
        private const int MaxDepth = 32;

        private static void WriteObject(StringBuilder sb, Dictionary<string, object> map)
        {
            sb.Append('{');
            if (map != null)
            {
                var first = true;
                foreach (var kv in map)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value, 1);
                }
            }

            sb.Append('}');
        }

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }

            if (value is float f)
            {
                // NaN/Infinity are not valid JSON numbers. Left as-is they'd
                // serialize to bare NaN/Infinity tokens, which System.Text.Json on
                // the backend rejects — failing the whole batch (400) and dropping
                // every event in it. Emit null so one bad value can't poison a flush.
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    sb.Append("null");
                    return;
                }

                // "G9" round-trips a float exactly; "R" is documented as unreliable.
                sb.Append(f.ToString("G9", CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    sb.Append("null");
                    return;
                }

                // "G17" round-trips a double exactly; "R" is documented as unreliable.
                sb.Append(d.ToString("G17", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal m)
            {
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long)
            {
                sb.Append(System.Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is ulong ul)
            {
                sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                return;
            }

            // Nested structures. IDictionary is checked before IEnumerable because
            // dictionaries are also enumerable. Past MaxDepth (a cyclic reference)
            // we emit null rather than overflow the stack.
            if (value is System.Collections.IDictionary dict)
            {
                if (depth >= MaxDepth)
                {
                    sb.Append("null");
                    return;
                }

                WriteDictionary(sb, dict, depth);
                return;
            }

            if (value is System.Collections.IEnumerable seq)
            {
                if (depth >= MaxDepth)
                {
                    sb.Append("null");
                    return;
                }

                WriteArray(sb, seq, depth);
                return;
            }

            // Enums, chars, and anything else exotic are stored as their string form.
            WriteString(sb, value.ToString());
        }

        private static void WriteDictionary(StringBuilder sb, System.Collections.IDictionary map, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteString(sb, entry.Key?.ToString() ?? string.Empty);
                sb.Append(':');
                WriteValue(sb, entry.Value, depth + 1);
            }

            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, System.Collections.IEnumerable seq, int depth)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in seq)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteValue(sb, item, depth + 1);
            }

            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }
}