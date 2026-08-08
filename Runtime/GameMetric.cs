using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GameMetricSDK
{
    /// <summary>Stage of a progression event, serialized as start/complete/fail.</summary>
    public enum ProgressionStatus
    {
        Start,
        Complete,
        Fail,
    }

    /// <summary>
    /// Static entry point for the GameMetric Unity SDK.
    ///
    /// Define <c>GAMEMETRIC_DISABLED</c> (Project Settings → Player → Scripting
    /// Define Symbols) to compile the SDK out entirely: every method below becomes
    /// a zero-overhead no-op and no background work is started — useful for builds
    /// that must ship without analytics.
    ///
    /// Usage:
    /// <code>
    /// GameMetric.Initialize(); // reads Project Settings → GameMetric
    /// GameMetric.LogEvent("level_complete", new Dictionary&lt;string, object&gt; { { "level", 7 } });
    /// GameMetric.LogMonetization("com.game.gems_pack", 4.99, "USD");
    /// GameMetric.LogProgression(ProgressionStatus.Complete, "world_1.level_3");
    /// </code>
    /// </summary>
    public static class GameMetric
    {
        /// <summary>
        /// Raised on the main thread after a remote-config fetch that actually
        /// changed the resolved values — never for an unchanged refresh, and never
        /// for the initial on-disk cache load at Initialize. Subscribe to react to
        /// balance/config changes live (re-read via GetConfig*/GetConfig&lt;T&gt;);
        /// remember to unsubscribe. Never raised while GAMEMETRIC_DISABLED.
        /// </summary>
#pragma warning disable 67 // Never raised under GAMEMETRIC_DISABLED.
        public static event Action OnConfigUpdated;
#pragma warning restore 67

#if GAMEMETRIC_DISABLED
        // Analytics compiled out via GAMEMETRIC_DISABLED. No state, no runner —
        // the getters return inert defaults and every call below is a no-op.
        public static bool IsInitialized => false;
        public static string SessionId => null;
        public static string UserId => null;
        public static bool IsCollectionEnabled => false;
#else
        private const string UserIdPrefKey = "gamemetric_user_id";

        // Persisted opt-out/consent flag. Collection is on by default; the game
        // gates it via SetCollectionEnabled based on its own consent flow, and the
        // choice survives restarts.
        private const string CollectionEnabledPrefKey = "gamemetric_collection_enabled";

        // Error-capture contract with the backend: captured Unity errors are sent
        // as this event, read back by GET /v1/analytics/crashes.
        private const string ErrorEventName = "error";

        // Bounds on captured text so a pathological message/stack can't bloat a
        // batch or the offline cache.
        private const int MaxConditionChars = 1000;
        private const int MaxStackTraceChars = 4000;

        // Dedup/aggregate captured errors so a per-frame crash loop is a couple of
        // counted events, not hundreds (created per session in Initialize).
        private static ErrorAggregator _errorAggregator;
        private static bool _errorCapWarned;

        // Per-thread re-entrancy guard: captured logs can arrive on background
        // threads (logMessageReceivedThreaded), so each thread needs its own guard.
        [ThreadStatic]
        private static bool _isCapturingLog;

        private static GameMetricConfig _config;
        private static EventStore _store;
        private static EventDispatcher _dispatcher;
        private static GameMetricRunner _runner;
        private static RemoteConfigStore _remoteConfig;
        private static RemoteConfigCache _remoteConfigCache;

        // Guards against overlapping remote-config fetches and debounces
        // resume-triggered refreshes so rapid foreground/background toggling can't
        // spam the server.
        private static bool _remoteConfigFetching;
        private static float _lastConfigFetchRealtime = float.NegativeInfinity;
        private const float MinRefreshGapSeconds = 30f;

        private static string _sessionId;
        private static string _userId;
        private static string _platform;
        private static string _version;
        private static bool _initialized;

        // Runtime consent gate. Volatile because LogEvent may be called from any
        // thread while SetCollectionEnabled flips it on the main thread. Defaults
        // to true; Initialize loads the persisted choice.
        private static volatile bool _collectionEnabled = true;

        // Session timing: start/pause bookkeeping + the resume-timeout decision.
        // GameMetric owns the id and the session_start/session_end emission.
        private static SessionTracker _session;

        public static bool IsInitialized => _initialized;

        /// <summary>Whether data collection is currently enabled (see <see cref="SetCollectionEnabled"/>).</summary>
        public static bool IsCollectionEnabled => _collectionEnabled;

        /// <summary>The current session id (regenerated after a long background per <see cref="GameMetricConfig.SessionTimeoutSeconds"/>).</summary>
        public static string SessionId => _sessionId;

        /// <summary>The persistent anonymous user/install id. Override with <see cref="SetUserId"/>.</summary>
        public static string UserId => _userId;
#endif

        /// <summary>
        /// Starts a session using the values saved in Edit → Project Settings →
        /// GameMetric (ApiKey / GameVersion / EnableDebugLogs).
        /// </summary>
        public static void Initialize()
        {
#if !GAMEMETRIC_DISABLED
            var settings = GameMetricSettings.LoadOrNull();
            if (settings == null)
            {
                Debug.LogError("[GameMetric] Initialize(): no GameMetricSettings found. Open Edit → Project Settings → GameMetric, or call Initialize(apiKey, gameVersion).");
                return;
            }

            InitializeInternal(settings.ApiKey, settings.GameVersion, settings.EnableDebugLogs, settings.BaseUrl);
#endif
        }

        /// <summary>
        /// Starts a session with just an API key. Game version falls back to
        /// <c>Application.version</c>; debug logs stay off. The simplest entry point.
        /// </summary>
        public static void Initialize(string apiKey)
        {
#if !GAMEMETRIC_DISABLED
            InitializeInternal(apiKey, null, false, null);
#endif
        }

        /// <summary>
        /// Starts a session explicitly: generates a SessionId, resolves the
        /// persistent user id, captures device metadata, spawns the background
        /// runner, and logs a session_start event. Safe to call once; extra calls
        /// are ignored. <paramref name="baseUrl"/> overrides the backend base URL
        /// (e.g. <c>http://localhost:5000</c> for local testing); null uses production.
        /// </summary>
        public static void Initialize(string apiKey, string gameVersion, bool enableDebugLogs = false, string baseUrl = null)
        {
#if !GAMEMETRIC_DISABLED
            InitializeInternal(apiKey, gameVersion, enableDebugLogs, baseUrl);
#endif
        }

        /// <summary>
        /// Queues an event for background delivery. Non-blocking; the optional
        /// <paramref name="parameters"/> become the event's JSON "properties".
        /// </summary>
        public static void LogEvent(string eventName, Dictionary<string, object> parameters = null)
        {
#if !GAMEMETRIC_DISABLED
            LogEventInternal(eventName, DateTime.UtcNow, parameters);
#endif
        }

#if !GAMEMETRIC_DISABLED
        /// <summary>
        /// Shared event-enqueue core. <paramref name="eventTimeUtc"/> lets internal
        /// callers stamp a specific time (e.g. a back-dated session_end at the real
        /// pause time) instead of "now"; public LogEvent always passes DateTime.UtcNow.
        /// </summary>
        private static void LogEventInternal(string eventName, DateTime eventTimeUtc, Dictionary<string, object> parameters)
        {
            if (!_initialized)
            {
                Debug.LogWarning("[GameMetric] LogEvent called before Initialize; event '" + eventName + "' ignored.");
                return;
            }

            // Opted out — collect nothing.
            if (!_collectionEnabled)
            {
                return;
            }

            if (string.IsNullOrEmpty(eventName))
            {
                return;
            }

            var evt = EventPool.Rent();
            evt.EventId = Guid.NewGuid().ToString("N");
            evt.UserId = _userId;
            evt.SessionId = _sessionId;
            evt.Platform = _platform;
            evt.Version = _version;
            evt.EventName = eventName;
            evt.EventTimeUtc = eventTimeUtc;

            if (parameters != null)
            {
                foreach (var kv in parameters)
                {
                    evt.Properties[kv.Key] = kv.Value;
                }
            }

            _dispatcher.Enqueue(evt);
        }
#endif

        /// <summary>
        /// Logs an in-app purchase. <paramref name="amount"/> is stored under
        /// "revenue" — the exact key the backend's revenue metrics read.
        /// </summary>
        public static void LogMonetization(string productId, double amount, string currency, Dictionary<string, object> extra = null)
        {
#if !GAMEMETRIC_DISABLED
            if (!_initialized)
            {
                Debug.LogWarning("[GameMetric] LogMonetization called before Initialize; ignored.");
                return;
            }

            var props = new Dictionary<string, object>(3 + (extra != null ? extra.Count : 0))
            {
                { "product_id", productId },
                { "revenue", amount },
                { "currency", currency },
            };

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    props[kv.Key] = kv.Value;
                }
            }

            LogEvent("in_app_purchase", props);
#endif
        }

        /// <summary>
        /// Logs a progression event, e.g. starting/finishing a level:
        /// <c>LogProgression(ProgressionStatus.Complete, "world_1.level_3")</c>.
        /// </summary>
        public static void LogProgression(ProgressionStatus status, string progression, string details = null, Dictionary<string, object> extra = null)
        {
#if !GAMEMETRIC_DISABLED
            if (!_initialized)
            {
                Debug.LogWarning("[GameMetric] LogProgression called before Initialize; ignored.");
                return;
            }

            var props = new Dictionary<string, object>(3 + (extra != null ? extra.Count : 0))
            {
                { "status", ProgressionStatusToString(status) },
                { "progression", progression },
            };

            if (!string.IsNullOrEmpty(details))
            {
                props["details"] = details;
            }

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    props[kv.Key] = kv.Value;
                }
            }

            LogEvent("progression", props);
#endif
        }

        /// <summary>
        /// Manually reports a handled error/exception as a crash event. Uncaught
        /// errors are captured automatically (see <see cref="GameMetricConfig.CaptureErrors"/>);
        /// use this for exceptions you catch yourself, e.g. in a try/catch.
        /// </summary>
        public static void LogError(string message, string stackTrace = null, Dictionary<string, object> extra = null)
        {
#if !GAMEMETRIC_DISABLED
            if (!_initialized)
            {
                Debug.LogWarning("[GameMetric] LogError called before Initialize; ignored.");
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            CaptureError(
                "error",
                "Manual",
                Truncate(message, MaxConditionChars),
                string.IsNullOrEmpty(stackTrace) ? null : Truncate(stackTrace, MaxStackTraceChars),
                extra);
#endif
        }

        /// <summary>Overrides the anonymous user id with your own stable id (e.g. an account id) and persists it.</summary>
        public static void SetUserId(string userId)
        {
#if !GAMEMETRIC_DISABLED
            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            _userId = userId;
            PlayerPrefs.SetString(UserIdPrefKey, userId);
            PlayerPrefs.Save();
#endif
        }

        /// <summary>
        /// Enables or disables all data collection at runtime — the consent /
        /// opt-out switch. The choice is persisted and honored on the next launch.
        /// While disabled, no events (including crashes and sessions) are collected
        /// or sent, and no remote-config fetch runs. Opting out also purges any data
        /// not yet delivered (the in-memory queue and the offline cache). Opting
        /// back in resumes capture and starts a fresh session. Safe to call before
        /// or after <see cref="Initialize()"/>; call it from the main thread.
        /// </summary>
        public static void SetCollectionEnabled(bool enabled)
        {
#if !GAMEMETRIC_DISABLED
            // Persist first so the choice survives even if called before Initialize.
            PlayerPrefs.SetInt(CollectionEnabledPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();

            if (_collectionEnabled == enabled)
            {
                return;
            }

            _collectionEnabled = enabled;

            // Before Initialize there's no pipeline to touch; Initialize applies it.
            if (!_initialized)
            {
                return;
            }

            if (enabled)
            {
                // Opt back in: resume crash capture and open a fresh session.
                if (_config.CaptureErrors)
                {
                    Application.logMessageReceivedThreaded -= HandleUnityLog;
                    Application.logMessageReceivedThreaded += HandleUnityLog;
                }

                StartNewSession();
                GameMetricLog.Info("Collection re-enabled.");
            }
            else
            {
                // Opt out: stop capturing and purge anything not yet delivered.
                Application.logMessageReceivedThreaded -= HandleUnityLog;
                _dispatcher.DiscardQueued();
                _store.Clear();
                GameMetricLog.Info("Collection disabled; queued and cached events cleared.");
            }
#endif
        }

        /// <summary>Requests an immediate delivery attempt of the queued (and cached) events.</summary>
        public static void Flush()
        {
#if !GAMEMETRIC_DISABLED
            if (_initialized && _runner != null)
            {
                _runner.FlushNow();
            }
#endif
        }

        /// <summary>
        /// Manually fetches this project's remote config for the current user. On
        /// success the snapshot is updated, persisted to disk (so the next launch
        /// starts from it, not fallbacks), and — if the values changed —
        /// <see cref="OnConfigUpdated"/> is raised; every subsequent event is also
        /// tagged with the user's A/B experiment membership (ab_&lt;name&gt;
        /// properties) so funnels/metrics can be split by test group.
        ///
        /// Usually unnecessary: with <see cref="GameMetricConfig.AutoFetchRemoteConfig"/>
        /// on (default) the SDK fetches automatically after Initialize, on resume,
        /// and on a timer. Use this to force a refresh or to await a fresh fetch
        /// before reading config on the first frame. Authenticated with the
        /// configured X-API-Key. Returns true on success.
        /// </summary>
        public static Task<bool> FetchRemoteConfigAsync()
        {
#if GAMEMETRIC_DISABLED
            return Task.FromResult(false);
#else
            if (!_initialized || _runner == null)
            {
                Debug.LogWarning("[GameMetric] FetchRemoteConfigAsync called before Initialize; ignored.");
                return Task.FromResult(false);
            }

            // UnityWebRequest must run on the main thread; drive it from the runner
            // coroutine and bridge completion back to an awaitable Task.
            var tcs = new TaskCompletionSource<bool>();
            _runner.StartCoroutine(FetchRemoteConfigRoutine(tcs));
            return tcs.Task;
#endif
        }

        /// <summary>Reads a cached remote-config string (or <paramref name="fallback"/> if unset / not yet fetched).</summary>
        public static string GetConfigString(string key, string fallback = null)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null ? _remoteConfig.GetString(key, fallback) : fallback;
#endif
        }

        /// <summary>Reads a cached remote-config integer (rounds a numeric value; falls back if unset).</summary>
        public static int GetConfigInt(string key, int fallback = 0)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null ? _remoteConfig.GetInt(key, fallback) : fallback;
#endif
        }

        /// <summary>Reads a cached remote-config number (falls back if unset).</summary>
        public static double GetConfigDouble(string key, double fallback = 0)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null ? _remoteConfig.GetDouble(key, fallback) : fallback;
#endif
        }

        /// <summary>Reads a cached remote-config boolean (falls back if unset).</summary>
        public static bool GetConfigBool(string key, bool fallback = false)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null ? _remoteConfig.GetBool(key, fallback) : fallback;
#endif
        }

        /// <summary>
        /// Typed read of a cached remote-config value. Supports string, bool, int,
        /// long, float, double. Returns <paramref name="fallback"/> when the key is
        /// absent, not yet fetched, or not convertible to <typeparamref name="T"/>.
        /// </summary>
        public static T GetConfig<T>(string key, T fallback = default)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null && _remoteConfig.TryGet<T>(key, out var v) ? v : fallback;
#endif
        }

        /// <summary>
        /// Typed lookup that reports presence: returns true and sets
        /// <paramref name="value"/> only when the key exists AND is convertible to
        /// <typeparamref name="T"/> — so you can tell a real value from a fallback.
        /// </summary>
        public static bool TryGetConfig<T>(string key, out T value)
        {
#if GAMEMETRIC_DISABLED
            value = default;
            return false;
#else
            if (_remoteConfig != null && _remoteConfig.TryGet<T>(key, out value))
            {
                return true;
            }

            value = default;
            return false;
#endif
        }

        /// <summary>
        /// Returns which A/B variant the current player is assigned to for
        /// <paramref name="experimentName"/>, so you can branch game logic on it:
        /// <c>if (GameMetric.GetVariant("price_test") == "variant_a") { … }</c>.
        /// Returns <paramref name="fallback"/> (default null) when the player isn't
        /// enrolled in that experiment or remote config hasn't been fetched yet.
        /// The variant name is exactly as configured on the backend. Assignment is
        /// sticky per user (deterministic server-side bucketing).
        /// </summary>
        public static string GetVariant(string experimentName, string fallback = null)
        {
#if GAMEMETRIC_DISABLED
            return fallback;
#else
            return _remoteConfig != null ? _remoteConfig.GetVariant(experimentName, fallback) : fallback;
#endif
        }

        /// <summary>
        /// Reports A/B enrollment: returns true and sets <paramref name="variant"/>
        /// only when the player is enrolled in <paramref name="experimentName"/> —
        /// so you can distinguish "in control" from "not in the test at all".
        /// </summary>
        public static bool TryGetVariant(string experimentName, out string variant)
        {
#if GAMEMETRIC_DISABLED
            variant = null;
            return false;
#else
            if (_remoteConfig != null)
            {
                return _remoteConfig.TryGetVariant(experimentName, out variant);
            }

            variant = null;
            return false;
#endif
        }

#if !GAMEMETRIC_DISABLED
        private static void InitializeInternal(string apiKey, string gameVersion, bool enableDebugLogs, string baseUrl)
        {
            if (_initialized)
            {
                Debug.LogWarning("[GameMetric] Already initialized; ignoring duplicate Initialize.");
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[GameMetric] Initialize failed: apiKey is required. Set it in Project Settings → GameMetric or pass it to Initialize(apiKey, gameVersion).");
                return;
            }

            GameMetricLog.DebugEnabled = enableDebugLogs;

            // Honor a previously persisted opt-out (or a pre-Initialize SetCollectionEnabled call).
            _collectionEnabled = PlayerPrefs.GetInt(CollectionEnabledPrefKey, 1) == 1;

            _config = new GameMetricConfig(apiKey, baseUrl) { EnableDebugLogs = enableDebugLogs };
            _sessionId = Guid.NewGuid().ToString("N");
            _session = new SessionTracker(_config.SessionTimeoutSeconds);
            _session.Start(DateTime.UtcNow);
            _platform = GameMetricConfig.ResolvePlatform();
            _version = string.IsNullOrEmpty(gameVersion) ? Application.version : gameVersion;
            _userId = ResolveUserId();

            _store = new EventStore(_config.MaxCachedEvents);
            _remoteConfig = new RemoteConfigStore();
            _remoteConfigCache = new RemoteConfigCache();
            _errorAggregator = new ErrorAggregator(_config.MaxDistinctErrorsPerSession, _config.ErrorAggregationWindowSeconds);
            _dispatcher = new EventDispatcher(_config, _store, _remoteConfig);

            // Cold-start fix: seed config from the last session's cached snapshot
            // synchronously, so the first frame reads real values instead of
            // fallbacks. A fresh fetch (kicked below / on the timer) supersedes it.
            LoadCachedRemoteConfig();

            // If the user previously opted out, ensure no personal data lingers to
            // be flushed on startup (nothing is written while disabled, but the
            // dispatcher's flush isn't consent-aware, so clear defensively).
            if (!_collectionEnabled)
            {
                _store.Clear();
            }

            var go = new GameObject("GameMetricRunner");
            go.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<GameMetricRunner>();
            _runner.Configure(_dispatcher, _config, new GameMetricRunner.Hooks
            {
                RefreshRemoteConfig = RefreshRemoteConfigInternal,
                Paused = HandlePauseInternal,
                Resumed = HandleResumeInternal,
                Quitting = HandleQuitInternal,
            });

            _initialized = true;
            _errorCapWarned = false;

            // Auto-capture uncaught Unity errors/exceptions as crash events (only
            // while collection is enabled). The unsubscribe-then-subscribe pattern
            // guarantees a single handler even if this were somehow reached twice.
            if (_config.CaptureErrors && _collectionEnabled)
            {
                Application.logMessageReceivedThreaded -= HandleUnityLog;
                Application.logMessageReceivedThreaded += HandleUnityLog;
            }

            // One session_start per session, carrying device metadata. No-op while
            // opted out (LogEvent is gated on consent).
            LogEvent("session_start", BuildDeviceMetadata());

            GameMetricLog.Info("Initialized. session=" + _sessionId + " platform=" + _platform
                + " version=" + _version + " collection=" + (_collectionEnabled ? "on" : "off"));

            // Kick an initial background refresh so the seeded snapshot is brought
            // up to date shortly after launch — no manual Fetch needed. Fires
            // OnConfigUpdated if the server's values differ from the cache.
            if (_config.AutoFetchRemoteConfig)
            {
                RefreshRemoteConfigInternal();
            }

            // Deliver any native crash a platform handler captured before the
            // process died last run (handed off on disk; see NativeCrashRecord).
            if (_config.CaptureErrors)
            {
                ReportPendingNativeCrashesInternal();
            }
        }

        private static string ResolveUserId()
        {
            var existing = PlayerPrefs.GetString(UserIdPrefKey, "");
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            var id = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(UserIdPrefKey, id);
            PlayerPrefs.Save();
            return id;
        }

        private static Dictionary<string, object> BuildDeviceMetadata()
        {
            return new Dictionary<string, object>(4)
            {
                { "os", SystemInfo.operatingSystem },
                { "device_model", SystemInfo.deviceModel },
                { "unity_version", Application.unityVersion },
                { "language", Application.systemLanguage.ToString() },
            };
        }

        private static string ProgressionStatusToString(ProgressionStatus status)
        {
            switch (status)
            {
                case ProgressionStatus.Start:
                    return "start";
                case ProgressionStatus.Complete:
                    return "complete";
                default:
                    return "fail";
            }
        }

        /// <summary>
        /// Unity log hook: turns Error/Assert/Exception logs into "error" events.
        /// Registered on <c>Application.logMessageReceivedThreaded</c>, so it also
        /// catches errors logged from background threads / the Job System — meaning
        /// this can run OFF the main thread. That's safe here: the aggregator, event
        /// pool and dispatch queue are all thread-safe, the re-entrancy guard is
        /// [ThreadStatic], and the throttle clock avoids main-thread-only Unity APIs.
        /// </summary>
        private static void HandleUnityLog(string condition, string stackTrace, LogType type)
        {
            // Only crash-worthy severities; ignore Log/Warning.
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }

            // Never capture the SDK's own diagnostics: it would pollute the
            // customer's crash data AND risk a feedback loop, since
            // GameMetricLog.Error routes through Debug.LogError back into here.
            if (string.IsNullOrEmpty(condition) || condition.StartsWith(GameMetricLog.Prefix, StringComparison.Ordinal))
            {
                return;
            }

            // Re-entrancy guard: if anything on the capture path itself logged an
            // error, don't recurse.
            if (_isCapturingLog)
            {
                return;
            }

            _isCapturingLog = true;
            try
            {
                CaptureError(
                    SeverityFor(type),
                    type.ToString(),
                    Truncate(condition, MaxConditionChars),
                    Truncate(stackTrace ?? string.Empty, MaxStackTraceChars),
                    null);
            }
            finally
            {
                _isCapturingLog = false;
            }
        }

        /// <summary>
        /// Central capture path for auto-captured logs and manual LogError:
        /// deduplicate/aggregate by signature, then emit (and request a priority
        /// flush) only when the aggregator decides to.
        /// </summary>
        private static void CaptureError(string severity, string logType, string condition, string stackTrace, Dictionary<string, object> extra)
        {
            if (_errorAggregator == null || !_collectionEnabled)
            {
                return;
            }

            _errorAggregator.Observe(
                severity, logType, condition, stackTrace, ErrorClockSeconds(),
                out var emit, out var report, out var capReached);

            if (capReached)
            {
                if (!_errorCapWarned)
                {
                    _errorCapWarned = true;
                    // Warning (not Error) so this notice isn't itself captured.
                    GameMetricLog.Warn("Distinct-error cap of " + _config.MaxDistinctErrorsPerSession
                        + "/session reached; new distinct errors are dropped (already-tracked ones keep counting).");
                }

                return;
            }

            if (emit)
            {
                EmitErrorReport(report, extra, priorityFlush: true);
            }
        }

        /// <summary>Builds the "error" event from an aggregator report and enqueues it; optionally asks for immediate delivery.</summary>
        private static void EmitErrorReport(ErrorAggregator.Report report, Dictionary<string, object> extra, bool priorityFlush)
        {
            var props = new Dictionary<string, object>(6 + (extra != null ? extra.Count : 0));

            // Copy user-supplied extras first so the reserved keys below always win
            // (membership/identity of a crash event can't be spoofed by extras).
            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    props[kv.Key] = kv.Value;
                }
            }

            props["severity"] = report.Severity;
            props["log_type"] = report.LogType;
            props["condition"] = report.Condition;
            // Stable per-signature id + cumulative session count, so the backend can
            // group by error_id and take max(count) (robust to at-least-once resend).
            props["error_id"] = report.Hash.ToString("x8");
            props["count"] = report.Count;

            if (!string.IsNullOrEmpty(report.StackTrace))
            {
                props["stack_trace"] = report.StackTrace;
            }

            LogEvent(ErrorEventName, props);

            if (priorityFlush)
            {
                RequestPriorityFlush();
            }
        }

        /// <summary>Asks the runner to deliver on the next frame instead of waiting for the flush timer.</summary>
        private static void RequestPriorityFlush()
        {
            _dispatcher?.RequestPriorityFlush();
        }

        /// <summary>
        /// Emits the accumulated tail counts for recurring errors — called on
        /// pause/quit before PersistPending, so a burst right before backgrounding
        /// isn't lost. No priority flush; the caller persists/flushes next.
        /// </summary>
        private static void FlushErrorAggregatesInternal()
        {
            if (_errorAggregator == null)
            {
                return;
            }

            var pending = _errorAggregator.DrainPending(ErrorClockSeconds());
            for (var i = 0; i < pending.Count; i++)
            {
                EmitErrorReport(pending[i], null, priorityFlush: false);
            }
        }

        /// <summary>
        /// Thread-safe elapsed-seconds source for the error aggregator's throttle
        /// window. Unlike Time.realtimeSinceStartup (main-thread only), this can be
        /// called from a background-thread log capture. Only deltas matter to the
        /// aggregator, so the arbitrary origin is fine; Stopwatch is monotonic.
        /// </summary>
        private static float ErrorClockSeconds() =>
            (float)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);

        // ----- native crash handoff -----------------------------------------

        /// <summary>
        /// Directory a platform crash handler writes native crash records into
        /// (one <c>*.json</c> file per crash; see <see cref="NativeCrashRecord"/>).
        /// Native writers use <c>Application.persistentDataPath</c> + this subpath.
        /// </summary>
        private static string NativeCrashDirectory() =>
            System.IO.Path.Combine(Application.persistentDataPath, "gamemetric", "native-crashes");

        /// <summary>
        /// On startup, drains any native crash records left by a platform handler
        /// before the process died last run: parse each, emit it (back-dated to the
        /// crash time) through the normal pipeline, then delete the file. Best-effort
        /// and fully guarded — a bad handoff file must never break Initialize.
        /// </summary>
        private static void ReportPendingNativeCrashesInternal()
        {
            string[] files;
            try
            {
                var dir = NativeCrashDirectory();
                if (!System.IO.Directory.Exists(dir))
                {
                    return;
                }

                files = System.IO.Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Could not scan native-crash directory: " + ex.Message);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    if (NativeCrashRecord.TryParse(json, out var record))
                    {
                        EmitNativeCrash(record);
                    }
                    else
                    {
                        GameMetricLog.Warn("Discarding unparseable native-crash file: " + System.IO.Path.GetFileName(file));
                    }
                }
                catch (Exception ex)
                {
                    GameMetricLog.Warn("Failed to read native-crash file: " + ex.Message);
                }
                finally
                {
                    // One-shot: once emitted the event lives in our durable pipeline
                    // (queue + offline cache), so drop the source regardless of the
                    // outcome to avoid re-emitting or a poison file looping forever.
                    try { System.IO.File.Delete(file); } catch { /* best effort */ }
                }
            }
        }

        /// <summary>Emits a parsed native crash as a fatal "error" event, back-dated to when the crash happened.</summary>
        private static void EmitNativeCrash(NativeCrashRecord record)
        {
            const string severity = "fatal";
            var condition = Truncate(
                !string.IsNullOrEmpty(record.Message) ? record.Message
                : !string.IsNullOrEmpty(record.Type) ? record.Type
                : "Native crash",
                MaxConditionChars);
            var stack = string.IsNullOrEmpty(record.Stack) ? null : Truncate(record.Stack, MaxStackTraceChars);
            var hash = ErrorAggregator.SignatureHash(severity, condition, stack);

            var props = new Dictionary<string, object>(8)
            {
                { "severity", severity },
                { "log_type", "Native" },
                { "condition", condition },
                { "error_id", hash.ToString("x8") },
                { "count", 1 },
                { "is_native", true },
            };

            if (!string.IsNullOrEmpty(stack)) props["stack_trace"] = stack;
            if (!string.IsNullOrEmpty(record.Type)) props["crash_type"] = record.Type;
            if (!string.IsNullOrEmpty(record.Platform)) props["native_platform"] = record.Platform;
            if (!string.IsNullOrEmpty(record.BuildId)) props["build_id"] = record.BuildId;

            // Back-date to the real crash time (from the record) so the event isn't
            // mis-attributed to this launch; fall back to now if it wasn't recorded.
            var when = record.HasTimestamp ? record.TimestampUtc : DateTime.UtcNow;
            LogEventInternal(ErrorEventName, when, props);
        }

        // ----- session lifecycle (wired to the runner) ----------------------

        /// <summary>On background: remember when, and flush the error tail before the queue is persisted.</summary>
        private static void HandlePauseInternal()
        {
            _session.Pause(DateTime.UtcNow);
            FlushErrorAggregatesInternal();
        }

        /// <summary>
        /// On resume: if the app was backgrounded past the timeout, the previous
        /// session ended when the user left — close it (back-dated) and open a new
        /// one. Then refresh remote config.
        /// </summary>
        private static void HandleResumeInternal()
        {
            if (_session.ResumeStartsNewSession(DateTime.UtcNow, out var endedAtUtc, out var endedDurationSeconds))
            {
                // Emit session_end for the OLD session before rotating the id.
                EmitSessionEnd(endedAtUtc, endedDurationSeconds);
                StartNewSession();
            }

            if (_config.AutoFetchRemoteConfig)
            {
                RefreshRemoteConfigInternal();
            }
        }

        /// <summary>On quit: close the current session (at the pause time if backgrounded, else now), then flush the error tail.</summary>
        private static void HandleQuitInternal()
        {
            _session.QuitEndInfo(DateTime.UtcNow, out var endUtc, out var durationSeconds);
            EmitSessionEnd(endUtc, durationSeconds);
            FlushErrorAggregatesInternal();
        }

        /// <summary>Emits session_end for the CURRENT session, back-dated to <paramref name="endUtc"/> so backend min/max-based duration isn't inflated by idle background time.</summary>
        private static void EmitSessionEnd(DateTime endUtc, long durationSeconds)
        {
            LogEventInternal("session_end", endUtc, new Dictionary<string, object>(1)
            {
                { "duration_seconds", durationSeconds },
            });
        }

        /// <summary>Rotates to a fresh session: new id + start time, reset per-session error aggregation, and a new session_start.</summary>
        private static void StartNewSession()
        {
            _sessionId = Guid.NewGuid().ToString("N");
            _session.Start(DateTime.UtcNow);
            _errorAggregator = new ErrorAggregator(_config.MaxDistinctErrorsPerSession, _config.ErrorAggregationWindowSeconds);
            _errorCapWarned = false;

            LogEvent("session_start", BuildDeviceMetadata());
            GameMetricLog.Info("New session after background timeout. session=" + _sessionId);
        }

        private static string SeverityFor(LogType type)
        {
            switch (type)
            {
                case LogType.Exception:
                    return "exception";
                case LogType.Assert:
                    return "assert";
                default:
                    return "error";
            }
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }

            return value.Substring(0, maxChars);
        }

        /// <summary>Seeds the in-memory snapshot from the on-disk cache at Initialize. Does not raise OnConfigUpdated (nothing has "changed" at startup).</summary>
        private static void LoadCachedRemoteConfig()
        {
            var cached = _remoteConfigCache.LoadOrNull();
            if (string.IsNullOrEmpty(cached))
            {
                return;
            }

            try
            {
                _remoteConfig.UpdateFromJson(cached);
                GameMetricLog.Info("Remote config seeded from cache. experiments=" + _remoteConfig.ExperimentCount);
            }
            catch (Exception e)
            {
                GameMetricLog.Warn("Cached remote-config parse failed: " + e.Message);
            }
        }

        /// <summary>Internal auto-refresh entry point (timer/resume/initial). Guarded and debounced; fire-and-forget.</summary>
        private static void RefreshRemoteConfigInternal()
        {
            // Skip while opted out — a fetch would send the user id in the query.
            if (!_initialized || _runner == null || _remoteConfigFetching || !_collectionEnabled)
            {
                return;
            }

            // Debounce: skip if we fetched very recently (e.g. a resume landing
            // right after a periodic refresh, or rapid app switching).
            if (Time.realtimeSinceStartup - _lastConfigFetchRealtime < MinRefreshGapSeconds)
            {
                return;
            }

            _runner.StartCoroutine(FetchRemoteConfigRoutine(null));
        }

        /// <summary>Invokes OnConfigUpdated on the main thread, isolating subscriber exceptions from the fetch pipeline.</summary>
        private static void RaiseConfigUpdated()
        {
            var handler = OnConfigUpdated;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception e)
            {
                GameMetricLog.Warn("OnConfigUpdated subscriber threw: " + e.Message);
            }
        }

        // A null tcs means an internal fire-and-forget refresh; a non-null tcs
        // bridges completion back to FetchRemoteConfigAsync's awaitable Task.
        private static IEnumerator FetchRemoteConfigRoutine(TaskCompletionSource<bool> tcs)
        {
            if (_remoteConfigFetching)
            {
                tcs?.TrySetResult(false);
                yield break;
            }

            _remoteConfigFetching = true;
            _lastConfigFetchRealtime = Time.realtimeSinceStartup;

            try
            {
                var url = _config.RemoteConfigUrl + "?userId=" + UnityWebRequest.EscapeURL(_userId ?? string.Empty);

                using (var www = UnityWebRequest.Get(url))
                {
                    www.timeout = _config.RequestTimeoutSeconds;
                    www.SetRequestHeader("X-API-Key", _config.ApiKey);
                    yield return www.SendWebRequest();

                    if (www.result != UnityWebRequest.Result.Success)
                    {
                        GameMetricLog.Warn("Remote config fetch failed: " + www.error);
                        tcs?.TrySetResult(false);
                        yield break;
                    }

                    var ok = false;
                    var changed = false;
                    var json = www.downloadHandler.text;
                    try
                    {
                        changed = _remoteConfig.UpdateFromJson(json);
                        ok = true;
                    }
                    catch (Exception e)
                    {
                        // Bad payload shouldn't throw into Unity's coroutine
                        // machinery; keep the previous snapshot and report failure.
                        GameMetricLog.Warn("Remote config parse failed: " + e.Message);
                    }

                    if (ok)
                    {
                        // Only persist + notify when the values actually changed,
                        // so an unchanged refresh is silent and free of disk writes.
                        if (changed)
                        {
                            _remoteConfigCache.Save(json);
                            RaiseConfigUpdated();
                        }

                        GameMetricLog.Info("Remote config loaded. experiments=" + _remoteConfig.ExperimentCount
                            + (changed ? " (changed)" : " (unchanged)"));
                    }

                    tcs?.TrySetResult(ok);
                }
            }
            finally
            {
                _remoteConfigFetching = false;
            }
        }
#endif
    }
}