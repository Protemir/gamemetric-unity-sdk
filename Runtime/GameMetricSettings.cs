using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Persisted SDK configuration, edited via Edit → Project Settings → GameMetric
    /// and stored at Assets/Resources/GameMetricSettings.asset so it can be loaded
    /// at runtime by <see cref="GameMetric.Initialize()"/> (the parameterless overload).
    /// </summary>
    public sealed class GameMetricSettings : ScriptableObject
    {
        /// <summary>Resources name (no extension) used by <see cref="LoadOrNull"/>.</summary>
        public const string ResourceName = "GameMetricSettings";

        [SerializeField]
        [Tooltip("Per-project ingestion key from Project Settings → API Key.")]
        private string apiKey = "";

        [SerializeField]
        [Tooltip("Game build/release version reported with every event. Blank falls back to Application.version.")]
        private string gameVersion = "";

        [SerializeField]
        [Tooltip("Log delivery/cache activity to the console. Leave off in shipping builds.")]
        private bool enableDebugLogs = false;

        [SerializeField]
        [Tooltip("Backend base URL. Leave blank for production. Override for local/staging testing, e.g. http://localhost:5000.")]
        private string baseUrl = "";

        public string ApiKey => apiKey;
        public string GameVersion => gameVersion;
        public bool EnableDebugLogs => enableDebugLogs;

        /// <summary>Backend base URL override; blank means use the production default.</summary>
        public string BaseUrl => baseUrl;

        /// <summary>Loads the settings asset from a Resources folder, or null if none exists.</summary>
        public static GameMetricSettings LoadOrNull()
        {
            return Resources.Load<GameMetricSettings>(ResourceName);
        }
    }
}