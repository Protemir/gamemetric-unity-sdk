using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameMetricSDK
{
    /// <summary>
    /// Holds the latest remote-config snapshot: the resolved config values and the
    /// per-event experiment tags (ab_&lt;name&gt; → variant). Updated wholesale by
    /// <see cref="GameMetric.FetchRemoteConfigAsync"/> (and from the on-disk cache
    /// at Initialize) and read by the dispatcher when stamping outgoing events, and
    /// by the typed Get/TryGet accessors. Snapshots are immutable and swapped by a
    /// single volatile reference write, so readers on any thread always see a
    /// consistent set without locking.
    /// </summary>
    internal sealed class RemoteConfigStore
    {
        private volatile Snapshot _snapshot = Snapshot.Empty;

        // Raw text of the last applied payload, for cheap change detection: a
        // fetch that returns byte-identical JSON is a no-op (no OnConfigUpdated).
        private string _lastJson;

        /// <summary>Number of active experiments the user is currently enrolled in.</summary>
        public int ExperimentCount => _snapshot.Experiments.Count;

        /// <summary>
        /// Returns the variant the user is assigned to for <paramref name="experimentName"/>,
        /// or <paramref name="fallback"/> if they're not enrolled (or config isn't
        /// fetched yet). The variant name is exactly as configured on the backend.
        /// </summary>
        public string GetVariant(string experimentName, string fallback = null)
        {
            return TryGetVariant(experimentName, out var variant) ? variant : fallback;
        }

        /// <summary>Reports enrollment: returns true and sets <paramref name="variant"/> only when the user is in the experiment.</summary>
        public bool TryGetVariant(string experimentName, out string variant)
        {
            if (!string.IsNullOrEmpty(experimentName)
                && _snapshot.Experiments.TryGetValue(experimentName, out variant))
            {
                return true;
            }

            variant = null;
            return false;
        }

        /// <summary>
        /// Parses a /v1/remote-config response and atomically swaps in the new
        /// snapshot. Returns true if the payload differs from the previously applied
        /// one (so callers can decide whether to raise a change notification).
        /// </summary>
        public bool UpdateFromJson(string json)
        {
            var root = JsonReader.Parse(json) as Dictionary<string, object>;
            if (root == null)
            {
                throw new FormatException("Remote-config root is not a JSON object.");
            }

            var config = root.TryGetValue("config", out var c) ? c as Dictionary<string, object> : null;
            var experiments = root.TryGetValue("activeExperiments", out var e) ? e as Dictionary<string, object> : null;

            // Keep both the raw experiment→variant map (for GetVariant lookups by
            // experiment name) and the precomputed ab_-prefixed tag map (so the
            // event-stamping hot path only copies ready-made key/value strings).
            var enrollments = new Dictionary<string, string>();
            var tags = new Dictionary<string, string>();
            if (experiments != null)
            {
                foreach (var kv in experiments)
                {
                    var variant = kv.Value?.ToString() ?? string.Empty;
                    enrollments[kv.Key] = variant;
                    tags["ab_" + kv.Key] = variant;
                }
            }

            _snapshot = new Snapshot(config ?? new Dictionary<string, object>(), enrollments, tags);

            var changed = !string.Equals(_lastJson, json, StringComparison.Ordinal);
            _lastJson = json;
            return changed;
        }

        /// <summary>
        /// Copies the current experiment tags into an event's properties. Reserved
        /// ab_ keys overwrite any collision so membership can't be spoofed by a
        /// user-supplied property. No-op when the user is in no experiments.
        /// </summary>
        public void ApplyExperimentTags(Dictionary<string, object> properties)
        {
            var tags = _snapshot.ExperimentTags;
            if (tags.Count == 0 || properties == null)
            {
                return;
            }

            foreach (var kv in tags)
            {
                properties[kv.Key] = kv.Value;
            }
        }

        /// <summary>True if <paramref name="key"/> is present in the current config (regardless of type).</summary>
        public bool ContainsKey(string key)
        {
            return _snapshot.Config.ContainsKey(key);
        }

        /// <summary>
        /// Typed lookup: returns true only when the key is present AND convertible
        /// to <typeparamref name="T"/>. Supports string, bool, int, long, float,
        /// double. Lets callers distinguish "value present" from "fell back".
        /// </summary>
        public bool TryGet<T>(string key, out T value)
        {
            value = default;
            if (!_snapshot.Config.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            var type = typeof(T);

            if (type == typeof(string))
            {
                value = (T)(object)ToStringValue(raw);
                return true;
            }

            if (type == typeof(bool))
            {
                if (TryToBool(raw, out var b))
                {
                    value = (T)(object)b;
                    return true;
                }

                return false;
            }

            if (type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long))
            {
                if (!TryToDouble(raw, out var d))
                {
                    return false;
                }

                if (type == typeof(double)) value = (T)(object)d;
                else if (type == typeof(float)) value = (T)(object)(float)d;
                else if (type == typeof(int)) value = (T)(object)(int)Math.Round(d);
                else value = (T)(object)(long)Math.Round(d);
                return true;
            }

            return false;
        }

        public string GetString(string key, string fallback = null)
        {
            return TryGet<string>(key, out var v) ? v : fallback;
        }

        public double GetDouble(string key, double fallback = 0)
        {
            return TryGet<double>(key, out var v) ? v : fallback;
        }

        public int GetInt(string key, int fallback = 0)
        {
            return TryGet<int>(key, out var v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback = false)
        {
            return TryGet<bool>(key, out var v) ? v : fallback;
        }

        // ----- value coercion (shared by Get*/TryGet) -----------------------

        private static string ToStringValue(object v)
        {
            if (v is string s) return s;
            if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return v.ToString();
        }

        private static bool TryToDouble(object v, out double result)
        {
            switch (v)
            {
                case double d:
                    result = d;
                    return true;
                case bool b:
                    result = b ? 1 : 0;
                    return true;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    result = parsed;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        private static bool TryToBool(object v, out bool result)
        {
            switch (v)
            {
                case bool b:
                    result = b;
                    return true;
                case double d:
                    result = Math.Abs(d) > double.Epsilon;
                    return true;
                case string s when bool.TryParse(s, out var parsed):
                    result = parsed;
                    return true;
                default:
                    result = false;
                    return false;
            }
        }

        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot(
                new Dictionary<string, object>(),
                new Dictionary<string, string>(),
                new Dictionary<string, string>());

            public readonly Dictionary<string, object> Config;

            /// <summary>Experiment name → assigned variant name (for GetVariant).</summary>
            public readonly Dictionary<string, string> Experiments;

            /// <summary>ab_&lt;experiment name&gt; → variant name (for event stamping).</summary>
            public readonly Dictionary<string, string> ExperimentTags;

            public Snapshot(
                Dictionary<string, object> config,
                Dictionary<string, string> experiments,
                Dictionary<string, string> experimentTags)
            {
                Config = config;
                Experiments = experiments;
                ExperimentTags = experimentTags;
            }
        }
    }
}
