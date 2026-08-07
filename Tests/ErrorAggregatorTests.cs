using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class ErrorAggregatorTests
    {
        [Test]
        public void FirstOccurrence_EmitsWithCountOne()
        {
            var agg = new ErrorAggregator(3, 10f);
            agg.Observe("exception", "Exception", "NRE", "stackA", 0f, out var emit, out var report, out var cap);

            Assert.IsTrue(emit);
            Assert.IsFalse(cap);
            Assert.AreEqual(1, report.Count);
            Assert.AreEqual("exception", report.Severity);
            Assert.AreEqual("Exception", report.LogType);
            Assert.AreEqual("NRE", report.Condition);
        }

        [Test]
        public void RecurrenceWithinWindow_IsSuppressed()
        {
            var agg = new ErrorAggregator(3, 10f);
            agg.Observe("exception", "Exception", "NRE", "stackA", 0f, out _, out _, out _);
            agg.Observe("exception", "Exception", "NRE", "stackA", 1f, out var emit, out _, out _);
            Assert.IsFalse(emit);
        }

        [Test]
        public void RecurrenceAfterWindow_EmitsCumulativeCount()
        {
            var agg = new ErrorAggregator(3, 10f);
            agg.Observe("exception", "Exception", "NRE", "stackA", 0f, out _, out _, out _);  // 1, emitted
            agg.Observe("exception", "Exception", "NRE", "stackA", 1f, out _, out _, out _);  // 2, suppressed
            agg.Observe("exception", "Exception", "NRE", "stackA", 11f, out var emit, out var report, out _); // 3

            Assert.IsTrue(emit);
            Assert.AreEqual(3, report.Count);
            Assert.AreEqual(1, agg.DistinctCount);
        }

        [Test]
        public void DistinctSignatures_AreTrackedIndependently()
        {
            var agg = new ErrorAggregator(3, 10f);
            agg.Observe("exception", "Exception", "NRE", "stackA", 0f, out _, out _, out _);
            agg.Observe("error", "Error", "OtherErr", "stackB", 0f, out var emit, out var report, out _);

            Assert.IsTrue(emit);
            Assert.AreEqual(1, report.Count);
            Assert.AreEqual("OtherErr", report.Condition);
            Assert.AreEqual(2, agg.DistinctCount);
        }

        [Test]
        public void DistinctCap_DropsNewSignaturesButKeepsCountingTracked()
        {
            var agg = new ErrorAggregator(2, 10f);
            agg.Observe("error", "Error", "A", "sA", 0f, out var e1, out _, out _);
            agg.Observe("error", "Error", "B", "sB", 0f, out var e2, out _, out _);
            agg.Observe("error", "Error", "C", "sC", 0f, out var e3, out _, out var cap3);

            Assert.IsTrue(e1);
            Assert.IsTrue(e2);
            Assert.IsFalse(e3, "new signature past the cap is not emitted");
            Assert.IsTrue(cap3, "new signature past the cap is flagged capReached");
            Assert.AreEqual(2, agg.DistinctCount);

            // An already-tracked signature keeps counting past the cap.
            agg.Observe("error", "Error", "A", "sA", 20f, out var e4, out var r4, out _);
            Assert.IsTrue(e4);
            Assert.AreEqual(2, r4.Count);
        }

        [Test]
        public void DrainPending_ReturnsUnreportedTailOnce()
        {
            var agg = new ErrorAggregator(10, 10f);
            agg.Observe("error", "Error", "D", "sD", 0f, out _, out _, out _);  // count 1, emitted
            agg.Observe("error", "Error", "D", "sD", 1f, out _, out _, out _);  // count 2, suppressed

            var first = agg.DrainPending(2f);
            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(2, first[0].Count);
            Assert.AreEqual("D", first[0].Condition);

            var second = agg.DrainPending(3f);
            Assert.AreEqual(0, second.Count, "nothing new to drain the second time");
        }

        [Test]
        public void SignatureHash_IsDeterministicAndDistinct()
        {
            Assert.AreEqual(
                ErrorAggregator.SignatureHash("error", "cond", "stack"),
                ErrorAggregator.SignatureHash("error", "cond", "stack"));
            Assert.AreNotEqual(
                ErrorAggregator.SignatureHash("error", "cond", "stack"),
                ErrorAggregator.SignatureHash("error", "cond", "STACK-different"));
            Assert.AreNotEqual(
                ErrorAggregator.SignatureHash("error", "cond", "stack"),
                ErrorAggregator.SignatureHash("exception", "cond", "stack"));
        }

        [Test]
        public void SignatureHash_IsNullSafe()
        {
            Assert.AreEqual(
                ErrorAggregator.SignatureHash("error", null, null),
                ErrorAggregator.SignatureHash("error", null, null));
        }
    }
}
