# ARCP F# Samples

Fourteen single-purpose samples, each named for the protocol
primitive it demonstrates. Mirrors the canonical Python set under
`/python-sdk/examples/`.

> **Illustrative, not runnable.** Each sample imports from the
> in-repo `ARCP` library as if it were a published `ARCP.FSharp`
> package. Setup boilerplate (transport, identity, auth) is elided
> with `Unchecked.defaultof<_>` or `failwith "elided"`. LLM and
> framework calls live in tiny stub files (`Agents.fs`, `Steps.fs`,
> `Synth.fs`, …) so the protocol code in `Program.fs` is what you
> read.

## The fourteen

| Sample | Demonstrates | Spec |
|---|---|---|
| [`Subscriptions/`](./Subscriptions) | Three Observer clients on one session, three filters, three sinks. | §5, §13 |
| [`Leases/`](./Leases) | Lease-gated shell agent. Read leases coarse, write leases scoped. | §15.4–§15.5 |
| [`LeaseRevocation/`](./LeaseRevocation) | Per-table leases with `lease.revoked`/`lease.extended` mid-flight. | §15.5 |
| [`PermissionChallenge/`](./PermissionChallenge) | Two-party permission challenge — generator asks, reviewer holds veto. | §15.4, §6.4 |
| [`Delegation/`](./Delegation) | `agent.delegate` fan-out + `JobMux` to demux events by `JobId`. | §14, §6.4 |
| [`Handoff/`](./Handoff) | `agent.handoff` with transcript packed as artifact, runtime fingerprint pinned. | §14, §16, §8.3 |
| [`Heartbeats/`](./Heartbeats) | Worker federation; heartbeat-loss reroute via `IdempotencyKey`. | §10.3, §6.4 |
| [`CapabilityNegotiation/`](./CapabilityNegotiation) | Capability-driven peer routing; standard `cost.usd` rollups. | §7, §17.3.1, §18.3 |
| [`Resumability/`](./Resumability) | **Actually crash and resume.** `Environment.Exit 137` mid-flight; second invocation picks up at the next step. | §10, §19, §6.4 |
| [`ReasoningStreams/`](./ReasoningStreams) | `kind: thought` stream + a peer runtime that subscribes and delegates critiques back. | §11.4, §13, §14 |
| [`Extensions/`](./Extensions) | Custom `arcpx.sdr.*.v1` extension namespace with correct unknown-message handling. | §21 |
| [`HumanInput/`](./HumanInput) | `human.input.request` fanned across phone/email/Slack; first-wins resolution. | §12 |
| [`Cancellation/`](./Cancellation) | Cooperative `cancel` (terminate) vs `interrupt` (pause and ask). | §10.4–§10.5 |
| [`Mcp/`](./Mcp) | ARCP runtime fronting an MCP server: `tool.invoke` → MCP `call_tool`. | §20 |

## Conventions

- F# 8 / .NET 8, `task {}` workflows, `Async`/`TaskSeq` where
  iteration is the point.
- Each sample is one `Program.fs` (the protocol code) + 0–2 stub
  modules named for what they elide (`Agents.fs`, `Steps.fs`,
  `Synth.fs`, `Cheap.fs`, `Work.fs`, `Channels.fs`, `Sql.fs`,
  `Upstream.fs`).
- `Unchecked.defaultof<_>` for elided `Client` instances —
  transport, identity, and auth blocks are setup noise, not the point.
- Envelopes match RFC-0001 v2 exactly. Custom message types follow
  §21.1 `arcpx.<domain>.<name>.v<n>` naming.

## What's where in the SDK

- `ARCP.Client.Client` — handshake driver. `OpenAsync`, `InvokeAsync`,
  `SubscribeAsync`, `CancelAsync`, `ResumeAsync`, `PutArtifactAsync`.
- `ARCP.Envelope.Envelope<'P>` — wire envelope, generic over payload.
- `ARCP.Errors.ARCPError` — discriminated union of canonical errors.
- `ARCP.Ids.*.create / .ofString` — typed id constructors.
- `ARCP.Transport.WebSocket` — most common transport.
- `ARCP.Store.EventLog` — SQLite schema reused by `Subscriptions`.

## Reading order

For a brisk tour: `Subscriptions`, `Leases`, `Delegation`,
`Resumability` (this one actually crashes and recovers),
`Cancellation`, `Extensions`, `Mcp`. These seven exercise
the bulk of the protocol.

## Older skeletons

`01.MinimalSession` … `06.RelayHumanInTheLoop` are pre-existing
walkthrough skeletons predating the canonical fourteen. They remain
in the solution for now but are superseded by the named samples
above.
