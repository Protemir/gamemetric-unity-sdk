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

        // P2-2: nested dictionaries and lists serialize as real JSON, not
        // "System.Collections..." from a ToString() fallback.
        [Test]
        public void NestedObjectsAndArrays_SerializeAndRoundTrip()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            evt.Properties["nested"] = new Dictionary<string, object> { { "a", 1 }, { "b", "x" } };
            evt.Properties["list"] = new List<object> { 1, "two", true };
            evt.Properties["deep"] = new Dictionary<string, object>
            {
                { "arr", new List<object> { new Dictionary<string, object> { { "k", 9 } } } },
            };

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];

            var nested = (Dictionary<string, object>)props["nested"];
            Assert.AreEqual(1.0, (double)nested["a"]);
            Assert.AreEqual("x", nested["b"]);

            var list = (List<object>)props["list"];
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(1.0, (double)list[0]);
            Assert.AreEqual("two", list[1]);
            Assert.AreEqual(true, list[2]);

            var deepArr = (List<object>)((Dictionary<string, object>)props["deep"])["arr"];
            Assert.AreEqual(9.0, (double)((Dictionary<string, object>)deepArr[0])["k"]);
        }

        // P2-7: "G17" round-trips a double exactly (unlike the old "R").
        [Test]
        public void DoubleValues_RoundTripExactly()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            var a = 0.1 + 0.2;   // 0.30000000000000004
            var b = 1.0 / 3.0;
            evt.Properties["a"] = a;
            evt.Properties["b"] = b;

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];
            Assert.AreEqual(a, (double)props["a"]);
            Assert.AreEqual(b, (double)props["b"]);
        }

        [Test]
        public void FloatValue_RoundTripsToFloatPrecision()
        {
            var evt = new GameMetricEvent { EventName = "e" };
            const float f = 0.1f;
            evt.Properties["f"] = f;

            var props = (Dictionary<string, object>)WriteAndParse(evt)["properties"];
            Assert.AreEqual((double)f, (double)props["f"], 1e-6);
        }
    }
}
