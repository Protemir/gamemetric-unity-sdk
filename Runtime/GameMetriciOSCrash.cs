#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;

namespace GameMetricSDK
{
    /// <summary>
    /// Thin bridge to the iOS native uncaught-exception handler
    /// (GameMetricCrashHandler.mm, statically linked → "__Internal"). Compiled only
    /// into iOS player builds; the .mm writes crash records that <see cref="GameMetric"/>
    /// picks up on the next launch.
    /// </summary>
    internal static class GameMetriciOSCrash
    {
        [DllImport("__Internal")]
        private static extern void gamemetric_install_crash_handler(string crashDir, string buildId);

        /// <summary>Installs the NSException handler. Idempotent on the native side.</summary>
        public static void Install(string crashDir, string buildId)
        {
            try
            {
                gamemetric_install_crash_handler(crashDir, buildId ?? string.Empty);
            }
            catch (Exception e)
            {
                // A missing symbol / linking issue must never break Initialize —
                // managed capture still works; we just don't get Obj-C crashes.
                GameMetricLog.Warn("iOS native crash handler not installed: " + e.Message);
            }
        }
    }
}
#endif
