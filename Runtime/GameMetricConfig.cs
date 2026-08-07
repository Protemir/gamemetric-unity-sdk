using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Runtime configuration for the SDK. Created by <see cref="GameMetric.Initialize"/>;
    /// the defaults match the GameMetric ingestion API and are safe for most games.
    /// </summary>
    public sealed class GameMetricConfig
    {
        /// <summary>Default production backend base URL. All endpoints are derived from a base URL.</summary>
        public const string DefaultBaseUrl = "https://api.gamemetric.dev";

        /// <summary>Default ingestion endpoint; kept for the editor test button and back-compat.</summary>
        public const string DefaultEndpointUrl = DefaultBaseUrl + "/v1/events";

        /// <summary>Default remote-config endpoint (GET, keyed by X-API-Key).</summary>
        public const string DefaultRemoteConfigUrl = DefaultBaseUrl + "/v1/remote-config";

        public string ApiKey { get; }

        /// <summary>Backend base URL (no trailing slash). Override for local/staging testing, e.g. http://localhost:5000.</summary>
        public string BaseUrl { get; }

        /// <summary>Ingestion endpoint (BaseUrl + /v1/events). Accepts a JSON array of events.</summary>
        public string EndpointUrl { get; }

        /// <summary>Remote-config endpoint (BaseUrl + /v1/remote-config). GET with X-API-Key; a ?userId= query is appended at fetch time.</summary>
        public string RemoteConfigUrl { get; }

        /// <summary>Flush the queue at least this often, regardless of size.</summary>
        public float FlushIntervalSeconds = 10f;

        /// <summary>Flush early once the queue reaches this many events.</summary>
        public int FlushThreshold = 50;

        /// <summary>
        /// Max events sent per request. Kept well below the server's 1000-event
        /// cap so a burst still delivers in bounded, retry-friendly chunks.
        /// </summary>
        public int MaxBatchSize = 256;

        /// <summary>Cap on events retained in the offline cache; oldest are dropped past this.</summary>
        public int MaxCachedEvents = 5000;

        /// <summary>
        /// Per-request network timeout (seconds). Bounds how long a stalled
        /// connection can block delivery: without it a hung socket would keep the
        /// dispatcher's send guard set until the platform's default timeout,
        /// wedging all further flushes for that whole window.
        /// </summary>
        public int RequestTimeoutSeconds = 15;

        /// <summary>
        /// Gzip the request body. OFF by default: the server must have request
        /// decompression enabled to accept it, otherwise the JSON body is
        /// unreadable. Turn on only against a backend that supports it.
        /// </summary>
        public bool CompressRequests = false;

        /// <summary>
        /// When true, the SDK fetches remote config automatically — once shortly
        /// after Initialize, again on resume from background, and on the periodic
        /// timer below — so config updates during long sessions without the game
        /// having to call <see cref="GameMetric.FetchRemoteConfigAsync"/> itself.
        /// Turn off to drive fetching entirely by hand.
        /// </summary>
        public bool AutoFetchRemoteConfig = true;

        /// <summary>
        /// Periodic auto-refresh interval for remote config (seconds). 0 disables
        /// the timer (a resume-triggered refresh still fires while
        /// <see cref="AutoFetchRemoteConfig"/> is on). Kept generous by default:
        /// config/balance changes are infrequent and each fetch is one request
        /// per player.
        /// </summary>
        public float RemoteConfigRefreshIntervalSeconds = 600f;

        /// <summary>
        /// Minimum time (seconds) the app must stay backgrounded before the next
        /// resume is treated as a NEW session (new SessionId + session_start, with
        /// a session_end closing the previous one). Matches the common 30-minute
        /// analytics default; lower it for games that want shorter sessions. A
        /// short background under this threshold keeps the same session.
        /// </summary>
        public float SessionTimeoutSeconds = 1800f;

        /// <summary>Emit informational delivery/cache logs (gated via GameMetricLog).</summary>
        public bool EnableDebugLogs = false;

        /// <summary>
        /// Automatically capture uncaught Unity logs of type Error/Assert/Exception
        /// as "error" events. On by default; turn off to disable crash reporting.
        /// </summary>
        public bool CaptureErrors = true;

        /// <summary>
        /// Cap on the number of DISTINCT error signatures captured per session.
        /// Bounds memory and event volume; errors already being tracked keep
        /// counting past this — only brand-new distinct errors are dropped.
        /// </summary>
        public int MaxDistinctErrorsPerSession = 100;

        /// <summary>
        /// Throttle window (seconds) for re-emitting a recurring error. The first
        /// occurrence is sent immediately; further occurrences of the same
        /// signature are counted in memory and re-sent at most once per window,
        /// carrying the running count — so a per-frame crash loop is a couple of
        /// events, not hundreds.
        /// </summary>
        public float ErrorAggregationWindowSeconds = 10f;

        public GameMetricConfig(string apiKey, string baseUrl = null)
        {
            ApiKey = apiKey;
            // Fall back to production when no base URL is supplied; trim a trailing
            // slash so "http://localhost:5000/" and ".../" both build clean paths.
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            EndpointUrl = BaseUrl + "/v1/events";
            RemoteConfigUrl = BaseUrl + "/v1/remote-config";
        }

        /// <summary>Maps the Unity runtime platform to the short, low-cardinality tag the backend expects.</summary>
        public static string ResolvePlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                    return "android";
                case RuntimePlatform.IPhonePlayer:
                    return "ios";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "windows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "macos";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "linux";
                case RuntimePlatform.WebGLPlayer:
                    return "web";
                default:
                    // Consoles and any future platforms fall through to their
                    // lowercased Unity name (e.g. "ps5", "xboxone").
                    return Application.platform.ToString().ToLowerInvariant();
            }
        }
    }
}