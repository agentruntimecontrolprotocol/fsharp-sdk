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

- Initial reference SDK release aligned with ARCP protocol v1.0.
- Full session handshake with capability negotiation (RFC §8, §9).
- Bearer and signed_jwt (HS256) authentication validators (RFC §8.2).
- Job lifecycle: `tool.invoke`, `job.accepted/started/progress/completed/failed/cancelled` (RFC §10).
- Heartbeats and missed-deadline reaper for externally-managed jobs (RFC §10.3).
- Streams: ordered chunks with sequence-gap detection (RFC §11).
- Soft cancellation, hard cancellation with deadline, and interrupt (RFC §12).
- Subscriptions with backfill, type/session/job/stream/trace/priority filtering, and per-subscription drain pump with backpressure overflow (RFC §13).
- Human input and choice with schema validation and default-on-expiry (RFC §14).
- Permission challenges with lease grant/extend/revoke and a deterministic sweeper (RFC §15).
- Artifact PUT/REF/FETCH/RELEASE with sha256 verification and retention sweeper (RFC §16).
- Trace context propagation (RFC §17).
- Resume via message-id cursor against the SQLite event log (RFC §19).
- Transports: in-memory paired channels, NDJSON stdio, and WebSocket (RFC §22).
- `arcp` CLI with `serve --stdio`, `serve --ws`, `tail`, `send`, and `replay` subcommands, packaged as a .NET global tool (`ARCP.FSharp.Cli`).
- Six runnable samples (`samples/01.MinimalSession` … `samples/06.RelayHumanInTheLoop`).
- README, CONFORMANCE table, and PLAN documents.

### Fixed

- Subscription delivery: the bounded per-subscription channel was buffered but never drained onto the wire. Added a per-subscription pump that streams `subscribe.event` envelopes back to the observer.

