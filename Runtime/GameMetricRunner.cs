using System;
using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// The SDK's heartbeat MonoBehaviour, spawned on a hidden DontDestroyOnLoad
    /// object by <see cref="GameMetric.Initialize"/>. Drives periodic/threshold
    /// event flushes, forwards app pause/resume/quit to the SDK (for session
    /// lifecycle, remote-config refresh, and error-tail flushing), and persists
    /// the queue on pause/quit so events survive backgrounding or closing.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class GameMetricRunner : MonoBehaviour
    {
        /// <summary>Lifecycle callbacks into GameMetric. Kept as delegates so the runner stays decoupled and compiles under GAMEMETRIC_DISABLED.</summary>
        internal sealed class Hooks
        {
            public Action RefreshRemoteConfig;  // periodic remote-config refresh (timer)
            public Action Paused;               // app backgrounded — runs before persist
            public Action Resumed;              // app foregrounded again
            public Action Quitting;             // app quitting — runs before persist
        }

        private EventDispatcher _dispatcher;
        private GameMetricConfig _config;
        private Hooks _hooks;
        private float _flushTimer;
        private float _configTimer;

        public void Configure(EventDispatcher dispatcher, GameMetricConfig config, Hooks hooks)
        {
            _dispatcher = dispatcher;
            _config = config;
            _hooks = hooks;
        }

        private void Start()
        {
            // Deliver any offline backlog from a previous session immediately.
            FlushNow();
        }

        private void Update()
        {
            if (_dispatcher == null)
            {
                return;
            }

            _flushTimer += Time.unscaledDeltaTime;

            // A captured crash asks for immediate delivery: flush next frame,
            // bypassing both the timer and backoff, so it goes out before a
            // possible termination rather than waiting for FlushIntervalSeconds.
            var priority = _dispatcher.PriorityFlushRequested;
            var due = priority || _flushTimer >= _config.FlushIntervalSeconds || _dispatcher.QueuedCount >= _config.FlushThreshold;
            if (due && !_dispatcher.IsSending && (priority || !_dispatcher.InBackoff))
            {
                _flushTimer = 0f;
                _dispatcher.ClearPriorityFlush();
                StartCoroutine(_dispatcher.FlushRoutine());
            }

            TickRemoteConfigRefresh();
        }

        /// <summary>Periodic remote-config auto-refresh, independent of the event-flush cadence.</summary>
        private void TickRemoteConfigRefresh()
        {
            if (_hooks?.RefreshRemoteConfig == null
                || !_config.AutoFetchRemoteConfig
                || _config.RemoteConfigRefreshIntervalSeconds <= 0f)
            {
                return;
            }

            _configTimer += Time.unscaledDeltaTime;
            if (_configTimer >= _config.RemoteConfigRefreshIntervalSeconds)
            {
                _configTimer = 0f;
                _hooks.RefreshRemoteConfig();
            }
        }

        public void FlushNow()
        {
            if (_dispatcher == null || !isActiveAndEnabled)
            {
                return;
            }

            _flushTimer = 0f;

            // Respect the exponential-backoff cooldown so a manual Flush(), the
            // startup flush, or a pause-triggered flush can't hammer a server we've
            // already backed off from. Events stay queued/cached and go out on the
            // next eligible cycle — nothing is lost. Crash priority flushes bypass
            // backoff via the Update() path, not here.
            if (!_dispatcher.IsSending && !_dispatcher.InBackoff)
            {
                StartCoroutine(_dispatcher.FlushRoutine());
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (_dispatcher == null)
            {
                return;
            }

            if (paused)
            {
                // Backgrounding (especially on mobile) can precede termination.
                // Let the SDK record the pause and emit any error tail first, then
                // persist synchronously, then attempt a live flush if we survive.
                _hooks?.Paused?.Invoke();
                _dispatcher.PersistPending();
                FlushNow();
            }
            else
            {
                // Resumed: the SDK decides whether the break was long enough to open
                // a new session, and refreshes config. Reset the periodic timer so
                // the next tick is measured from now.
                _configTimer = 0f;
                _hooks?.Resumed?.Invoke();
            }
        }

        private void OnApplicationQuit()
        {
            // Network delivery is unreliable during teardown, so just persist —
            // the next launch sends it cache-first. Let the SDK close the session
            // and emit the error tail first so both are persisted.
            if (_dispatcher != null)
            {
                _hooks?.Quitting?.Invoke();
                _dispatcher.PersistPending();
            }
        }
    }
}
