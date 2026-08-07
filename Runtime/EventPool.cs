using System.Collections.Concurrent;
using System.Threading;

namespace GameMetricSDK
{
    /// <summary>
    /// Thread-safe object pool for <see cref="GameMetricEvent"/> so the logging
    /// hot path reuses instances instead of allocating one per event. Bounded so
    /// a burst can't grow the pool without limit.
    /// </summary>
    internal static class EventPool
    {
        private const int MaxPooled = 512;

        private static readonly ConcurrentQueue<GameMetricEvent> Pool = new ConcurrentQueue<GameMetricEvent>();
        private static int _count;

        public static GameMetricEvent Rent()
        {
            if (Pool.TryDequeue(out var evt))
            {
                Interlocked.Decrement(ref _count);
                return evt;
            }

            return new GameMetricEvent();
        }

        public static void Return(GameMetricEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            evt.Reset();

            if (Volatile.Read(ref _count) >= MaxPooled)
            {
                return;
            }

            Pool.Enqueue(evt);
            Interlocked.Increment(ref _count);
        }
    }
}