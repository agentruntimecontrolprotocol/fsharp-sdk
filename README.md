# ARCP F# SDK

F# reference implementation of the [Agent Runtime Control Protocol (ARCP) v1.0](./RFC-0001-v2.md).

> **Status:** v0.1 — Phase 0 skeleton.
> The protocol surface is being built out in hard-gated phases. See [`PLAN.md`](./PLAN.md) for the build plan and [`CONFORMANCE.md`](./CONFORMANCE.md) for per-section status.

## Quickstart

Requires .NET 10 SDK.

```bash
dotnet tool restore
dotnet build -c Release --warnaserror
dotnet test  -c Release
dotnet run   --project samples/01.MinimalSession
```

## Layout

- `src/ARCP/` — the SDK library.
- `src/ARCP.Cli/` — `arcp` command-line tool.
- `samples/` — runnable end-to-end demonstrations.
- `tests/` — unit and integration test projects.

## Why F#

Discriminated unions plus exhaustive pattern matching give compile-time dispatch over the entire protocol envelope: adding a new message type to `ARCP.Messages.Registry.MessageType` forces every match site to handle it, or the build breaks. Records and immutability give correct-by-construction state machines for sessions, jobs, streams, subscriptions, and leases. `task { ... }` keeps interop with .NET clean. The F# file-order rule keeps dependencies honest. See [`PLAN.md`](./PLAN.md) for the full design rationale.
