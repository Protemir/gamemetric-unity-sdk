using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class JsonWriterTests
    {
        private static Dictionary<string, object> WriteAndParse(GameMetricEvent evt)
        {
            var sb = new StringBuilder();
            JsonWriter.WriteEvent(sb, evt);
            return (Dictionary<string, object>)JsonReader.Parse(sb.ToString());
        }

        [Test]
        public void WritesEventEnvelopeFields()
        {
            var evt = new GameMetricEvent
            {
                EventId = "abc123",
                UserId = "u1",
                SessionId = "s1",
                EventName = "level_up",
                Platform = "android",
                Version = "1.2.3",
                EventTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            };

            var root = WriteAndParse(evt);
            Assert.AreEqual("abc123", root["event_id"]);
            Assert.AreEqual("u1", root["user_id"]);
            Assert.AreEqual("s1", root["session_id"]);
            Assert.AreEqual("level_up", root["event_name"]);
            Assert.AreEqual("android", root["platform"]);
            Assert.AreEqual("1.2.3", root["version"]);
            Assert.AreEqual("2026-01-02T03:04:05.000Z", root["event_time"]);
            Assert.IsInstanceOf<Dictionary<string, object>>(root["properties"]);
        }

        [Test]
        public void SerializesPrimitivePropertyTypes()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            evt.Properties["i"] = 42;
            evt.Properties["d"] = 3.5;
            evt.Properties["b"] = true;
            evt.Properties["s"] = "hi";
            evt.Properties["n"] = null;

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];
            Assert.AreEqual(42.0, (double)props["i"]);
            Assert.AreEqual(3.5, (double)props["d"]);
            Assert.AreEqual(true, props["b"]);
            Assert.AreEqual("hi", props["s"]);
            Assert.IsNull(props["n"]);
        }

        // Regression test for the P0 fix: non-finite floats must not produce invalid
        // JSON (bare NaN/Infinity) that would fail the whole batch server-side.
        [Test]
        public void NaNAndInfinity_SerializeAsNull()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            evt.Properties["nan"] = double.NaN;
            evt.Properties["posinf"] = double.PositiveInfinity;
            evt.Properties["neginf"] = float.NegativeInfinity;

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];
            Assert.IsNull(props["nan"]);
            Assert.IsNull(props["posinf"]);
            Assert.IsNull(props["neginf"]);
        }

        [Test]
        public void EscapesSpecialCharactersInStrings()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            const string tricky = "he said \"hi\"\n\tand \\ left";
            evt.Properties["q"] = tricky;

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];
            Assert.AreEqual(tricky, props["q"]);
        }
    }
}
