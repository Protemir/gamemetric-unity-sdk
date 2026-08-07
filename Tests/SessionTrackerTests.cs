using System;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class SessionTrackerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void Resume_AfterTimeout_StartsNewSession_BackDatedToPause()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.Pause(T0.AddSeconds(100));                                   // 100s of active play
            var startsNew = s.ResumeStartsNewSession(T0.AddSeconds(100 + 1800), out var endedAt, out var dur);

            Assert.IsTrue(startsNew);
            Assert.AreEqual(T0.AddSeconds(100), endedAt, "ends at the pause time, not the resume time");
            Assert.AreEqual(100, dur, "duration excludes the idle background gap");
        }

        [Test]
        public void Resume_WithinTimeout_KeepsSameSession()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.Pause(T0.AddSeconds(50));
            Assert.IsFalse(s.ResumeStartsNewSession(T0.AddSeconds(50 + 1799), out _, out _));
        }

        [Test]
        public void Resume_ExactlyAtTimeout_StartsNewSession()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.Pause(T0.AddSeconds(10));
            Assert.IsTrue(s.ResumeStartsNewSession(T0.AddSeconds(10 + 1800), out _, out _), ">= timeout is a new session");
        }

        [Test]
        public void Resume_WithoutPause_IsNoOp()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            Assert.IsFalse(s.ResumeStartsNewSession(T0.AddSeconds(9999), out _, out _));
        }

        [Test]
        public void Quit_WhileActive_EndsNowWithFullDuration()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.QuitEndInfo(T0.AddSeconds(250), out var endUtc, out var dur);
            Assert.AreEqual(T0.AddSeconds(250), endUtc);
            Assert.AreEqual(250, dur);
        }

        [Test]
        public void Quit_WhilePaused_EndsAtPauseTime()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.Pause(T0.AddSeconds(120));
            s.QuitEndInfo(T0.AddSeconds(500), out var endUtc, out var dur);   // quit later without resuming
            Assert.AreEqual(T0.AddSeconds(120), endUtc);
            Assert.AreEqual(120, dur);
        }

        [Test]
        public void Start_RotatesDurationBaseline()
        {
            var s = new SessionTracker(1800);
            s.Start(T0);
            s.Pause(T0.AddSeconds(100));
            s.ResumeStartsNewSession(T0.AddSeconds(100 + 1800), out _, out _);
            s.Start(T0.AddSeconds(100 + 1800));                              // caller rotates to a fresh session
            s.QuitEndInfo(T0.AddSeconds(100 + 1800 + 30), out _, out var dur);
            Assert.AreEqual(30, dur, "new session duration measured from its own start");
        }

        [Test]
        public void Duration_IsClampedNonNegative()
        {
            var s = new SessionTracker(1800);
            s.Start(T0.AddSeconds(100));
            s.QuitEndInfo(T0, out _, out var dur);   // clock skew: "end" before start
            Assert.AreEqual(0, dur);
        }
    }
}
