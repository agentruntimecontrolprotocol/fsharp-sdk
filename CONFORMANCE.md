# ARCP F# SDK — RFC Conformance (v0.1.0)

Status legend: Implemented / Partial / Deferred to v0.2 / N/A.

| § | Title | Status | Notes |
| --- | --- | --- | --- |
| 1 | Introduction | N/A | Non-normative. |
| 2 | Terminology | N/A | Non-normative. |
| 3 | Roles & deployment models | Implemented | Co-process (stdio/ws) and same-process (Memory). |
| 4 | Conformance levels | Implemented | Targets "Core+" — sessions, jobs, streams, subscriptions, leases, artifacts, resume. |
| 5 | Message-set overview | Implemented | `Messages/Registry.fs` is the closed DU; all message types compile-checked. |
| 6 | Envelope schema | Implemented | `Envelope.fs`; ULIDs for ids; FSharp.SystemTextJson serialisation. |
| 6.4 | Idempotency | Implemented | `EventLog.AppendAsync` uses `INSERT OR IGNORE` on `(session_id, message_id)`. |
| 7 | Error model | Implemented | `Errors.fs`; `ARCPError.code/message/retryable`. |
| 8 | Capability negotiation | Implemented | `negotiate` in `Runtime/Session.fs`; missing-required → UNIMPLEMENTED. |
| 8.2 | Auth schemes | Partial | `bearer`, `signed_jwt` (HS256), `none`. mTLS and OAuth2 deferred to v0.2. |
| 9 | Session handshake | Implemented | `session.open/accepted/rejected/unauthenticated` end-to-end. |
| 9.x | Session challenge | Deferred to v0.2 | The challenge-then-authenticate flow currently rejects with FAILED_PRECONDITION. |
| 10 | Jobs | Implemented | `JobManager` drives `tool.invoke → job.accepted/started/progress/completed/failed`. |
| 10.3 | Heartbeats | Implemented | Per-job heartbeat channel + missed-deadline reaper. |
| 10.6 | Scheduled jobs | Deferred to v0.2 | Not yet emitted. |
| 11 | Streams | Implemented | `StreamManager` + `StreamReader/Writer`; ordered chunks; out-of-order surfaces as exception. |
| 11.3 | Stream sidecars | Deferred to v0.2 | No external sidecar binary. |
| 12 | Cancellation | Implemented | Soft cancel via ct, hard cancel via deadline. |
| 12.x | Interrupt | Implemented | `InterruptAsync` transitions a job to `Blocked`. |
| 12.3 | Multi-channel HITL relay | Implemented | `samples/06.RelayHumanInTheLoop` demonstrates fan-out + winner-takes-all. |
| 13 | Subscriptions | Implemented | `SubscriptionManager`; bounded per-sub channel + drain pump; filter on session/trace/job/stream/type/priority. |
| 13.2 | Backfill | Implemented | Replays from `EventLog`; synthetic `subscription.backfill_complete`. |
| 13.3 | Unsubscribe / close | Implemented | Both explicit unsubscribe and BACKPRESSURE_OVERFLOW. |
| 14 | Human input | Implemented | `human.input.request/response/cancelled`, with default fallback on expiry. |
| 14.2 | Human choice | Implemented | `human.choice.request/response`. |
| 14.x | Agent delegation / handoff | Deferred to v0.2 | Multi-agent handoff not implemented. |
| 15 | Permissions | Implemented | `permission.request/grant/deny` end-to-end. |
| 15.4 | Permission challenges | Implemented | Runtime issues the challenge; client handler decides. |
| 15.5 | Leases | Implemented | `LeaseManager` issues, extends, revokes; sweeper drives expiry. |
| 15.6 | Trust elevation | Deferred to v0.2 | Not implemented. |
| 16 | Artifacts | Implemented | `ArtifactStore` with inline base64 PUT/FETCH/RELEASE; sha256 validation; retention sweeper. |
| 17 | Trace context | Implemented | `TraceContext` carried on envelopes; W3C-compatible. |
| 18 | Telemetry | Partial | Envelope types exist (`Messages/Telemetry.fs`); no runtime aggregator. |
| 19 | Resume | Implemented | Message-id cursor against `EventLog`. Checkpoint-id resume deferred to v0.2. |
| 20 | Idempotency tokens | Implemented | `IdempotencyKey` on envelope; replays return cached response. |
| 21 | Versioning | Implemented | `Version.Protocol = "1.0"`. Runtime advertises kind/version. |
| 22 | Transports | Implemented | `Memory`, `Stdio` (NDJSON), `WebSocket` (one text frame = one envelope). |
| 22.x | HTTP/2 + QUIC | Deferred to v0.2 | Not implemented. |
| 23 | Authentication & TLS | Partial | Bearer + JWT only; TLS termination delegated to host. mTLS deferred to v0.2. |
| 24 | Security considerations | Implemented | Per-principal subscription authorisation; lease revocation on principal session close. |
| 25 | Examples | Implemented | The six `samples/` projects each map to one §. |
| 26 | IANA media type | N/A | Registration is out of scope for this SDK. |
| 27 | Compatibility | Implemented | Capability set is `Capabilities.empty` plus opt-ins; unknown capabilities fail closed with UNIMPLEMENTED. |
| 28 | References | N/A | Non-normative. |

## Deferred to v0.2

- §8.2 mTLS + OAuth2 auth schemes.
- §10.6 scheduled jobs.
- §11.3 stream sidecar binary.
- §14.x multi-agent delegation / handoff.
- §15.6 trust elevation.
- §19 checkpoint-id resume (message-id resume only in v0.1).
- §22 HTTP/2 + QUIC transports.

Source modules carry RFC section citations in their doc comments (e.g. `RFC §13.1`); use them as cross-references when reading the spec alongside the implementation.
