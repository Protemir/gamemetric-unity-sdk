using System;
using System.Collections.Generic;

namespace GameMetricSDK
{
    /// <summary>
    /// A single analytics event, shaped to the backend's ingestion columns.
    /// Instances are pooled (see <see cref="EventPool"/>) and reused, so the
    /// hot logging path allocates nothing steady-state beyond growing the
    /// per-event properties dictionary.
    /// </summary>
    internal sealed class GameMetricEvent
    {
        /// <summary>
        /// Stable per-event id (Guid). Generated once at log time and preserved
        /// across every retry / offline resend, so the backend can deduplicate a
        /// batch that was delivered but whose response was lost.
        /// </summary>
        public string EventId;

        public string UserId;
        public string SessionId;
        public string EventName;
        public DateTime EventTimeUtc;
        public string Platform;
        public string Version;

        /// <summary>Reused across rentals — cleared, never reallocated, on reset.</summary>
        public readonly Dictionary<string, object> Properties = new Dictionary<string, object>();

        public void Reset()
        {
            EventId = null;
            UserId = null;
            SessionId = null;
            EventName = null;
            EventTimeUtc = default;
            Platform = null;
            Version = null;
            Properties.Clear();
        }
    }
}