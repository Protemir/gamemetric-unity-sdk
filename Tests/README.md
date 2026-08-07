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
