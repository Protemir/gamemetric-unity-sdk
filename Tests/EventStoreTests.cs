using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    /// <summary>
    /// Exercises the real EventStore against an isolated temp directory (via the
    /// internal test-seam constructor), so these run under the Unity Test Runner
    /// where Application paths and the thread pool are available. The async file
    /// ops are awaited synchronously — safe in EditMode since nothing here touches
    /// the Unity main-thread API from the pool thread.
    /// </summary>
    public class EventStoreTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Application.temporaryCachePath, "gm_test_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // Best effort — the temp dir is unique per test.
            }
        }

        private EventStore NewStore(int max = 5000) => new EventStore(max, _dir);

        private static List<string> L(params string[] xs) => new List<string>(xs);

        [Test]
        public void AppendThenReadOldest()
        {
            var s = NewStore();
            s.AppendBlocking(L("a", "b", "c", "d"));

            Assert.IsTrue(s.HasDataAsync().GetAwaiter().GetResult());
            var read = s.ReadAsync(2).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "a", "b" }, read);
        }

        [Test]
        public void RemoveFirst_KeepsTail()
        {
            var s = NewStore();
            s.AppendBlocking(L("a", "b", "c", "d"));
            s.RemoveFirstAsync(2).GetAwaiter().GetResult();

            var read = s.ReadAsync(10).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "c", "d" }, read);
        }

        [Test]
        public void RemoveAll_ClearsCache()
        {
            var s = NewStore();
            s.AppendBlocking(L("a", "b"));
            s.RemoveFirstAsync(5).GetAwaiter().GetResult();

            Assert.IsFalse(s.HasDataAsync().GetAwaiter().GetResult());
        }

        [Test]
        public void Append_TrimsToNewestWhenOverCap()
        {
            var s = NewStore(3);
            s.AppendBlocking(L("1", "2"));
            s.AppendBlocking(L("3", "4", "5"));   // total 5, cap 3 -> keep newest 3

            var read = s.ReadAsync(10).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "3", "4", "5" }, read);
        }

        [Test]
        public void ColdStart_CountsExistingFileFromPreviousSession()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(Path.Combine(_dir, "offline_events.ndjson"), new[] { "x", "y", "z" });

            var s = NewStore();
            Assert.IsTrue(s.HasDataAsync().GetAwaiter().GetResult());
            s.RemoveFirstAsync(1).GetAwaiter().GetResult();
            var read = s.ReadAsync(10).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "y", "z" }, read);
        }

        [Test]
        public void InMemoryMode_AppendReadRemoveClear()
        {
            // WebGL path: no disk, no Task.Run — the backend is an in-memory cache.
            var s = new EventStore(5000, "unused", inMemory: true);
            s.AppendBlocking(L("a", "b", "c"));
            Assert.IsTrue(s.HasDataAsync().GetAwaiter().GetResult());
            CollectionAssert.AreEqual(new[] { "a", "b" }, s.ReadAsync(2).GetAwaiter().GetResult());
            s.RemoveFirstAsync(2).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "c" }, s.ReadAsync(10).GetAwaiter().GetResult());
            s.Clear();
            Assert.IsFalse(s.HasDataAsync().GetAwaiter().GetResult());
        }

        [Test]
        public void Clear_DeletesTheEntireCache()
        {
            var s = NewStore();
            s.AppendBlocking(L("a", "b", "c"));
            Assert.IsTrue(s.HasDataAsync().GetAwaiter().GetResult());

            s.Clear();

            Assert.IsFalse(s.HasDataAsync().GetAwaiter().GetResult());
            Assert.AreEqual(0, s.ReadAsync(10).GetAwaiter().GetResult().Count);
        }

        [Test]
        public void CacheFirstPipeline_ReadRemoveReadNext()
        {
            var s = NewStore();
            for (var i = 0; i < 10; i++)
            {
                s.AppendBlocking(L("e" + i));
            }

            var first = s.ReadAsync(4).GetAwaiter().GetResult();
            s.RemoveFirstAsync(first.Count).GetAwaiter().GetResult();
            var second = s.ReadAsync(4).GetAwaiter().GetResult();

            Assert.AreEqual("e0", first[0]);
            Assert.AreEqual("e4", second[0]);
        }
    }
}
