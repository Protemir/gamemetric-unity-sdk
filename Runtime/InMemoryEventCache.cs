using System;
using System.Collections.Generic;

namespace GameMetricSDK
{
    /// <summary>
    /// In-memory replacement for the NDJSON offline cache, used on platforms where
    /// the on-disk cache can't work reliably — notably WebGL, whose
    /// Application.persistentDataPath is a virtual filesystem that isn't flushed to
    /// IndexedDB without FS.syncfs (so writes don't survive a reload), and which is
    /// single-threaded (so the disk path's Task.Run offloading can't run).
    ///
    /// This gives in-session retry (failed batches are re-sent within the session)
    /// without promising cross-reload persistence — the honest behavior for the web,
    /// where a page close loses un-sent events anyway. FIFO, oldest-first, capped to
    /// the newest <c>maxLines</c>. Unity-free so it can be unit-tested.
    /// </summary>
    internal sealed class InMemoryEventCache
    {
        private readonly int _maxLines;
        private readonly List<string> _lines = new List<string>();
        private readonly object _lock = new object();

        public InMemoryEventCache(int maxLines)
        {
            _maxLines = Math.Max(1, maxLines);
        }

        public bool HasData
        {
            get { lock (_lock) { return _lines.Count > 0; } }
        }

        /// <summary>Appends non-blank lines, then trims to the newest <c>maxLines</c> (drops oldest).</summary>
        public void Append(IEnumerable<string> lines)
        {
            if (lines == null)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _lines.Add(line);
                    }
                }

                if (_lines.Count > _maxLines)
                {
                    _lines.RemoveRange(0, _lines.Count - _maxLines);
                }
            }
        }

        /// <summary>Returns up to <paramref name="max"/> of the oldest lines (a copy).</summary>
        public List<string> Read(int max)
        {
            lock (_lock)
            {
                var n = Math.Min(Math.Max(max, 0), _lines.Count);
                return _lines.GetRange(0, n);
            }
        }

        /// <summary>Removes the oldest <paramref name="count"/> lines after delivery.</summary>
        public void RemoveFirst(int count)
        {
            if (count <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _lines.RemoveRange(0, Math.Min(count, _lines.Count));
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }
    }
}
