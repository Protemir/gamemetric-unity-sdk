using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace GameMetricSDK
{
    /// <summary>
    /// Owns the in-memory event queue and delivers it to the ingestion API in
    /// batches. Enqueue is lock-free and non-blocking; delivery runs as a
    /// coroutine (driven by <see cref="GameMetricRunner"/>) because UnityWebRequest
    /// must be used from the main thread. Failed batches are cached offline and
    /// re-sent cache-first on later flushes.
    /// </summary>
    internal sealed class EventDispatcher
    {
        private enum SendResult
        {
            Success,
            Retryable,
            Permanent,
        }

        private readonly GameMetricConfig _config;
        private readonly EventStore _store;
        private readonly RemoteConfigStore _remoteConfig;

        // Exponential backoff on retryable failures so we don't hammer a down or
        // flaky server: 2s, 4s, 8s, 16s, 32s, then capped at 60s. Reset on success.
        private const float BackoffBaseSeconds = 2f;
        private const float BackoffMaxSeconds = 60f;

        private readonly ConcurrentQueue<GameMetricEvent> _queue = new ConcurrentQueue<GameMetricEvent>();
        private int _queuedCount;
        private bool _sending;
        private int _consecutiveFailures;
        private float _backoffUntilRealtime;

        // Set (from any thread) when a high-priority event — a captured crash —
        // should be delivered on the next frame instead of waiting for the timer.
        private int _priorityFlush;

        // Reused across flushes to keep steady-state allocation down.
        private readonly List<GameMetricEvent> _batch = new List<GameMetricEvent>(64);
        private readonly List<string> _lines = new List<string>(64);

        // Immutable empty fallback for a faulted cache read — never mutated.
        private static readonly List<string> EmptyLines = new List<string>();
        private readonly StringBuilder _eventBuilder = new StringBuilder(1024);
        private readonly StringBuilder _bodyBuilder = new StringBuilder(8192);

        public EventDispatcher(GameMetricConfig config, EventStore store, RemoteConfigStore remoteConfig)
        {
            _config = config;
            _store = store;
            _remoteConfig = remoteConfig;
        }

        public int QueuedCount => Volatile.Read(ref _queuedCount);

        public bool IsSending => _sending;

        /// <summary>True while an exponential-backoff cooldown from a failed send is still active.</summary>
        public bool InBackoff => Time.realtimeSinceStartup < _backoffUntilRealtime;

        /// <summary>True when a crash/error asked for immediate delivery (see <see cref="RequestPriorityFlush"/>).</summary>
        public bool PriorityFlushRequested => Volatile.Read(ref _priorityFlush) == 1;

        /// <summary>Requests an immediate flush on the next frame, bypassing the timer and backoff. Safe from any thread.</summary>
        public void RequestPriorityFlush() => Interlocked.Exchange(ref _priorityFlush, 1);

        /// <summary>Clears the priority-flush request; called by the runner when it acts on it.</summary>
        public void ClearPriorityFlush() => Interlocked.Exchange(ref _priorityFlush, 0);

        /// <summary>Non-blocking enqueue from any thread. Serialization is deferred to flush time.</summary>
        public void Enqueue(GameMetricEvent evt)
        {
            _queue.Enqueue(evt);
            Interlocked.Increment(ref _queuedCount);
        }

        /// <summary>
        /// One flush pass: drain the offline cache first, then a live batch.
        /// Guarded so overlapping triggers (timer + threshold) don't double-send.
        /// </summary>
        public IEnumerator FlushRoutine()
        {
            if (_sending)
            {
                yield break;
            }

            _sending = true;

            // try/finally (no catch — legal around yield) is the watchdog: the
            // guard is cleared even if this coroutine is stopped or disposed
            // mid-flight (scene teardown, StopAllCoroutines, runner destroyed), so
            // a single interrupted flush can't wedge delivery permanently. The
            // per-request www.timeout in PostBatchRoutine bounds the common case
            // of a stalled socket.
            try
            {
                var offline = false;
                var hasDataTask = _store.HasDataAsync();
                yield return AwaitTask(hasDataTask);
                if (ResultOr(hasDataTask, false))
                {
                    var cacheResult = SendResult.Success;
                    yield return SendFromCacheRoutine(r => cacheResult = r);
                    offline = cacheResult == SendResult.Retryable;
                }

                if (offline)
                {
                    // No connectivity — skip the live network attempt this cycle
                    // and just persist the queue so nothing is lost. Serialization
                    // stays on the main thread; only the disk write goes to a pool
                    // thread (awaited so the next pass sees a settled cache).
                    var lines = DrainQueue();
                    if (lines != null && lines.Count > 0)
                    {
                        yield return AwaitTask(_store.AppendAsync(lines));
                    }
                }
                else
                {
                    yield return SendLiveRoutine();
                }
            }
            finally
            {
                _sending = false;
            }
        }

        /// <summary>
        /// Synchronously moves everything queued into the offline cache. Used on
        /// pause/quit, where the write must finish before the process is suspended
        /// or killed — so this path stays blocking on purpose.
        /// </summary>
        public void PersistPending()
        {
            var lines = DrainQueue();
            if (lines != null && lines.Count > 0)
            {
                _store.AppendBlocking(lines);
            }
        }

        /// <summary>Discards everything queued without sending or persisting it. Used on consent withdrawal.</summary>
        public void DiscardQueued()
        {
            while (_queue.TryDequeue(out var evt))
            {
                Interlocked.Decrement(ref _queuedCount);
                EventPool.Return(evt);
            }
        }

        /// <summary>
        /// Drains the in-memory queue into serialized NDJSON lines on the calling
        /// (main) thread — cheap string building, no disk. The returned list is
        /// handed to the file layer, which does the actual I/O off-thread.
        /// </summary>
        private List<string> DrainQueue()
        {
            List<string> lines = null;
            while (_queue.TryDequeue(out var evt))
            {
                Interlocked.Decrement(ref _queuedCount);
                _remoteConfig?.ApplyExperimentTags(evt.Properties);
                (lines ?? (lines = new List<string>())).Add(SerializeEvent(evt));
                EventPool.Return(evt);
            }

            return lines;
        }

        private IEnumerator SendLiveRoutine()
        {
            _batch.Clear();
            while (_batch.Count < _config.MaxBatchSize && _queue.TryDequeue(out var evt))
            {
                Interlocked.Decrement(ref _queuedCount);
                _batch.Add(evt);
            }

            if (_batch.Count == 0)
            {
                yield break;
            }

            _lines.Clear();
            for (var i = 0; i < _batch.Count; i++)
            {
                // Stamp current A/B membership onto each event just before it's
                // serialized, so funnels/metrics can be split by test group.
                _remoteConfig?.ApplyExperimentTags(_batch[i].Properties);
                _lines.Add(SerializeEvent(_batch[i]));
            }

            var body = Encode(BuildArrayBody(_lines));
            var batchCount = _batch.Count;
            var result = SendResult.Retryable;
            yield return PostBatchRoutine(body, r => result = r);

            if (result == SendResult.Success)
            {
                GameMetricLog.Info("Delivered " + batchCount + " event(s).");
            }
            else if (result == SendResult.Retryable)
            {
                // AppendAsync snapshots _lines, so returning the batch to the pool
                // and reusing _lines afterwards is safe; we still await so a
                // following pass doesn't read the cache before this write lands.
                yield return AwaitTask(_store.AppendAsync(_lines));
                GameMetricLog.Info("Delivery failed; cached " + batchCount + " event(s) for retry.");
            }
            else
            {
                GameMetricLog.Warn("Server rejected " + batchCount + " event(s) (client error); dropped.");
            }

            NoteOutcome(result);

            for (var i = 0; i < _batch.Count; i++)
            {
                EventPool.Return(_batch[i]);
            }

            _batch.Clear();
        }

        private IEnumerator SendFromCacheRoutine(System.Action<SendResult> onResult)
        {
            var readTask = _store.ReadAsync(_config.MaxBatchSize);
            yield return AwaitTask(readTask);
            var lines = ResultOr(readTask, EmptyLines);
            if (lines.Count == 0)
            {
                onResult(SendResult.Success);
                yield break;
            }

            var body = Encode(BuildArrayBody(lines));
            var result = SendResult.Retryable;
            yield return PostBatchRoutine(body, r => result = r);

            if (result == SendResult.Success)
            {
                yield return AwaitTask(_store.RemoveFirstAsync(lines.Count));
                GameMetricLog.Info("Re-sent " + lines.Count + " cached event(s).");
            }
            else if (result == SendResult.Permanent)
            {
                yield return AwaitTask(_store.RemoveFirstAsync(lines.Count));
                GameMetricLog.Warn("Server rejected " + lines.Count + " cached event(s) (client error); dropped.");
            }
            // Retryable leaves the cache intact for a later flush.

            NoteOutcome(result);
            onResult(result);
        }

        /// <summary>Updates the exponential-backoff window: grow it on a retryable failure, clear it on success/permanent.</summary>
        private void NoteOutcome(SendResult result)
        {
            if (result == SendResult.Retryable)
            {
                _consecutiveFailures++;
                var delay = Mathf.Min(BackoffBaseSeconds * Mathf.Pow(2f, _consecutiveFailures - 1), BackoffMaxSeconds);
                _backoffUntilRealtime = Time.realtimeSinceStartup + delay;
                GameMetricLog.Info("Backing off " + delay.ToString("0") + "s after " + _consecutiveFailures + " failed attempt(s).");
            }
            else
            {
                _consecutiveFailures = 0;
                _backoffUntilRealtime = 0f;
            }
        }

        private IEnumerator PostBatchRoutine(byte[] body, System.Action<SendResult> onResult)
        {
            using (var www = new UnityWebRequest(_config.EndpointUrl, UnityWebRequest.kHttpVerbPOST))
            {
                www.uploadHandler = new UploadHandlerRaw(body);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.timeout = _config.RequestTimeoutSeconds;
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("X-API-Key", _config.ApiKey);
                www.SetRequestHeader("Accept-Encoding", "gzip");
                if (_config.CompressRequests)
                {
                    www.SetRequestHeader("Content-Encoding", "gzip");
                }

                yield return www.SendWebRequest();
                onResult(Classify(www));
            }
        }

        private static SendResult Classify(UnityWebRequest www)
        {
            var code = www.responseCode;

            if (www.result == UnityWebRequest.Result.Success && code >= 200 && code < 300)
            {
                return SendResult.Success;
            }

            // 4xx other than 429 are client errors that won't fix themselves on retry.
            if (code >= 400 && code < 500 && code != 429)
            {
                return SendResult.Permanent;
            }

            // Network failure (code 0), 5xx, or 429 (rate limited) — retry later.
            return SendResult.Retryable;
        }

        private string SerializeEvent(GameMetricEvent evt)
        {
            _eventBuilder.Length = 0;
            JsonWriter.WriteEvent(_eventBuilder, evt);
            return _eventBuilder.ToString();
        }

        private string BuildArrayBody(List<string> eventJsonLines)
        {
            _bodyBuilder.Length = 0;
            _bodyBuilder.Append('[');
            for (var i = 0; i < eventJsonLines.Count; i++)
            {
                if (i > 0)
                {
                    _bodyBuilder.Append(',');
                }

                _bodyBuilder.Append(eventJsonLines[i]);
            }

            _bodyBuilder.Append(']');
            return _bodyBuilder.ToString();
        }

        private byte[] Encode(string json)
        {
            var raw = Encoding.UTF8.GetBytes(json);
            if (!_config.CompressRequests)
            {
                return raw;
            }

            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    gz.Write(raw, 0, raw.Length);
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Drives a Task to completion from a coroutine by yielding a frame until
        /// it finishes. This is what keeps EventStore's disk I/O off the main
        /// thread: the work runs on a pool thread while the flush coroutine simply
        /// waits, so no frame is blocked on the filesystem.
        /// </summary>
        private static IEnumerator AwaitTask(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Reads a completed task's result, or returns <paramref name="fallback"/>
        /// if it faulted. EventStore swallows its own I/O errors and returns safe
        /// defaults, so a fault here is not expected — but we degrade gracefully
        /// rather than throw into the coroutine machinery.
        /// </summary>
        private static T ResultOr<T>(Task<T> task, T fallback)
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task.Result;
            }

            if (task.IsFaulted)
            {
                GameMetricLog.Warn("Offline cache I/O failed: " + task.Exception?.GetBaseException().Message);
            }

            return fallback;
        }
    }
}