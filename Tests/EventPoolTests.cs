using System;
using NUnit.Framework;
using GameMetricSDK;

namespace GameMetricSDK.Tests
{
    public class EventPoolTests
    {
        [Test]
        public void Rent_ReturnsNonNull()
        {
            Assert.IsNotNull(EventPool.Rent());
        }

        [Test]
        public void Return_Null_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => EventPool.Return(null));
        }

        [Test]
        public void Return_ResetsEventBeforePooling()
        {
            var e = EventPool.Rent();
            e.EventId = "id";
            e.EventName = "dirty";
            e.UserId = "u";
            e.Properties["k"] = 1;
            EventPool.Return(e);

            // Return() resets before pooling, so every event handed out by Rent()
            // is clean — whether it's the one just returned or a fresh instance.
            var next = EventPool.Rent();
            Assert.IsNull(next.EventId);
            Assert.IsNull(next.EventName);
            Assert.IsNull(next.UserId);
            Assert.AreEqual(0, next.Properties.Count);
        }

        [Test]
        public void GameMetricEvent_Reset_ClearsEveryField()
        {
            var e = new GameMetricEvent
            {
                EventId = "id",
                UserId = "u",
                SessionId = "s",
                EventName = "n",
                Platform = "p",
                Version = "v",
                EventTimeUtc = DateTime.UtcNow,
            };
            e.Properties["k"] = 1;

            e.Reset();

            Assert.IsNull(e.EventId);
            Assert.IsNull(e.UserId);
            Assert.IsNull(e.SessionId);
            Assert.IsNull(e.EventName);
            Assert.IsNull(e.Platform);
            Assert.IsNull(e.Version);
            Assert.AreEqual(default(DateTime), e.EventTimeUtc);
            Assert.AreEqual(0, e.Properties.Count);
        }
    }
}
