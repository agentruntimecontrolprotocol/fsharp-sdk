# Resume (§6.3)

If the transport drops mid-job, ARCP allows the client to reconnect
and have the runtime replay missed events — no polling, no duplicate
processing. The runtime keeps an event log for each session up to
`ResumeWindowSec` seconds (default 600 s); on reconnect, the new
`session.hello` carries a `Resume` payload identifying which session
to continue and the last event sequence the client saw.

## Status in the F# SDK

The wire-level resume machinery is fully implemented:

- The runtime buffers events in an `EventLog` keyed by session, sized
  by `ArcpServerOptions.ResumeWindowSec`.
- `session.welcome` returns a `ResumeToken` (surfaced on
  `SessionContext.ResumeToken`) and a `ResumeWindowSec`.
- `SessionHelloPayload` carries an optional `Resume` field
  (`ResumeRequest = { SessionId; ResumeToken; LastEventSeq }`) that the
  runtime accepts on incoming sessions.

What the SDK does *not* currently expose is a dedicated client-side
`ResumeAsync` convenience. To resume from a dropped connection today,
construct a new transport, build an `ArcpClient` over it, and call
`ConnectAsync` — but use a custom path that injects the `Resume`
payload into the hello message. For most applications, treat a dropped
session as terminal and start a fresh one; resume is a planned
ergonomic addition.

## Capturing the resume token

`SessionContext` is returned from `ConnectAsync`. Hold onto its
`ResumeToken` and `SessionId` if you intend to resume:

```fsharp
let session = (client.ConnectAsync CancellationToken.None).Result
let sessionId   = session.SessionId
let resumeToken = session.ResumeToken
let windowSec   = session.ResumeWindowSec
```

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

## Disabling buffering

The runtime always retains events for `ResumeWindowSec`. To shrink
memory pressure for short-lived sessions, lower `ResumeWindowSec`:

```fsharp
let options =
    { ArcpServerOptions.defaults with
        ResumeWindowSec = 30 }   // 30 s — enough to ride out a flap
```

## Runnable sample

```bash
dotnet run --project samples/Resume
```

The sample starts a streaming job, simulates a mid-stream disconnect,
and reconnects to complete reassembly.

## See also

- [Sessions guide](sessions.md) — full session lifecycle.
- [Spec §6.3](../../spec/docs/draft-arcp-1.1.md#63-session-resume)
