# Resume (§6.3)

If the transport drops mid-job, the client can reconnect and replay
all missed events — no polling, no duplicate processing. Resume is
gated on the `resume` feature (part of `Features.All`).

## How it works

The runtime keeps an event log for each session for up to
`ResumeWindowSec` seconds (default 600 s / 10 min). When the client
reconnects, it sends a `resume_token` in `session.hello`; the runtime
replays events from the last acknowledged sequence forward.

## Reconnecting

After a dropped connection, use `ResumeAsync` instead of
`ConnectAsync`. Pass the `ResumeToken` from the previous
`SessionContext`:

```fsharp
// first connect
let session = (client.ConnectAsync CancellationToken.None).Result
let resumeToken = session.ResumeToken

// … transport drops …

// reconnect on a new transport
let newTransport = new WebSocketClientTransport(uri) :> ITransport
let newClient = new ArcpClient(newTransport, ArcpClientOptions.defaults)
let resumed = (newClient.ResumeAsync(resumeToken, CancellationToken.None)).Result
```

After resuming, `JobHandle` instances you held before are still valid —
their `.Result` tasks complete as normal once the replayed events
arrive.

## Resume window

The server retains event logs for `ResumeWindowSec`:

```fsharp
let options =
    { ArcpServerOptions.defaults with
        ResumeWindowSec = 1200 }  // 20 min; default is 600 s
```

If the client reconnects after the window expires, the runtime rejects
the resume with `RESUME_WINDOW_EXPIRED`. Start a fresh session in that
case.

## Idempotency during resume

Jobs that completed before the disconnect are replayed as their
terminal event (`job.result` or `job.error`). The runtime never
re-executes a completed job during replay — it only replays the stored
events.

To prevent duplicate execution when retrying a *failed* job, use an
`IdempotencyKey` in the submit request so a duplicate submit collapses
to the same `job_id`. See [jobs guide](jobs.md#idempotency).

## Without resume

If you don't need resume (e.g. short-lived CLI sessions), set the
feature to `None`:

```fsharp
let options =
    { ArcpClientOptions.defaults with
        Features = Features.All |> Set.remove "resume" }
```

The runtime will not retain event logs for sessions that don't
negotiate the feature, saving memory.

## Runnable sample

```bash
dotnet run --project samples/StreamResume
```

The sample starts a streaming job, simulates a mid-stream disconnect,
and resumes to complete reassembly.

## See also

- [Sessions guide](sessions.md) — full session lifecycle.
- [Spec §6.3](../../spec/docs/draft-arcp-1.1.md#63-session-resume)
