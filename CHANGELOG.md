# Changelog

All notable changes to the GameMetric Unity SDK are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-08-08

### Added
- **Native crash handoff (managed pipeline).** A platform crash handler writes a
  JSON crash record to `persistentDataPath/gamemetric/native-crashes/`; on the next
  launch the SDK parses each record, emits it as a fatal `error` event
  (`is_native: true`, back-dated to the crash time) through the normal delivery
  pipeline, then deletes the file. Defines the platform-agnostic on-disk contract
  and the pickup/delivery side. The platform native writers (iOS/Android/il2cpp)
  and address symbolication are upcoming phases.

## [1.1.5] - 2026-08-07

### Added
- Crash capture now hooks `Application.logMessageReceivedThreaded`, so exceptions
  thrown on **background threads and the Job System** are captured too — previously
  only main-thread errors were seen.

### Changed
- Hardened the capture path for off-main-thread use: a `[ThreadStatic]`
  re-entrancy guard and a monotonic `Stopwatch`-based clock for the aggregation
  window (replacing the main-thread-only `Time.realtimeSinceStartup`).

## [1.1.4] - 2026-08-07

### Changed
- On **WebGL** the offline cache is now in-memory instead of writing to
  `Application.persistentDataPath` — a browser virtual filesystem that isn't
  persisted across reloads without `FS.syncfs`, on a single-threaded runtime where
  the disk path's background I/O can't run. In-session retry still works;
  un-sent events don't survive a page reload (documented). The remote-config disk
  cache is likewise skipped on WebGL (config re-fetches on load).

## [1.1.3] - 2026-08-07

### Added
- Event `properties` may now contain **nested dictionaries and lists**, serialized
  as real JSON objects/arrays (depth-bounded against cyclic references).

### Changed
- `double`/`float` properties serialize with `G17`/`G9` for an **exact numeric
  round-trip**, replacing the historically unreliable `R` format.

## [1.1.2] - 2026-08-07

### Changed
- The retry backoff now applies **±20% jitter** to each delay, so a fleet of
  clients that failed together (e.g. during a backend outage) don't all retry in
  lockstep and stampede the server on recovery.

## [1.1.1] - 2026-08-07

### Fixed
- A manual `Flush()`, the startup flush, and the pause-triggered flush now
  **respect the exponential-backoff cooldown** instead of bypassing it, so they
  can't hammer a server the SDK has already backed off from. Queued events stay
  cached and deliver on the next eligible cycle. Captured crashes still flush with
  priority.

## [1.1.0] - 2026-08-07

### Added
- **Runtime consent / opt-out API**: `GameMetric.SetCollectionEnabled(bool)` and
  `IsCollectionEnabled`. The choice is persisted; while disabled the SDK collects
  and sends nothing (events, crashes, sessions, remote-config), and opting out
  purges data not yet delivered. Works before or after `Initialize()`.

## [1.0.0] - 2026-08-07

Initial public release.

### Added
- **Event logging** — non-blocking `LogEvent` (pooled event objects + a lock-free
  queue), plus `LogMonetization` and `LogProgression` helpers, and manual
  `LogError`.
- **Sessions** — automatic `session_start` / `session_end` with duration and a
  configurable background timeout that opens a new session on long resumes.
- **Offline-first delivery** — durable NDJSON cache with atomic, off-main-thread
  I/O; batching, exponential backoff, per-request timeouts, and optional gzip; a
  client-generated `event_id` for idempotent, dedup-safe resends.
- **Remote Config** — typed `GetConfig<T>` / `TryGetConfig<T>`, an on-disk cache
  loaded synchronously at startup (no cold-start fallbacks), background
  auto-refresh, and an `OnConfigUpdated` event.
- **A/B testing** — `GetVariant` / `TryGetVariant` with deterministic, sticky,
  server-side assignment; every event auto-tagged with the player's variant.
- **Crash & error analytics** — automatic capture of uncaught exceptions with
  signature-based deduplication and priority delivery.
- **Editor integration** — a Project Settings panel for the API key plus a
  "Send Test Event" button.
- **Zero dependencies**, Unity 2020.3+, and a `GAMEMETRIC_DISABLED` switch to
  compile the SDK out completely.
- **EditMode test suite** (Unity Test Framework / NUnit) covering the core logic.

[1.2.0]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.5...v1.2.0
[1.1.5]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/Protemir/gamemetric-unity-sdk/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Protemir/gamemetric-unity-sdk/releases/tag/v1.0.0
