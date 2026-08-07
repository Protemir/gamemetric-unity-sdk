using System.Collections.Generic;

namespace GameMetricSDK
{
    /// <summary>
    /// Deduplicates and aggregates captured errors so a repeating exception (e.g.
    /// the same stack thrown every frame in a freeze loop) becomes a handful of
    /// events carrying a running count, instead of flooding the pipeline with
    /// hundreds of identical events and then silently hitting a per-session cap.
    ///
    /// Errors are keyed by a stable signature hash of severity + condition + stack.
    /// The first occurrence emits immediately; recurrences are counted in memory
    /// and only re-emitted once per throttle window, always carrying the running
    /// <c>count</c> (cumulative per session per signature — the backend should take
    /// the max per (session, error_id), which is robust to at-least-once delivery).
    /// A cap on the number of DISTINCT signatures bounds memory and event volume
    /// without ever dropping the count of an error already being tracked.
    ///
    /// Deliberately free of Unity dependencies: the caller passes "now" (seconds)
    /// so this can be unit-tested in isolation. Guarded by a lock so a manual
    /// LogError from a background thread is safe.
    /// </summary>
    internal sealed class ErrorAggregator
    {
        /// <summary>A ready-to-send error, produced on first capture or a throttled follow-up / drain.</summary>
        internal struct Report
        {
            public int Hash;
            public string Severity;
            public string LogType;
            public string Condition;
            public string StackTrace;
            public int Count;
        }

        private sealed class Entry
        {
            public string Severity;
            public string LogType;
            public string Condition;
            public string StackTrace;
            public int Count;         // total occurrences this session
            public int EmittedCount;  // count as of the last emitted event
            public float LastEmit;    // realtime seconds of the last emit
        }

        private readonly int _maxDistinct;
        private readonly float _windowSeconds;
        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly object _lock = new object();

        public ErrorAggregator(int maxDistinctSignatures, float aggregationWindowSeconds)
        {
            _maxDistinct = maxDistinctSignatures < 1 ? 1 : maxDistinctSignatures;
            _windowSeconds = aggregationWindowSeconds < 0f ? 0f : aggregationWindowSeconds;
        }

        /// <summary>Distinct error signatures tracked this session.</summary>
        public int DistinctCount
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        /// <summary>
        /// Records one occurrence and decides whether to emit an event now.
        /// <paramref name="emit"/> and <paramref name="report"/> are set when a
        /// send should happen; <paramref name="capReached"/> is true when a NEW
        /// signature was dropped because the distinct-signature cap is full.
        /// </summary>
        public void Observe(
            string severity, string logType, string condition, string stackTrace, float now,
            out bool emit, out Report report, out bool capReached)
        {
            emit = false;
            capReached = false;
            report = default;

            var hash = SignatureHash(severity, condition, stackTrace);

            lock (_lock)
            {
                if (_entries.TryGetValue(hash, out var entry))
                {
                    entry.Count++;

                    // Throttle recurrences: only re-emit once the window has passed,
                    // carrying the running cumulative count.
                    if (now - entry.LastEmit >= _windowSeconds)
                    {
                        entry.LastEmit = now;
                        entry.EmittedCount = entry.Count;
                        emit = true;
                        report = ToReport(hash, entry);
                    }

                    return;
                }

                if (_entries.Count >= _maxDistinct)
                {
                    // Full: drop this new distinct error. Errors already tracked keep
                    // counting — we only cap the number of distinct signatures.
                    capReached = true;
                    return;
                }

                entry = new Entry
                {
                    Severity = severity,
                    LogType = logType,
                    Condition = condition,
                    StackTrace = stackTrace,
                    Count = 1,
                    EmittedCount = 1,
                    LastEmit = now,
                };
                _entries[hash] = entry;

                emit = true;
                report = ToReport(hash, entry);
            }
        }

        /// <summary>
        /// Returns a final report for every signature whose count grew since it was
        /// last emitted (the un-reported tail), marking them emitted. Called on
        /// pause/quit so a burst right before backgrounding isn't lost.
        /// </summary>
        public List<Report> DrainPending(float now)
        {
            var reports = new List<Report>();
            lock (_lock)
            {
                foreach (var kv in _entries)
                {
                    var entry = kv.Value;
                    if (entry.Count > entry.EmittedCount)
                    {
                        entry.EmittedCount = entry.Count;
                        entry.LastEmit = now;
                        reports.Add(ToReport(kv.Key, entry));
                    }
                }
            }

            return reports;
        }

        private static Report ToReport(int hash, Entry entry)
        {
            return new Report
            {
                Hash = hash,
                Severity = entry.Severity,
                LogType = entry.LogType,
                Condition = entry.Condition,
                StackTrace = entry.StackTrace,
                Count = entry.Count,
            };
        }

        /// <summary>
        /// Stable FNV-1a 32-bit hash of the error signature. Deterministic across
        /// runs and platforms (unlike string.GetHashCode), so it doubles as the
        /// cross-session <c>error_id</c> the backend can group on.
        /// </summary>
        internal static int SignatureHash(string severity, string condition, string stackTrace)
        {
            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                var hash = offset;
                hash = FnvMix(hash, severity, prime);
                hash = FnvMix(hash, "\n", prime);
                hash = FnvMix(hash, condition, prime);
                hash = FnvMix(hash, "\n", prime);
                hash = FnvMix(hash, stackTrace, prime);
                return (int)hash;
            }
        }

        private static uint FnvMix(uint hash, string s, uint prime)
        {
            if (s == null)
            {
                return hash;
            }

            unchecked
            {
                for (var i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= prime;
                }
            }

            return hash;
        }
    }
}
