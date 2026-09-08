# Security policy

## Reporting a vulnerability

Do not open a public issue for a security problem. Report it privately through
[GitHub Security Advisories](https://github.com/Protemir/gamemetric-unity-sdk/security/advisories/new),
which keeps the report and the discussion private until a fix ships.

Expect an acknowledgement within a few working days. This is a small project —
if a report goes unanswered for a week, please chase it rather than assume it
was received.

## Supported versions

Fixes land on the newest minor release. Older tags are not patched; upgrading
is the supported path.

## What is in scope

This package runs inside a game and sends telemetry to the GameMetric backend.
Reports that concern the SDK itself are in scope, for example:

- The API key or any collected data being exposed to another app on the device.
- Crash payloads or event properties leaking data the game never intended to
  send.
- A malformed server response causing memory or execution problems in the SDK.
- The offline event store being readable or writable by another application.

## What is not

- Anything requiring the attacker to already control the device or the game
  binary — an attacker who can modify the game can also read its API key, and
  the SDK cannot defend against that.
- The API key being visible in a shipped client build. Ingestion keys are
  write-only by design and are expected to live in client binaries; report key
  *misuse* to the backend instead.
- Denial of service against the developer's own game by flooding their own SDK.

## A note on collected data

Crash reports include stack traces and any properties the integrating game
attaches to events. Integrators are responsible for what they attach; the SDK
does not collect device identifiers beyond what is documented in the README.
