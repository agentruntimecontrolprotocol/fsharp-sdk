# CONFORMANCE.md — ARCP F# SDK v0.1

Status of every RFC section in the F# reference implementation. Updated at the end of each phase.

Legend: `Implemented` · `Partial` · `Deferred (target: vX.Y)` · `Out of scope` · `Skeleton` (Phase 0 only).

| RFC §                                  | Status        | Notes                                                                |
| -------------------------------------- | ------------- | -------------------------------------------------------------------- |
| §4 Design Principles                   | Skeleton      | Documented in PLAN.md; verified end of Phase 7.                      |
| §6.1 Envelope                          | Skeleton      | Phase 1.                                                             |
| §6.2 Message Types                     | Skeleton      | Phase 1–2; full DU populated by end of Phase 5.                      |
| §6.3 Command/Result/Event Flow         | Skeleton      | Phase 2 (handshake) → Phase 5.                                       |
| §6.4 Delivery Semantics                | Skeleton      | Phase 1 (event log) + Phase 5 (resume).                              |
| §6.5 Priority and QoS                  | Skeleton      | Phase 3.                                                             |
| §7 Capability Negotiation              | Skeleton      | Phase 2.                                                             |
| §8 Authentication & Identity           | Skeleton      | Phase 2 (`bearer`, `signed_jwt`, `none` only).                       |
| §8.2 `mtls` / `oauth2`                 | Out of scope  | v0.1: not implemented.                                               |
| §9 Sessions (stateless/stateful)       | Skeleton      | Phase 2.                                                             |
| §9 Durable Sessions                    | Deferred      | v0.2.                                                                |
| §10 Jobs                               | Skeleton      | Phase 3.                                                             |
| §10.6 Scheduled Jobs                   | Out of scope  | v0.1: `nack UNIMPLEMENTED`.                                          |
| §11 Streaming                          | Skeleton      | Phase 3.                                                             |
| §11.3 Sidecar Binary Frames            | Out of scope  | v0.1: base64 only.                                                   |
| §12 Human-in-the-Loop                  | Skeleton      | Phase 4.                                                             |
| §12.3 Quorum Policies                  | Out of scope  | v0.1: first-response-wins only.                                      |
| §13 Subscriptions                      | Skeleton      | Phase 5.                                                             |
| §14 Multi-Agent Coordination           | Out of scope  | v0.1: not implemented.                                               |
| §15 Permissions & Leases               | Skeleton      | Phase 4.                                                             |
| §15.6 Trust Elevation                  | Out of scope  | v0.1: not implemented.                                               |
| §16 Artifacts                          | Skeleton      | Phase 5; inline base64 only.                                         |
| §17 Observability                      | Skeleton      | Phase 1 (envelope fields) + Phase 3 (telemetry messages).            |
| §18 Error Model                        | Skeleton      | Phase 1.                                                             |
| §19 Resumability                       | Skeleton      | Phase 5; message-id replay only.                                     |
| §19 Checkpoint Resume                  | Deferred      | v0.2.                                                                |
| §20 MCP Compatibility                  | Out of scope  | Documentation only.                                                  |
| §21 Extensions                         | Skeleton      | Phase 1.                                                             |
| §22 Reference Transports               | Skeleton      | Phase 6: WebSocket + stdio mandatory; HTTP/2 + QUIC out of scope.    |
