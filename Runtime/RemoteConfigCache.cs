using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace GameMetricSDK
{
    /// <summary>
    /// Persists the last successful remote-config response to disk so the very
    /// first frame after launch reads the previous session's values instead of
    /// falling back to hard-coded defaults (the cold-start problem).
    ///
    /// The payload is a single small JSON blob, so it's loaded synchronously once
    /// at Initialize (cheap, one read) and written on a pool thread after each
    /// successful fetch via an atomic temp-file replace (a crash mid-write can't
    /// corrupt the cache). All access is guarded so a background write and the
    /// startup read can't tear.
    /// </summary>
    internal sealed class RemoteConfigCache
    {
        private readonly string _directory;
        private readonly string _filePath;
        private readonly string _tempFilePath;
        private readonly object _lock = new object();

        public RemoteConfigCache()
        {
            _directory = Path.Combine(Application.persistentDataPath, "gamemetric");
            _filePath = Path.Combine(_directory, "remote_config.json");
            _tempFilePath = _filePath + ".tmp";
        }

        /// <summary>Synchronously reads the cached payload, or null if none/unreadable. Called once at Initialize.</summary>
        public string LoadOrNull()
        {
            lock (_lock)
            {
                try
                {
                    return File.Exists(_filePath) ? File.ReadAllText(_filePath) : null;
                }
                catch (Exception ex)
                {
                    GameMetricLog.Warn("Failed to read remote-config cache: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Persists <paramref name="json"/> on a pool thread (fire-and-forget). The
        /// string is immutable, so handing it to another thread is safe; the write
        /// is rare (only on a changed fetch) and never touches the main thread.
        /// </summary>
        public void Save(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        Directory.CreateDirectory(_directory);
                        File.WriteAllText(_tempFilePath, json);
                        ReplaceFile(_tempFilePath, _filePath);
                    }
                    catch (Exception ex)
                    {
                        GameMetricLog.Warn("Failed to write remote-config cache: " + ex.Message);
                        TryDelete(_tempFilePath);
                    }
                }
            });
        }

        /// <summary>Atomically replaces <paramref name="target"/> with <paramref name="temp"/>, with a best-effort fallback for filesystems that don't support File.Replace.</summary>
        private static void ReplaceFile(string temp, string target)
        {
            try
            {
                if (File.Exists(target))
                {
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
                // Fall through to delete-then-move.
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            if (File.Exists(temp))
            {
                File.Move(temp, target);
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
                // Best effort — a leftover temp file is harmless.
            }
        }
    }
}
