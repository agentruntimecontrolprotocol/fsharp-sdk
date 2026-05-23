# Transports

`ArcpClient` and `ArcpServer` are transport-agnostic. Both accept any
value that implements `ITransport`. Three transports ship in `Arcp.Client`:

## In-memory

`MemoryTransport.CreatePair()` returns two `ITransport` halves wired
together in memory — no sockets, no threads, ideal for unit tests and
samples:

```fsharp
open ARCP.Client.Transport

let clientT, serverT = MemoryTransport.CreatePair()
let _ = server.HandleSessionAsync(serverT, CancellationToken.None)
let client = new ArcpClient(clientT, ArcpClientOptions.defaults)
```

Calling `CloseAsync` on either half completes both channels, ending
the paired `Receive` enumerator on the other side.

## WebSocket

`WebSocketClientTransport` wraps a `System.Net.WebSockets.WebSocket`.
The convenience constructor `connectAsync` opens a `ClientWebSocket`,
adds the bearer token (if any) as the `Authorization` header on the
upgrade, and returns an `ITransport`:

```fsharp
open ARCP.Client.Transport

task {
    let! transport =
        WebSocketClientTransport.connectAsync
            (Uri "ws://localhost:7878/arcp")
            (Some "my-token")                 // bearer; None for no auth
            CancellationToken.None

    let client =
        new ArcpClient(transport,
            { ArcpClientOptions.defaults with Auth = AuthScheme.Bearer "my-token" })

    let! _session = client.ConnectAsync CancellationToken.None
    return client
}
```

You can also wrap a `WebSocket` you opened yourself:

```fsharp
let ws = new ClientWebSocket()
do! ws.ConnectAsync(uri, ct)
let transport = new WebSocketClientTransport(ws, ownsSocket = true) :> ITransport
```

On the server side, the runtime receives an already-upgraded socket —
`Arcp.AspNetCore` and `Arcp.Giraffe` handle the HTTP upgrade and pass
the resulting `WebSocketClientTransport` to `ArcpServer.HandleSessionAsync`:

```fsharp
// ASP.NET Core (Arcp.AspNetCore)
app.UseWebSockets() |> ignore
app.MapArcp("/arcp", server) |> ignore

// Giraffe (Arcp.Giraffe)
choose [
    useArcp "/arcp" server
    route "/" >=> text "ok"
]
```

See [Arcp.AspNetCore](projects/Arcp.AspNetCore.md) and
[Arcp.Giraffe](projects/Arcp.Giraffe.md).

## Stdio

`StdioTransport` reads newline-framed JSON envelopes from a
`TextReader` and writes them to a `TextWriter`. It's designed for
spawning agents as child processes inside a trust boundary.

The runtime side is started with the CLI:

```bash
arcp serve --stdio --token $ARCP_TOKEN
```

That CLI uses `StdioTransport.fromConsole ()`, which binds to
`Console.In` / `Console.Out`. A parent process that wants to talk to
the child process via its stdio streams constructs a `StdioTransport`
directly from those streams:

```fsharp
open System.Diagnostics
open ARCP.Client.Transport

let child = Process.Start(ProcessStartInfo(
    FileName = "arcp",
    Arguments = "serve --stdio --token secret",
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    UseShellExecute = false))

let transport =
    new StdioTransport(child.StandardOutput, child.StandardInput, ownsStreams = false) :> ITransport

let client =
    new ArcpClient(transport, { ArcpClientOptions.defaults with
        Auth = AuthScheme.Bearer "secret" })
```

Since the child runs inside a trust boundary, you can also omit the
token and configure the server with `AuthScheme.None`.

See [cli.md](cli.md) for the full `arcp serve` command reference.

## Custom transports

Implement `ITransport` from `ARCP.Client`:

```fsharp
type ITransport =
    /// Send a single envelope; completes once the wire has accepted it.
    abstract SendAsync : envelope: Envelope * ct: CancellationToken -> Task
    /// Stream received envelopes. The enumerator completes cleanly on
    /// graceful close and throws on transport failure.
    abstract Receive : ct: CancellationToken -> IAsyncEnumerable<Envelope>
    /// Idempotent close.
    abstract CloseAsync : ct: CancellationToken -> Task
```

Implementations are expected to deliver envelopes in order. `Receive`
is consumed by both `ArcpClient` and `ArcpServer` in a single
loop per session. Any implementation works — the runtime and client
are fully agnostic.
