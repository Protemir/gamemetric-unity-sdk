using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Durable offline cache for events that couldn't be delivered (no network
    /// or a retryable server error). Stored as newline-delimited JSON — one
    /// serialized event object per line — under Application.persistentDataPath.
    ///
    /// NDJSON keeps append and trim cheap and lets us rebuild a request body by
    /// wrapping the lines in [ ... ] with no parsing.
    ///
    /// Threading model:
    /// - The heavy operations (read / append / trim) run on a thread-pool thread
    ///   via <see cref="Task.Run"/> so the periodic flush never blocks the Unity
    ///   main thread with disk I/O. Reads stream (File.ReadLines / StreamReader)
    ///   instead of loading the whole file, and rewrites go through a temp file
    ///   that atomically replaces the original, so a crash mid-write can't corrupt
    ///   the cache.
    /// - All file access is serialized by a single lock, so a background flush
    ///   write and a synchronous pause/quit persist can't interleave.
    /// - <see cref="AppendBlocking"/> is the one deliberately synchronous path:
    ///   on pause/quit the write must complete before the OS suspends or kills the
    ///   process, so it can't be deferred to a pool thread.
    /// - An in-memory line count makes <see cref="HasDataAsync"/> a pure memory
    ///   read after the first call (no per-flush stat syscall) and lets append
    ///   skip the whole-file trim unless the cap is actually exceeded.
    /// All operations are guarded so a filesystem hiccup degrades gracefully
    /// instead of throwing into the dispatch coroutine.
    /// </summary>
    internal sealed class EventStore
    {
        private readonly string _directory;
        private readonly string _filePath;
        private readonly string _tempFilePath;
        private readonly int _maxLines;
        private readonly object _lock = new object();

        // On WebGL the disk cache can't work reliably (virtual FS not flushed to
        // IndexedDB without FS.syncfs, and no threads for the Task.Run offload), so
        // we keep an in-memory cache instead: in-session retry without a false
        // cross-reload persistence promise.
        private readonly bool _inMemory;
        private readonly InMemoryEventCache _memory;

        // Authoritative in-memory count of cached (non-blank) lines (disk mode).
        // -1 = "not yet initialized from disk"; initialized lazily on first use.
        private int _lineCount = -1;

        public EventStore(int maxCachedEvents)
            : this(maxCachedEvents, Path.Combine(Application.persistentDataPath, "gamemetric"),
                   Application.platform == RuntimePlatform.WebGLPlayer)
        {
        }

        // Test seam: lets tests target an isolated directory (disk mode) or force
        // the in-memory backend. Production picks the backend from the platform.
        internal EventStore(int maxCachedEvents, string directory, bool inMemory = false)
        {
            _maxLines = Math.Max(1, maxCachedEvents);
            _directory = directory;
            _filePath = Path.Combine(_directory, "offline_events.ndjson");
            _tempFilePath = _filePath + ".tmp";
            _inMemory = inMemory;

            if (_inMemory)
            {
                _memory = new InMemoryEventCache(_maxLines);
                GameMetricLog.Info("Offline cache is in-memory (WebGL); events are not persisted across page reloads.");
            }
        }

        /// <summary>
        /// True when the cache holds at least one event. After the first call
        /// (which counts any leftover file off the main thread) this is answered
        /// from the in-memory count with no disk access.
        /// </summary>
        public Task<bool> HasDataAsync()
        {
            if (_inMemory)
            {
                return Task.FromResult(_memory.HasData);
            }

            lock (_lock)
            {
                if (_lineCount >= 0)
                {
                    return Task.FromResult(_lineCount > 0);
                }
            }

            return Task.Run(() =>
            {
                lock (_lock)
                {
                    EnsureCountInitialized();
                    return _lineCount > 0;
                }
            });
        }

        /// <summary>Reads up to <paramref name="max"/> of the oldest cached event lines (streamed, on a pool thread).</summary>
        public Task<List<string>> ReadAsync(int max)
        {
            if (_inMemory)
            {
                return Task.FromResult(_memory.Read(max));
            }

            return Task.Run(() =>
            {
                lock (_lock)
                {
                    EnsureCountInitialized();
                    return ReadOldest(max);
                }
            });
        }

        /// <summary>Removes the oldest <paramref name="count"/> lines after they've been delivered (on a pool thread).</summary>
        public Task RemoveFirstAsync(int count)
        {
            if (count <= 0)
            {
                return Task.CompletedTask;
            }

            if (_inMemory)
            {
                _memory.RemoveFirst(count);
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                lock (_lock)
                {
                    EnsureCountInitialized();
                    RemoveFirstCore(count);
                }
            });
        }

        /// <summary>
        /// Appends serialized event lines (then trims to the newest
        /// <see cref="_maxLines"/> if over cap) on a pool thread. The input is
        /// snapshotted on the calling thread, so the caller may reuse its buffer
        /// immediately without awaiting.
        /// </summary>
        public Task AppendAsync(IList<string> eventJsonLines)
        {
            if (eventJsonLines == null || eventJsonLines.Count == 0)
            {
                return Task.CompletedTask;
            }

            if (_inMemory)
            {
                _memory.Append(eventJsonLines);
                return Task.CompletedTask;
            }

            var snapshot = new List<string>(eventJsonLines);
            return Task.Run(() =>
            {
                lock (_lock)
                {
                    EnsureCountInitialized();
                    AppendCore(snapshot);
                }
            });
        }

        /// <summary>
        /// Synchronous, durable append for pause/quit. Runs inline (not on a pool
        /// thread) so the write is guaranteed to finish before the process is
        /// suspended or killed.
        /// </summary>
        public void AppendBlocking(IList<string> eventJsonLines)
        {
            if (eventJsonLines == null || eventJsonLines.Count == 0)
            {
                return;
            }

            if (_inMemory)
            {
                _memory.Append(eventJsonLines);
                return;
            }

            lock (_lock)
            {
                EnsureCountInitialized();
                AppendCore(eventJsonLines);
            }
        }

        /// <summary>Deletes the entire offline cache. Used when a user opts out of collection.</summary>
        public void Clear()
        {
            if (_inMemory)
            {
                _memory.Clear();
                return;
            }

            lock (_lock)
            {
                try
                {
                    TryDelete(_tempFilePath);
                    if (File.Exists(_filePath))
                    {
                        File.Delete(_filePath);
                    }
                }
                catch (Exception ex)
                {
                    GameMetricLog.Warn("Failed to clear offline cache: " + ex.Message);
                }

                _lineCount = 0;
            }
        }

        // ----- core (all callers hold _lock) --------------------------------

        /// <summary>Counts the leftover file once per session so the in-memory count is authoritative thereafter.</summary>
        private void EnsureCountInitialized()
        {
            if (_lineCount >= 0)
            {
                return;
            }

            _lineCount = 0;
            try
            {
                if (File.Exists(_filePath))
                {
                    var n = 0;
                    foreach (var line in File.ReadLines(_filePath))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            n++;
                        }
                    }

                    _lineCount = n;
                }
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Failed to init offline cache count: " + ex.Message);
                _lineCount = 0;
            }
        }

        private List<string> ReadOldest(int max)
        {
            var result = new List<string>(Math.Min(Math.Max(max, 0), 256));
            if (max <= 0)
            {
                return result;
            }

            try
            {
                if (!File.Exists(_filePath))
                {
                    return result;
                }

                // File.ReadLines streams lazily — we stop after `max` without
                // loading the whole file into memory.
                foreach (var line in File.ReadLines(_filePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    result.Add(line);
                    if (result.Count >= max)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Failed to read offline cache: " + ex.Message);
            }

            return result;
        }

        private void AppendCore(IList<string> eventJsonLines)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                var written = 0;
                using (var writer = new StreamWriter(_filePath, append: true))
                {
                    foreach (var line in eventJsonLines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        writer.WriteLine(line);
                        written++;
                    }
                }

                if (written == 0)
                {
                    return;
                }

                _lineCount += written;

                // Only pay for a full rewrite when the cap is actually exceeded —
                // in steady state this never runs.
                if (_lineCount > _maxLines)
                {
                    RemoveFirstCore(_lineCount - _maxLines);
                }
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Failed to write offline cache: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops the oldest <paramref name="count"/> lines by streaming the file
        /// through a temp copy and atomically replacing the original. Updates the
        /// in-memory count to the retained total.
        /// </summary>
        private void RemoveFirstCore(int count)
        {
            if (count <= 0)
            {
                return;
            }

            try
            {
                if (!File.Exists(_filePath))
                {
                    _lineCount = 0;
                    return;
                }

                Directory.CreateDirectory(_directory);

                var kept = 0;
                var skipped = 0;
                using (var reader = new StreamReader(_filePath))
                using (var writer = new StreamWriter(_tempFilePath, append: false))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        if (skipped < count)
                        {
                            skipped++;
                            continue;
                        }

                        writer.WriteLine(line);
                        kept++;
                    }
                }

                if (kept == 0)
                {
                    // Nothing left — drop both files rather than keep an empty one.
                    TryDelete(_tempFilePath);
                    TryDelete(_filePath);
                }
                else
                {
                    ReplaceFile(_tempFilePath, _filePath);
                }

                _lineCount = kept;
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Failed to trim offline cache: " + ex.Message);
                TryDelete(_tempFilePath);
            }
        }

        /// <summary>Atomically replaces <paramref name="target"/> with <paramref name="temp"/>, with a best-effort fallback.</summary>
        private static void ReplaceFile(string temp, string target)
        {
            try
            {
                if (File.Exists(target))
                {
                    // File.Replace is atomic on filesystems that support it.
                    File.Replace(temp, target, null);
                }
                else
                {
                    File.Move(temp, target);
                }

                return;
            }
            catch
            {
                // Some platforms/filesystems don't support File.Replace — fall
                // back to delete-then-move.
            }

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (File.Exists(temp))
                {
                    File.Move(temp, target);
                }
            }
            catch (Exception ex)
            {
                GameMetricLog.Warn("Failed to replace offline cache file: " + ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort — a leftover temp/stale file is harmless.
            }
        }
    }
}
