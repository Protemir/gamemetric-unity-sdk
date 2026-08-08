#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Thin bridge to the Android Java uncaught-exception handler
    /// (dev.gamemetric.sdk.GameMetricCrashHandler). Compiled only into Android
    /// player builds; a no-op everywhere else. The Java side writes crash records
    /// that <see cref="GameMetric"/> picks up on the next launch.
    /// </summary>
    internal static class GameMetricAndroidCrash
    {
        /// <summary>Installs the Java uncaught-exception handler. Safe to call more than once (idempotent on the Java side).</summary>
        public static void Install(string crashDir, string buildId)
        {
            try
            {
                using (var handler = new AndroidJavaClass("dev.gamemetric.sdk.GameMetricCrashHandler"))
                {
                    handler.CallStatic("install", crashDir, buildId ?? string.Empty);
                }
            }
            catch (Exception e)
            {
                // Missing plugin / JNI issue must never break Initialize — managed
                // capture still works; we just don't get Java-layer crashes.
                GameMetricLog.Warn("Android native crash handler not installed: " + e.Message);
            }
        }
    }
}
#endif
