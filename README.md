# GameMetric Unity SDK

The official Unity SDK for [**GameMetric**](https://gamemetric.dev) — a LiveOps &
analytics platform for games. Non-blocking event logging, automatic crash
reporting, Remote Config, and A/B testing, built **offline-first** with a strict
focus on **zero frame-time impact**.

- 🚀 **Non-blocking** — logging never touches the network or does JSON work on your frame.
- 📦 **Offline-first** — events are cached durably and re-sent when connectivity returns.
- 🎛️ **Remote Config & A/B testing** — change balance and run experiments without shipping a build.
- 💥 **Crash & error reporting** — uncaught exceptions captured automatically, deduplicated.
- 🔌 **Zero-dependency** — pure C#, no third-party packages.

---

## Requirements

- Unity **2020.3** or newer.
- A GameMetric account and project API key (see below).

## Installation

Install via the Unity **Package Manager** → **Add package from git URL…**:

```
https://github.com/Protemir/gamemetric-unity-sdk.git
```

Or add it to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "dev.gamemetric.sdk": "https://github.com/Protemir/gamemetric-unity-sdk.git"
  }
}
```

---

## Prerequisites / Getting an API Key

1. Sign up at **[https://gamemetric.dev](https://gamemetric.dev)**.
2. Create a **project** for your game from the dashboard.
3. Open **Settings → Project Settings** and copy your project's **API Key**.
4. Provide it to the SDK, either:
   - **In the Editor** — open **Edit → Project Settings → GameMetric**, paste the
     API Key (and optionally a Game Version), then call `GameMetric.Initialize()`
     with no arguments; or
   - **In code** — pass it directly:

     ```csharp
     GameMetric.Initialize("YOUR_API_KEY");
     ```

> The API key identifies your project during ingestion. It is safe to ship in a
> client build (it only permits sending events for that one project).

---

## Quick Start

```csharp
using System.Collections.Generic;
using GameMetricSDK;

public class Bootstrap : MonoBehaviour
{
    void Start()
    {
        // 1. Start a session (new SessionId, device metadata, background delivery).
        GameMetric.Initialize("YOUR_API_KEY");

        // 2. Log a gameplay event with arbitrary properties.
        GameMetric.LogEvent("level_complete", new Dictionary<string, object>
        {
            { "level", 7 },
            { "duration_seconds", 42.5 },
            { "used_powerup", true },
        });

        // 3. Log an in-app purchase (revenue analytics).
        GameMetric.LogMonetization("com.game.gems_pack", 4.99, "USD");

        // 4. Log progression through your game.
        GameMetric.LogProgression(ProgressionStatus.Complete, "world_1.level_3");

        // 5. Read remote config (cached on disk, so the first frame gets real values).
        int   startingGold = GameMetric.GetConfig<int>("starting_gold", 100);
        float spawnRate    = GameMetric.GetConfig<float>("spawn_rate", 1.0f);

        // 6. React live when config changes during a session.
        GameMetric.OnConfigUpdated += () =>
            spawnRate = GameMetric.GetConfig<float>("spawn_rate", 1.0f);

        // 7. Branch on the player's A/B variant (sticky per user, resolved server-side).
        if (GameMetric.GetVariant("price_test") == "variant_a")
            ShowDiscountedStore();
    }
}
```

Manually report a handled exception:

```csharp
try { Risky(); }
catch (System.Exception e) { GameMetric.LogError(e.Message, e.StackTrace); }
```

Identify a known user (persists across sessions):

```csharp
GameMetric.SetUserId("account-12345");
```

---

## Under the Hood / Architecture

A deeper look for tech leads evaluating the SDK.

### Zero frame-time impact

`LogEvent` is designed to do **no work that scales with your frame**. On the hot
path it:

1. **Rents** a reusable event object from a bounded, thread-safe **object pool**
   (`EventPool`), so steady-state logging allocates nothing for the event itself.
2. Copies the caller's fields, and **enqueues** onto a **lock-free**
   `ConcurrentQueue`.

No network, no JSON serialization, and no locks happen inline. All heavy work is
deferred to a hidden `DontDestroyOnLoad` runner and, where possible, pushed off the
main thread onto the **.NET thread pool**.

### Offline-first with atomic durability

Delivery failures never lose data:

- **`EventStore`** is a durable NDJSON offline cache under
  `Application.persistentDataPath`. All file I/O (append / read / trim) runs on a
  **pool thread** via `Task.Run`, and the flush coroutine awaits it without blocking
  a frame. Reads **stream** (they stop after the requested count instead of loading
  the whole file), and rewrites go through a **temp file + atomic replace**
  (`File.Replace`), so a crash mid-write can never corrupt the cache. An in-memory
  line count keeps the "is there anything to send?" check off the disk entirely and
  only triggers a trim when the cap is actually exceeded.
- **`RemoteConfigCache`** persists the last good config the same way (atomic
  temp-file replace on a pool thread) and is loaded **synchronously at
  `Initialize`**, so the very first frame reads real values instead of fallbacks —
  the classic cold-start problem is solved, with a fresh network fetch layered on
  top (stale-while-revalidate).

On pause/quit the queue is flushed to the cache **synchronously**, so nothing is
lost when the app is backgrounded or closed.

### Efficient, resilient networking

- **Batching.** Events are sent as a JSON array, up to **256** per request, on a
  **10 s** timer or as soon as **50** are queued — bounded, retry-friendly chunks.
- **Idempotency.** Every event carries a client-generated `event_id`, fixed at log
  time and preserved across every retry, so a batch that was delivered but whose
  response was lost is **deduplicated** server-side instead of inflating counts.
- **Exponential backoff.** After a retryable failure (network error, `5xx`, `429`)
  the flush cadence backs off `2s → 4s → 8s → 16s → 32s → 60s` (capped) and resets
  on the next success. Non-retryable `4xx` is dropped with a warning. A hung socket
  is bounded by a per-request timeout, and a captured crash triggers a **priority
  flush** on the next frame (bypassing the timer) so it ships before a possible
  termination.
- **Optional gzip** compression of the request body.

### Sessions

A session starts at `Initialize`. After the app is backgrounded past a timeout
(default **30 min**), the next resume closes the old session with a back-dated
`session_end` (carrying `duration_seconds`) and opens a fresh one — so session
counts and durations stay accurate instead of one id spanning the whole process.

### Crash & error analytics

Uncaught exceptions and `Debug.LogError` / `Debug.Assert` are captured
automatically as `error` events — including errors thrown on **background threads
and the Job System** (capture hooks `logMessageReceivedThreaded`). Identical errors
(same stack) are **deduplicated** by a stable signature hash and counted, so a
per-frame crash loop becomes a couple of counted events instead of hundreds — the
delivery pipeline is never flooded.

**Native crashes** (iOS signal / Android SIGSEGV / il2cpp hard-crash) kill the
process, so they can't be sent in the moment. A platform crash handler writes a
small JSON record under `persistentDataPath/gamemetric/native-crashes/`, and on the
next launch the SDK emits each as a fatal `error` event (`is_native: true`,
back-dated to the crash time) through the normal pipeline, then deletes it. The
managed pickup + delivery is in place; the platform native writers and address
symbolication are upcoming.

### Compiling the SDK out

Define `GAMEMETRIC_DISABLED` (Project Settings → Player → Scripting Define Symbols)
to compile the SDK out entirely: every call becomes a zero-overhead no-op and no
background work starts.

---

## Privacy & Consent

Data collection is **on by default**; gate it at runtime from your own consent flow:

```csharp
// e.g. after the player declines analytics in your consent dialog
GameMetric.SetCollectionEnabled(false);
```

While disabled, no events (including crashes and sessions) are collected or sent
and no remote-config fetch runs — and opting out **purges data not yet delivered**
(the in-memory queue and the offline cache). The choice is persisted and honored
on the next launch, and `SetCollectionEnabled` works before or after
`Initialize()`. `IsCollectionEnabled` reports the current state. For a
build-time kill switch, define `GAMEMETRIC_DISABLED`.

---

## API Reference

| Member | Description |
|---|---|
| `Initialize()` / `Initialize(apiKey)` / `Initialize(apiKey, gameVersion, enableDebugLogs, baseUrl)` | Start a session. The parameterless overload reads **Project Settings → GameMetric**. |
| `LogEvent(name, parameters = null)` | Queue a custom event. Non-blocking; `parameters` become the event's JSON `properties` (primitives or nested dictionaries/lists). |
| `LogMonetization(productId, amount, currency, extra = null)` | Log an in-app purchase (`amount` powers revenue metrics). |
| `LogProgression(status, progression, details = null, extra = null)` | Log a `Start` / `Complete` / `Fail` progression step. |
| `LogError(message, stackTrace = null, extra = null)` | Manually report a handled exception (uncaught ones are captured automatically). |
| `GetConfig<T>(key, fallback = default)` | Typed remote-config read (`string` / `bool` / `int` / `long` / `float` / `double`). |
| `TryGetConfig<T>(key, out value)` | Typed read that reports presence — distinguishes a real value from a fallback. |
| `GetConfigString/Int/Double/Bool(key, fallback)` | Convenience typed getters. |
| `GetVariant(experimentName, fallback = null)` | The player's assigned A/B variant, to branch game logic. |
| `TryGetVariant(experimentName, out variant)` | Variant lookup that reports enrollment. |
| `OnConfigUpdated` (event) | Raised on the main thread when a fetch changes config values. |
| `FetchRemoteConfigAsync()` | Force a remote-config refresh (usually unnecessary — auto-fetch is on by default). |
| `SetUserId(userId)` | Override the anonymous install id with your own stable id (persisted). |
| `SetCollectionEnabled(enabled)` | Consent / opt-out switch (persisted). Disabling stops all collection and purges undelivered data. |
| `Flush()` | Request an immediate delivery attempt. |
| `IsInitialized` / `SessionId` / `UserId` / `IsCollectionEnabled` | Current SDK state. |

---

## Release Notes

### v1.2.0

- **Native crash handoff (managed pipeline)** — the SDK now reads native crash
  records left on disk by a platform handler (under
  `persistentDataPath/gamemetric/native-crashes/`) and, on the next launch, emits
  each as a fatal `error` event (`is_native: true`, back-dated to the crash time)
  through the normal delivery pipeline, then deletes the file. This ships the
  platform-agnostic contract + pickup; the platform native writers (iOS/Android/
  il2cpp) and symbolication follow.

### v1.1.5

- **Background-thread crash capture** — error/exception capture now hooks
  `logMessageReceivedThreaded`, so exceptions thrown on background threads or the
  Job System are captured too (previously only the main thread). The capture path
  is thread-safe: a per-thread re-entrancy guard and a monotonic clock that avoids
  main-thread-only Unity APIs.

### v1.1.4

- **WebGL offline cache** — on WebGL the offline cache is now in-memory instead of
  writing to `Application.persistentDataPath` (a browser virtual filesystem that
  isn't persisted across reloads without `FS.syncfs`, on a single-threaded runtime
  where the disk path's background I/O can't run). In-session retry works; un-sent
  events don't survive a page reload — the honest behavior for the web. The
  remote-config disk cache is likewise skipped on WebGL (config re-fetches on load).

### v1.1.3

- **Nested event properties** — property values can now be nested dictionaries and
  lists, serialized as real JSON objects/arrays (previously only primitives were
  supported; anything else fell back to a `ToString()` that produced garbage).
- **Exact numeric round-trip** — `double`/`float` properties now serialize with
  `G17`/`G9`, which round-trip exactly, instead of the historically unreliable `R`.

### v1.1.2

- **Backoff jitter** — the retry backoff now applies ±20% jitter to each delay, so
  a fleet of clients that failed together (e.g. during a backend outage) don't all
  retry in lockstep and hammer the server the moment it recovers.

### v1.1.1

- **Backoff fix** — a manual `Flush()`, the startup flush, and the pause-triggered
  flush now respect the exponential-backoff cooldown instead of bypassing it, so
  they can't hammer a server the SDK has already backed off from. Queued events
  stay cached and deliver on the next eligible cycle; captured crashes still flush
  with priority.

### v1.1.0

- **Privacy / consent API** — `SetCollectionEnabled(bool)` and `IsCollectionEnabled`
  let you opt players in or out of collection at runtime. The choice is persisted;
  while disabled the SDK collects and sends nothing (events, crashes, sessions,
  remote-config), and opting out purges data not yet delivered.

### v1.0.0

Initial public release.

- **Event logging** — non-blocking `LogEvent` with pooled event objects and a
  lock-free queue; `LogMonetization` and `LogProgression` helpers.
- **Sessions** — automatic `session_start` / `session_end` with duration and a
  configurable background timeout that opens a new session on long resumes.
- **Offline-first delivery** — durable NDJSON cache with atomic writes and
  off-main-thread I/O; batching, exponential backoff, per-request timeouts, and
  optional gzip; client-generated `event_id` for idempotent, dedup-safe resends.
- **Remote Config** — typed `GetConfig<T>` / `TryGetConfig<T>`, an on-disk cache
  loaded synchronously at startup (no cold-start fallbacks), background
  auto-refresh, and an `OnConfigUpdated` event for live reactions.
- **A/B testing** — `GetVariant` / `TryGetVariant` with deterministic, sticky,
  server-side assignment; every event auto-tagged with the player's variant.
- **Crash & error analytics** — automatic capture of uncaught exceptions with
  signature-based deduplication and priority delivery.
- **Editor integration** — a Project Settings panel for the API key plus a
  "Send Test Event" button to verify the setup end-to-end.
- **Zero dependencies**, Unity 2020.3+, and a `GAMEMETRIC_DISABLED` switch to
  compile the SDK out completely.

---

## Links

- Website & dashboard: **[https://gamemetric.dev](https://gamemetric.dev)**
- Demo scene: install **Demo Scene** from the package's *Samples* in the Package Manager.