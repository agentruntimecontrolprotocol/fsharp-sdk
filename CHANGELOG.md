# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (breaking)

- `ToolContext.RequestHumanInputAsync` and `RequestChoiceAsync` (and the
  corresponding `Runtime` methods) now return
  `Task<Result<_, ARCPError>>`. Deadline expiry surfaces as
  `Error (DeadlineExceeded _)` instead of raising `TimeoutException`,
  bringing these affordances in line with the rest of the protocol's
  typed-error contract (RFC §18).

### Changed

- `Runtime.stampAndLog` is now async (`Task<Envelope<MessageType>>`) and
  is `do!`-awaited by `sendEnvelope`; the prior
  `eventLog.AppendAsync(...).GetAwaiter().GetResult()` sync-over-async
  call has been removed.
- Outbound and inbound send/log paths classify swallowed exceptions:
  `OperationCanceledException` propagates silently, everything else is
  logged at warning level with the relevant `MessageId`.
- The runtime's main dispatch fallthrough now logs the unhandled
  message type before replying `UNIMPLEMENTED`, so new wire types are
  visible in operator logs rather than silently nack'd.
- Internal `subscriptionManager: obj = null` field replaced by a typed
  `SubscriptionManager option` — no more downcast-driven dispatch.
- `let ... = ref ...` cells in the runtime modernized to `let mutable`.
- `Directory.Build.props` no longer suppresses `FS3261`, `FS3262`,
  `FS3265`; nullness across `Json.fs`, `Extensions.fs`,
  `Store/EventLog.fs`, and `Messages/Registry.fs` has been made
  explicit.

## [0.1.0] - 2026-05-10

### Added

- Initial reference SDK release aligned with ARCP protocol v1.0 (see README status).

