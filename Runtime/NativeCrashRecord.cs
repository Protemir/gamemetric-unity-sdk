using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GameMetricSDK
{
    /// <summary>
    /// One native crash handed off from a platform crash handler. A native crash
    /// (iOS signal / Android SIGSEGV / il2cpp hard-crash) kills the process, so it
    /// can't be sent in the moment; the native layer instead writes a small JSON
    /// record to disk and the managed SDK picks it up, emits it, and deletes it on
    /// the next launch (see <see cref="GameMetric"/>).
    ///
    /// This type defines that on-disk CONTRACT and parses it. Parsing is
    /// dependency-free (no Unity), so it's unit-testable and tolerant of partial or
    /// malformed input — a corrupt handoff file must never crash startup.
    ///
    /// Expected JSON (all fields optional except that at least one of type/message/
    /// stack must be present):
    /// <code>
    /// {
    ///   "schema": 1,
    ///   "platform": "android",            // ios | android | il2cpp | ...
    ///   "type": "SIGSEGV",                // signal name or exception class
    ///   "message": "Segfault at 0x0",     // human summary
    ///   "stack": "frame0\nframe1" | ["frame0","frame1"],
    ///   "build_id": "com.game@1.4.2 (arm64)",  // for later symbolication
    ///   "timestamp": "2026-08-08T12:34:56.000Z" // when the crash happened (UTC)
    /// }
    /// </code>
    /// </summary>
    internal sealed class NativeCrashRecord
    {
        public int Schema;
        public string Platform;
        public string Type;
        public string Message;
        public string Stack;
        public string BuildId;

        /// <summary>When the crash occurred; <see cref="HasTimestamp"/> is false if the record omitted it.</summary>
        public DateTime TimestampUtc;
        public bool HasTimestamp;

        public static bool TryParse(string json, out NativeCrashRecord record)
        {
            record = null;

            try
            {
                if (!(JsonReader.Parse(json) is Dictionary<string, object> root))
                {
                    return false;
                }

                var parsed = new NativeCrashRecord
                {
                    Schema = (int)Math.Round(GetDouble(root, "schema", 1)),
                    Platform = GetString(root, "platform"),
                    Type = GetString(root, "type"),
                    Message = GetString(root, "message"),
                    Stack = GetStack(root, "stack"),
                    BuildId = GetString(root, "build_id"),
                };

                var ts = GetString(root, "timestamp");
                if (!string.IsNullOrEmpty(ts) && DateTime.TryParse(
                        ts, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                {
                    parsed.TimestampUtc = when;
                    parsed.HasTimestamp = true;
                }

                // A record with nothing identifying isn't worth an event.
                if (string.IsNullOrEmpty(parsed.Type)
                    && string.IsNullOrEmpty(parsed.Message)
                    && string.IsNullOrEmpty(parsed.Stack))
                {
                    return false;
                }

                record = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            if (map.TryGetValue(key, out var v) && v != null)
            {
                return v as string ?? v.ToString();
            }

            return null;
        }

        private static double GetDouble(Dictionary<string, object> map, string key, double fallback)
        {
            if (map.TryGetValue(key, out var v))
            {
                if (v is double d) return d;
                if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        // A native writer may emit the stack either as one string or as an array of
        // frame strings; accept both and normalize to newline-joined text.
        private static string GetStack(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var v) || v == null)
            {
                return null;
            }

            if (v is string s)
            {
                return s;
            }

            if (v is List<object> frames)
            {
                var sb = new StringBuilder();
                for (var i = 0; i < frames.Count; i++)
                {
                    if (frames[i] == null)
                    {
                        continue;
                    }

                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(frames[i].ToString());
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }

            return v.ToString();
        }
    }
}
