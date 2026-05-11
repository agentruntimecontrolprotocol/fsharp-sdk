# Subscriptions

One producing session, three Observer clients, three different sinks.
None of them ever issue a command.

## Before ARCP

Most teams sidecar the agent with a tee: agent emits to stdout, a
shipper tails the log, a second tail re-parses for metrics, a third
process writes to SQLite for replay. Three pipelines diverge over
time, none of them know about each other, and adding a fourth
consumer means another sidecar.

## With ARCP

```fsharp
let client : Client = Unchecked.defaultof<_>   // observer client
// await client.OpenAsync(...)                 // subscriptions: true, nothing else
match! client.SubscribeAsync(mkFilter target (Some [ "metric" ])) with
| Ok (sid, events) ->
    do! events |> TaskSeq.iterAsync sink.Handle
```

Three observers. One transport each. Filters declared inline. The
agent never knows they exist.

## ARCP primitives

- Subscriptions, filters, Observer role — RFC §13, §5.
- `since.after_message_id` backfill + the synthetic
  `subscription.backfill_complete` marker — §13.3.
- Standard metrics + trace spans — §17.
- Stream-kind filtering for `kind: thought` redaction — §11.4.

## File tour

- `Program.fs` — boots three subscribers in parallel via `Task.WhenAll`.
- `Sinks.fs` — `StdoutSink`, `SqliteSink`, `OtlpSink`. Each writes
  to its own backend; bodies are stubbed.

## Variations

- Replace SQLite with ClickHouse for fleet-wide replay.
- Tee stdout into Slack via a `MinPriority = Some Critical` filter.
- A fourth subscriber on `kind: thought` only, gated by stricter
  access control.
