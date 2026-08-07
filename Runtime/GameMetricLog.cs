using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Tiny logging facade. Informational messages are gated behind
    /// <see cref="DebugEnabled"/> (set from settings at Initialize) so a shipping
    /// game stays quiet, while warnings and errors always surface.
    /// </summary>
    internal static class GameMetricLog
    {
        /// <summary>
        /// Prefix on every SDK log line. Public so the SDK's own error-capture can
        /// skip its own messages and avoid a Debug.LogError → capture feedback loop.
        /// </summary>
        public const string Prefix = "[GameMetric] ";

        public static bool DebugEnabled;

        public static void Info(string message)
        {
            if (DebugEnabled)
            {
                Debug.Log(Prefix + message);
            }
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}