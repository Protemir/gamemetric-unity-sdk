using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class BackoffTests
    {
        // With zero jitter the schedule is deterministic regardless of the sample.
        [Test]
        public void ExponentialGrowth_NoJitter()
        {
            Assert.AreEqual(2f, Backoff.DelaySeconds(1, 2f, 60f, 0f, 0.5), 1e-4);
            Assert.AreEqual(4f, Backoff.DelaySeconds(2, 2f, 60f, 0f, 0.5), 1e-4);
            Assert.AreEqual(8f, Backoff.DelaySeconds(3, 2f, 60f, 0f, 0.5), 1e-4);
            Assert.AreEqual(16f, Backoff.DelaySeconds(4, 2f, 60f, 0f, 0.5), 1e-4);
            Assert.AreEqual(32f, Backoff.DelaySeconds(5, 2f, 60f, 0f, 0.5), 1e-4);
        }

        [Test]
        public void CapsAtMax_NoJitter()
        {
            Assert.AreEqual(60f, Backoff.DelaySeconds(6, 2f, 60f, 0f, 0.5), 1e-4);   // would be 64
            Assert.AreEqual(60f, Backoff.DelaySeconds(20, 2f, 60f, 0f, 0.5), 1e-4);  // far past the cap
        }

        [Test]
        public void JitterHitsLowerAndUpperBounds()
        {
            // rand01 = 0 -> factor 0.8 (lower bound); rand01 -> 1 -> factor ~1.2.
            Assert.AreEqual(1.6f, Backoff.DelaySeconds(1, 2f, 60f, 0.2f, 0.0), 1e-4);
            Assert.AreEqual(2.4f, Backoff.DelaySeconds(1, 2f, 60f, 0.2f, 1.0), 1e-4);
            // Midpoint sample -> no change.
            Assert.AreEqual(2.0f, Backoff.DelaySeconds(1, 2f, 60f, 0.2f, 0.5), 1e-4);
        }

        [Test]
        public void JitterStaysWithinTwentyPercent()
        {
            // Across the sample range, a capped 60s delay stays within [48, 72).
            for (var i = 0; i <= 100; i++)
            {
                var d = Backoff.DelaySeconds(10, 2f, 60f, 0.2f, i / 100.0);
                Assert.GreaterOrEqual(d, 48f - 1e-3);
                Assert.LessOrEqual(d, 72f + 1e-3);
            }
        }

        [Test]
        public void ClampsNonPositiveAttemptToFirst()
        {
            Assert.AreEqual(2f, Backoff.DelaySeconds(0, 2f, 60f, 0f, 0.5), 1e-4);
            Assert.AreEqual(2f, Backoff.DelaySeconds(-5, 2f, 60f, 0f, 0.5), 1e-4);
        }
    }
}
