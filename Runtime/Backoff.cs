namespace GameMetricSDK
{
    /// <summary>
    /// Pure helper for the retry backoff schedule: a capped exponential delay with
    /// symmetric jitter, so a fleet of clients that failed together (e.g. during a
    /// backend outage) don't all retry in lockstep and hammer the server the moment
    /// it recovers — the "thundering herd". Unity-free so it can be unit-tested;
    /// the caller supplies the random sample.
    /// </summary>
    internal static class Backoff
    {
        /// <summary>
        /// Delay before retry attempt number <paramref name="consecutiveFailures"/>
        /// (1-based): <c>min(base * 2^(n-1), max)</c> scaled by a jitter factor in
        /// <c>[1 - jitter, 1 + jitter)</c>. <paramref name="rand01"/> is a uniform
        /// sample in <c>[0, 1)</c> supplied by the caller so this stays deterministic
        /// and testable.
        /// </summary>
        public static float DelaySeconds(int consecutiveFailures, float baseSeconds, float maxSeconds, float jitter, double rand01)
        {
            if (consecutiveFailures < 1)
            {
                consecutiveFailures = 1;
            }

            var capped = System.Math.Min(baseSeconds * System.Math.Pow(2.0, consecutiveFailures - 1), maxSeconds);
            var factor = 1.0 + (rand01 * 2.0 - 1.0) * jitter;
            return (float)(capped * factor);
        }
    }
}
