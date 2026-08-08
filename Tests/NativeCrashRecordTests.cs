using System;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class NativeCrashRecordTests
    {
        [Test]
        public void ParsesFullRecord()
        {
            const string json =
                "{\"schema\":1,\"platform\":\"android\",\"type\":\"SIGSEGV\"," +
                "\"message\":\"Segfault at 0x0\",\"stack\":\"f0\\nf1\"," +
                "\"build_id\":\"com.game@1.4.2 (arm64)\",\"timestamp\":\"2026-08-08T12:34:56.000Z\"}";

            Assert.IsTrue(NativeCrashRecord.TryParse(json, out var r));
            Assert.AreEqual(1, r.Schema);
            Assert.AreEqual("android", r.Platform);
            Assert.AreEqual("SIGSEGV", r.Type);
            Assert.AreEqual("Segfault at 0x0", r.Message);
            Assert.AreEqual("f0\nf1", r.Stack);
            Assert.AreEqual("com.game@1.4.2 (arm64)", r.BuildId);
            Assert.IsTrue(r.HasTimestamp);
            Assert.AreEqual(new DateTime(2026, 8, 8, 12, 34, 56, DateTimeKind.Utc), r.TimestampUtc);
        }

        [Test]
        public void StackAsArray_IsJoinedWithNewlines()
        {
            const string json = "{\"type\":\"SIGABRT\",\"stack\":[\"frame0\",\"frame1\",\"frame2\"]}";
            Assert.IsTrue(NativeCrashRecord.TryParse(json, out var r));
            Assert.AreEqual("frame0\nframe1\nframe2", r.Stack);
        }

        [Test]
        public void MissingTimestamp_LeavesHasTimestampFalse()
        {
            const string json = "{\"type\":\"SIGSEGV\",\"message\":\"boom\"}";
            Assert.IsTrue(NativeCrashRecord.TryParse(json, out var r));
            Assert.IsFalse(r.HasTimestamp);
            Assert.AreEqual("boom", r.Message);
        }

        [Test]
        public void SchemaDefaultsToOne_WhenAbsent()
        {
            Assert.IsTrue(NativeCrashRecord.TryParse("{\"type\":\"SIGSEGV\"}", out var r));
            Assert.AreEqual(1, r.Schema);
        }

        [Test]
        public void MinimalRecord_WithOnlyType_Parses()
        {
            Assert.IsTrue(NativeCrashRecord.TryParse("{\"type\":\"SIGSEGV\"}", out var r));
            Assert.AreEqual("SIGSEGV", r.Type);
        }

        [Test]
        public void EmptyOrIdentityless_IsRejected()
        {
            Assert.IsFalse(NativeCrashRecord.TryParse("{}", out _));
            Assert.IsFalse(NativeCrashRecord.TryParse("{\"platform\":\"ios\",\"build_id\":\"x\"}", out _));
        }

        [Test]
        public void MalformedJson_IsRejected()
        {
            Assert.IsFalse(NativeCrashRecord.TryParse("not json", out _));
            Assert.IsFalse(NativeCrashRecord.TryParse("", out _));
            Assert.IsFalse(NativeCrashRecord.TryParse("[1,2,3]", out _));   // array root, not an object
        }
    }
}
