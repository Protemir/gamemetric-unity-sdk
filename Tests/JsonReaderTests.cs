using System;
using System.Collections.Generic;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class JsonReaderTests
    {
        [Test]
        public void ParsesObjectWithMixedPrimitiveTypes()
        {
            var root = (Dictionary<string, object>)JsonReader.Parse("{\"s\":\"x\",\"n\":3.5,\"b\":true,\"z\":null}");
            Assert.AreEqual("x", root["s"]);
            Assert.AreEqual(3.5, (double)root["n"]);
            Assert.AreEqual(true, root["b"]);
            Assert.IsNull(root["z"]);
        }

        [Test]
        public void ParsesNestedArraysAndObjects()
        {
            var root = (Dictionary<string, object>)JsonReader.Parse("{\"a\":[1,2,{\"k\":\"v\"}]}");
            var arr = (List<object>)root["a"];
            Assert.AreEqual(3, arr.Count);
            Assert.AreEqual(1.0, (double)arr[0]);
            Assert.AreEqual("v", ((Dictionary<string, object>)arr[2])["k"]);
        }

        [Test]
        public void ParsesEscapesAndUnicode()
        {
            var root = (Dictionary<string, object>)JsonReader.Parse("{\"t\":\"a\\tb\\n\",\"u\":\"\\u0041\"}");
            Assert.AreEqual("a\tb\n", root["t"]);
            Assert.AreEqual("A", root["u"]);
        }

        [Test]
        public void ParsesEmptyObjectAndArray()
        {
            Assert.AreEqual(0, ((Dictionary<string, object>)JsonReader.Parse("{}")).Count);
            Assert.AreEqual(0, ((List<object>)JsonReader.Parse("[]")).Count);
        }

        [Test]
        public void ParsesNegativeAndExponentNumbers()
        {
            var root = (Dictionary<string, object>)JsonReader.Parse("{\"a\":-12.5,\"b\":1e3}");
            Assert.AreEqual(-12.5, (double)root["a"]);
            Assert.AreEqual(1000.0, (double)root["b"]);
        }

        [Test]
        public void MalformedInputThrows()
        {
            Assert.Throws<FormatException>(() => JsonReader.Parse(""));
            Assert.Throws<FormatException>(() => JsonReader.Parse("{"));
            Assert.Throws<FormatException>(() => JsonReader.Parse("{\"a\":}"));
            Assert.Throws<FormatException>(() => JsonReader.Parse("{} trailing"));
        }
    }
}
