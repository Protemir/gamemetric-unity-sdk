using System;
using System.Collections.Generic;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class RemoteConfigStoreTests
    {
        private const string Json =
            "{\"config\":{\"speed\":10,\"title\":\"hello\",\"hardcore\":true,\"price\":4.99,\"maxLives\":\"5\"}," +
            "\"activeExperiments\":{\"price_test\":\"Variant A\",\"onboarding\":\"control\"}}";

        private static RemoteConfigStore Loaded()
        {
            var store = new RemoteConfigStore();
            store.UpdateFromJson(Json);
            return store;
        }

        [Test]
        public void ChangeDetection_FirstChanged_IdenticalUnchanged_ModifiedChanged()
        {
            var store = new RemoteConfigStore();
            Assert.IsTrue(store.UpdateFromJson(Json), "first apply is a change");
            Assert.IsFalse(store.UpdateFromJson(Json), "identical re-apply is not a change");
            Assert.IsTrue(store.UpdateFromJson(Json.Replace("\"speed\":10", "\"speed\":20")), "modified payload is a change");
        }

        [Test]
        public void TryGet_TypedRetrieval()
        {
            var s = Loaded();
            Assert.IsTrue(s.TryGet<double>("speed", out var d)); Assert.AreEqual(10.0, d);
            Assert.IsTrue(s.TryGet<int>("speed", out var i)); Assert.AreEqual(10, i);
            Assert.IsTrue(s.TryGet<long>("speed", out var l)); Assert.AreEqual(10L, l);
            Assert.IsTrue(s.TryGet<string>("title", out var str)); Assert.AreEqual("hello", str);
            Assert.IsTrue(s.TryGet<bool>("hardcore", out var b)); Assert.IsTrue(b);
            Assert.IsTrue(s.TryGet<float>("price", out var f)); Assert.AreEqual(4.99f, f, 0.0001f);
        }

        [Test]
        public void TryGet_AbsentKey_ReturnsFalseAndDefault()
        {
            var s = Loaded();
            Assert.IsFalse(s.TryGet<int>("nope", out var v));
            Assert.AreEqual(0, v);
        }

        [Test]
        public void Coercion_AcrossTypes()
        {
            var s = Loaded();
            Assert.IsTrue(s.TryGet<double>("maxLives", out var d)); Assert.AreEqual(5.0, d);   // "5" -> double
            Assert.IsTrue(s.TryGet<int>("maxLives", out var i)); Assert.AreEqual(5, i);         // "5" -> int
            Assert.IsTrue(s.TryGet<string>("speed", out var str)); Assert.AreEqual("10", str);  // 10 -> "10"
        }

        [Test]
        public void GetWithFallback()
        {
            var s = Loaded();
            Assert.AreEqual(10, s.GetInt("speed", -1));
            Assert.AreEqual(-1, s.GetInt("nope", -1));
            Assert.AreEqual("def", s.GetString("nope", "def"));
            Assert.IsTrue(s.GetBool("hardcore", false));
        }

        [Test]
        public void Experiments_CountAndVariantLookup()
        {
            var s = Loaded();
            Assert.AreEqual(2, s.ExperimentCount);
            Assert.AreEqual("Variant A", s.GetVariant("price_test"));
            Assert.AreEqual("control", s.GetVariant("onboarding"));
            Assert.IsNull(s.GetVariant("missing"));
            Assert.AreEqual("control", s.GetVariant("missing", "control"));
            Assert.IsNull(s.GetVariant(null));

            Assert.IsTrue(s.TryGetVariant("price_test", out var v)); Assert.AreEqual("Variant A", v);
            Assert.IsFalse(s.TryGetVariant("missing", out var v2)); Assert.IsNull(v2);
        }

        [Test]
        public void ApplyExperimentTags_ReservedKeysOverwriteUserValues()
        {
            var s = Loaded();
            var props = new Dictionary<string, object> { { "ab_price_test", "spoofed" }, { "level", 3 } };
            s.ApplyExperimentTags(props);

            Assert.AreEqual("Variant A", props["ab_price_test"], "reserved ab_ key wins over user value");
            Assert.AreEqual("control", props["ab_onboarding"]);
            Assert.AreEqual(3, props["level"], "unrelated property untouched");
        }

        [Test]
        public void MalformedJson_Throws_AndKeepsPreviousSnapshot()
        {
            var s = Loaded();
            Assert.Throws<FormatException>(() => s.UpdateFromJson("not json"));
            Assert.AreEqual(10, s.GetInt("speed", -1), "previous snapshot retained after a bad payload");
        }

        [Test]
        public void ContainsKey()
        {
            var s = Loaded();
            Assert.IsTrue(s.ContainsKey("speed"));
            Assert.IsFalse(s.ContainsKey("nope"));
        }
    }
}
