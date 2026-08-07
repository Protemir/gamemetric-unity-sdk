using System.Runtime.CompilerServices;

// Grant the EditMode test assembly access to internal types so tests can target
// the real ErrorAggregator, RemoteConfigStore, EventStore, JsonReader/Writer,
// EventPool, and GameMetricEvent directly.
[assembly: InternalsVisibleTo("GameMetricSDK.Tests")]
