# Delegation

Research orchestrator that fans a single request out to three peer
runtimes via `agent.delegate`, demultiplexes their event streams,
tolerates per-peer failure.

## Before ARCP

Each peer agent is reached over its own bespoke HTTP/SSE endpoint.
The orchestrator stands up three separate websockets, parses three
different event formats, and writes three retry loops. Trace context
is "added later" and never quite makes it across the seam.

## With ARCP

```fsharp
let traceId = TraceId.create ()

let! jobs =
    peers |> List.map (fun p -> delegate client p request traceId) |> Task.WhenAll

for j in jobs do
    match j.JobId with Some jid -> mux.Register jid | None -> ()

let! completed = jobs |> Array.map (collect mux) |> Task.WhenAll
```

One transport, one envelope shape, one trace. Per-peer failure is a
typed `job.failed` envelope, not a 502 with a stack trace.

## ARCP primitives

- `agent.delegate` + `trace_id` propagation — RFC §14, §17.1.
- Job lifecycle (accepted → terminal) — §10.2.
- Stream/event multiplexing across `JobId` — §6.4.

## File tour

- `Program.fs` — fan-out / gather / synthesize. `JobMux` is the
  interesting type: a single inbound reader fans events out by `JobId`.
- `Synth.fs` — final synthesis stub.

## Variations

- Bound the fan-out by capability (e.g. only peers advertising
  `arcpx.research.web.v1`).
- Return artifact refs from peers (`job.completed.result_ref`)
  instead of inline results when payloads cross the inline budget (§16).
- Cancel slowest peer once N succeed via `cancel`
  (see `Cancellation`).
