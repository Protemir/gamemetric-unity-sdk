using System;

namespace GameMetricSDK
{
    /// <summary>
    /// Pure session-timing state machine. Tracks the current session's start and
    /// background pauses, and decides — on resume — whether the app was gone long
    /// enough (&gt;= timeout) to count as a new session. Computes session duration
    /// (clamped &gt;= 0) to the TRUE end (the pause time when backgrounded), so the
    /// backend's min/max(event_time)-based duration isn't inflated by idle
    /// background time.
    ///
    /// Free of Unity dependencies — the caller passes UTC timestamps — so it is
    /// unit-testable. The session id itself is owned by GameMetric, which emits
    /// session_start/session_end and rotates the id in the right order.
    /// </summary>
    internal sealed class SessionTracker
    {
        private readonly double _timeoutSeconds;
        private DateTime _startUtc;
        private bool _paused;
        private DateTime _pausedAtUtc;

        public SessionTracker(double timeoutSeconds)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>Begins (or rotates to) a session starting at <paramref name="nowUtc"/>.</summary>
        public void Start(DateTime nowUtc)
        {
            _startUtc = nowUtc;
            _paused = false;
        }

        /// <summary>Records that the app was backgrounded at <paramref name="nowUtc"/>.</summary>
        public void Pause(DateTime nowUtc)
        {
            _paused = true;
            _pausedAtUtc = nowUtc;
        }

        /// <summary>
        /// Evaluates a resume. Returns true when the background gap reached the
        /// timeout and a new session should start; then <paramref name="endedAtUtc"/>
        /// and <paramref name="endedDurationSeconds"/> describe the session that
        /// ended (at the pause time). Does NOT rotate the id — the caller emits
        /// session_end, rotates its id, then calls <see cref="Start"/>.
        /// </summary>
        public bool ResumeStartsNewSession(DateTime nowUtc, out DateTime endedAtUtc, out long endedDurationSeconds)
        {
            endedAtUtc = _pausedAtUtc;
            endedDurationSeconds = DurationSeconds(_startUtc, _pausedAtUtc);

            if (!_paused)
            {
                return false;   // a resume with no recorded pause — nothing to do
            }

            _paused = false;
            return (nowUtc - _pausedAtUtc).TotalSeconds >= _timeoutSeconds;
        }

        /// <summary>End time + duration for a quit: the pause time if currently backgrounded, else now.</summary>
        public void QuitEndInfo(DateTime nowUtc, out DateTime endUtc, out long durationSeconds)
        {
            endUtc = _paused ? _pausedAtUtc : nowUtc;
            durationSeconds = DurationSeconds(_startUtc, endUtc);
        }

        private static long DurationSeconds(DateTime start, DateTime end)
        {
            var seconds = (long)Math.Round((end - start).TotalSeconds);
            return seconds < 0 ? 0 : seconds;
        }
    }
}
