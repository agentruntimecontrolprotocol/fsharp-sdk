# Sessions (§6)

An ARCP session is a long-lived, multiplexed channel between one client
and one runtime. All jobs, events, and subscriptions for a connection
flow through a single session.

## Handshake

The client opens a transport and calls `ConnectAsync`. Under the hood:

1. Client sends `session.hello` carrying `ClientIdentity`, `AuthPayload`,
   and the set of features it wants to negotiate.
2. Runtime authenticates the token, computes the intersection of
   advertised features, and replies with `session.welcome` carrying
   `SessionId`, `NegotiatedFeatures`, and the agent inventory.

```fsharp
open ARCP.Core
open ARCP.Client

let options =
    { ArcpClientOptions.defaults with
        // This is the client application's version, not the ARCP protocol version.
        Client = { Name = "my-client"; Version = "1.0" }
        Auth = AuthScheme.Bearer "my-token"
        Features = Features.All }

let client = new ArcpClient(transport, options)
let session = (client.ConnectAsync CancellationToken.None).Result

printfn "session_id  = %s" session.SessionId
printfn "features    = %A" session.NegotiatedFeatures
printfn "agents      = %A" session.AgentInventory
```

`SessionContext` returned by `ConnectAsync` is immutable — the
negotiated state doesn't change during the session lifetime.

## Session identity

`session_id` is a runtime-generated ULID assigned on `session.welcome`.
Every message after the handshake carries it. You can retrieve it from
`SessionContext.SessionId`.

## Heartbeat (§6.4)

When the `heartbeat` feature is negotiated, the runtime sends periodic
`session.ping` messages. The client responds with `session.pong`
automatically — you don't need to handle this manually.

If two consecutive pongs are missed, the runtime closes the session
with `HEARTBEAT_LOST`.

The heartbeat interval is set on the server side:

```fsharp
let options =
    { ArcpServerOptions.defaults with
        HeartbeatIntervalSec = Some 15 }  // 15 s; default is 30 s
```

## Ack and back-pressure (§6.5)

When `ack` is negotiated, the server tracks the last event sequence
number acknowledged by the client. If the client falls too far behind,
the server may pause sending more events.

Auto-ack is on by default and fires at 32 events or 250 ms:

```fsharp
let options =
    { ArcpClientOptions.defaults with
        AutoAck = { MaxPending = 32; MaxDelay = TimeSpan.FromMilliseconds 250.0 } }
```

To ack manually, disable auto-ack and call `AckAsync`:

```fsharp
let options =
    { ArcpClientOptions.defaults with
        AutoAck = AutoAckOptions.Manual }

// after processing events up to seq 42:
do! client.AckAsync(42L, ct)
```

## Closing

Call `CloseAsync` when done to send a graceful close:

```fsharp
do! client.CloseAsync(CancellationToken.None)
```

If the transport drops unexpectedly, `handle.Result` on in-flight jobs
will complete with an error. See [resume.md](resume.md) to reconnect
without losing events.

## Session on the runtime

Each call to `HandleSessionAsync` runs exactly one session. The runtime
processes the handshake, dispatches all messages on that transport, and
returns when the session closes:

```fsharp
// spawn a session per accepted socket
let _ = server.HandleSessionAsync(transport, ct)
```

The server is stateless across sessions; shared state lives in the
agent registry and (if configured) the `ICredentialStore`.

## See also

- [Auth guide](auth.md) — bearer tokens and custom verifiers.
- [Resume guide](resume.md) — reconnect and replay.
- [Spec §6](../../spec/docs/draft-arcp-1.1.md#6-session-layer)
