using System.Collections.Generic;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class InMemoryEventCacheTests
    {
        private static List<string> L(params string[] xs) => new List<string>(xs);

        [Test]
        public void AppendThenReadOldest()
        {
            var c = new InMemoryEventCache(5000);
            c.Append(L("a", "b", "c", "d"));
            Assert.IsTrue(c.HasData);
            CollectionAssert.AreEqual(new[] { "a", "b" }, c.Read(2));
        }

        [Test]
        public void RemoveFirst_KeepsTail()
        {
            var c = new InMemoryEventCache(5000);
            c.Append(L("a", "b", "c", "d"));
            c.RemoveFirst(2);
            CollectionAssert.AreEqual(new[] { "c", "d" }, c.Read(10));
        }

        [Test]
        public void RemoveMoreThanPresent_Empties()
        {
            var c = new InMemoryEventCache(5000);
            c.Append(L("a", "b"));
            c.RemoveFirst(5);
            Assert.IsFalse(c.HasData);
            Assert.AreEqual(0, c.Read(10).Count);
        }

        [Test]
        public void Append_TrimsToNewestWhenOverCap()
        {
            var c = new InMemoryEventCache(3);
            c.Append(L("1", "2"));
            c.Append(L("3", "4", "5"));   // total 5, cap 3 -> keep newest 3
            CollectionAssert.AreEqual(new[] { "3", "4", "5" }, c.Read(10));
        }

        [Test]
        public void BlankLinesAreIgnored()
        {
            var c = new InMemoryEventCache(5000);
            c.Append(L("a", "", "  ", "b"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, c.Read(10));
        }

        [Test]
        public void Clear_Empties()
        {
            var c = new InMemoryEventCache(5000);
            c.Append(L("a", "b", "c"));
            c.Clear();
            Assert.IsFalse(c.HasData);
            Assert.AreEqual(0, c.Read(10).Count);
        }

        [Test]
        public void CacheFirstPipeline_ReadRemoveReadNext()
        {
            var c = new InMemoryEventCache(5000);
            for (var i = 0; i < 10; i++) c.Append(L("e" + i));
            var first = c.Read(4);
            c.RemoveFirst(first.Count);
            var second = c.Read(4);
            Assert.AreEqual("e0", first[0]);
            Assert.AreEqual("e4", second[0]);
        }
    }
}
