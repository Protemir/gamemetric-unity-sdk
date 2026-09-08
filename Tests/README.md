# GameMetricSDK Tests

EditMode tests (Unity Test Framework / NUnit) for the SDK's core logic.

## Running

Open the project in Unity → **Window → General → Test Runner → EditMode → Run All**.
The `GameMetricSDK.Tests` assembly compiles only in a test context
(`UNITY_INCLUDE_TESTS`) and references `GameMetricSDK.Runtime`; internal types are
exposed to it via `InternalsVisibleTo` in `Runtime/AssemblyInfo.cs`.

## Coverage

| Suite | What it covers |
|---|---|
| `SessionTrackerTests` | Timeout boundary (new session vs same), back-dated end at pause time, quit-while-active vs paused, duration clamp |
| `ErrorAggregatorTests` | Dedup/throttle window, cumulative count, distinct-signature cap, `DrainPending` tail, stable signature hash |
| `RemoteConfigStoreTests` | Change detection, typed `TryGet<T>`, cross-type coercion, `GetVariant`/`TryGetVariant`, ab_ tag anti-spoofing, malformed-payload resilience |
| `JsonReaderTests` | Objects/arrays/primitives, escapes + `\u`, nesting, malformed inputs throw |
| `JsonWriterTests` | Event envelope + property types, string escaping, NaN/Infinity → `null` (P0 regression) |
| `EventPoolTests` | Pool reuse resets events; `GameMetricEvent.Reset` clears every field |
| `EventStoreTests` | Real file I/O in an isolated temp dir: append/read/remove, trim-to-newest, cold-start count, cache-first pipeline |

## Note

The logic suites (everything except `EventStoreTests`) are free of Unity
dependencies, so they can also be run headlessly in a plain .NET NUnit project
that links the same source + test files — handy for CI without a Unity license.
`EventStoreTests` needs the Unity runtime (`Application.temporaryCachePath`) and
runs only in the Test Runner.

## Not covered

Written down rather than left implicit, so a gap is a known gap and not a
pleasant assumption:

| Area | Why it is uncovered | What it would take |
|---|---|---|
| `EventDispatcher.Classify` and backoff progression | Needs `UnityWebRequest` and coroutine scheduling, neither of which exists in EditMode | PlayMode tests, or an interface over the transport so retry classification can be driven from a fake |
| `RemoteConfigCache` | Reads and writes through Unity's persistent data path | Same treatment as `EventStore` — an isolated temp dir and a Unity-side suite |
| Native crash writers (Android Java, iOS Obj-C) | The handoff is exercised only end to end, on a device build | A device or emulator run; the managed `NativeCrashRecord` parsing is covered, the native side that produces those records is not |
| Session lifecycle inside `GameMetricRunner` | `SessionTracker` is covered as pure logic, but the pause/resume/quit wiring that calls it is not | PlayMode tests driving application focus events |

The pattern in every row is the same: logic that was extracted into a
Unity-free type is tested, and whatever still touches the engine directly is
not. That is also the cheapest way to close each gap — pull the decision out of
the engine-facing class, not spin up more infrastructure around it.
