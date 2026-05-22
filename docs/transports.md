# Transports

`ArcpClient` and `ArcpServer` are transport-agnostic. Both accept any
value that implements `ITransport`. Three transports ship in `Arcp.Client`:

## In-memory

`MemoryTransport.CreatePair()` returns two `ITransport` halves wired
together in memory — no sockets, no threads, ideal for unit tests and
samples:

```fsharp
let clientT, serverT = MemoryTransport.CreatePair()
let _ = server.HandleSessionAsync(serverT, CancellationToken.None)
let client = new ArcpClient(clientT, ArcpClientOptions.defaults)
```

Both halves are `IDisposable`. Disposing either side signals the other
with a graceful close.

## WebSocket

`WebSocketClientTransport` wraps a `System.Net.WebSockets.WebSocket`.
Use it on the client side to connect to a running runtime:

```fsharp
open ARCP.Client.Transport

let uri = Uri("ws://localhost:7878/arcp")
let transport = new WebSocketClientTransport(uri) :> ITransport
let client = new ArcpClient(transport, { ArcpClientOptions.defaults with
    Auth = AuthScheme.Bearer "my-token" })
let _ = client.ConnectAsync(CancellationToken.None).Result
```

On the server side, the runtime receives an already-upgraded socket —
`Arcp.AspNetCore` and `Arcp.Giraffe` handle the HTTP upgrade and pass
the socket as a transport:

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

`StdioTransport` reads newline-framed JSON from `stdin` and writes to
`stdout`. It's designed for spawning agents as child processes inside a
trust boundary.

The runtime side is started with the CLI:

```bash
arcp serve --stdio --token $ARCP_TOKEN
```

The parent process opens the child's stdin/stdout as a transport:

```fsharp
open ARCP.Client.Transport

let transport = StdioTransport.fromProcess childProcess
let client = new ArcpClient(transport, { ArcpClientOptions.defaults with
    Auth = AuthScheme.Bearer "my-token" })
```

Since the child runs inside a trust boundary, you can also omit the
token and configure the server with `AuthScheme.None`.

See [cli.md](cli.md) for the full `arcp serve` command reference.

## Custom transports

Implement `ITransport` from `ARCP.Client.Transport`:

```fsharp
type ITransport =
    abstract SendAsync : Envelope -> CancellationToken -> Task
    abstract ReceiveAsync : CancellationToken -> Task<Envelope option>
    abstract CloseAsync : CancellationToken -> Task
    inherit IDisposable
```

The runtime and client are fully agnostic — any implementation works.
