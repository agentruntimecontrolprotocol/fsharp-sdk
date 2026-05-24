# Getting Started

This walks through a minimal ARCP runtime and client in F#. Five
minutes, zero infrastructure. By the end you'll have a job that streams
events back from agent to client.

## Prerequisites

- **.NET 10 SDK** — install from [dot.net](https://dot.net).
- Basic familiarity with F# or C#.

## Install

```xml
<PackageReference Include="Arcp" Version="*" />
```

`Arcp` re-exports `Arcp.Core`, `Arcp.Client`, and `Arcp.Runtime`.
If you only need the client, reference `Arcp.Client` directly.

## In-process demo (no network)

The fastest path to "I see events flowing" is `MemoryTransport.CreatePair()`,
which returns two `ITransport` halves wired together in memory:

```fsharp
open System.Threading
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime

let server =
    ArcpServer({ ArcpServerOptions.defaults with Features = Features.All })

server.RegisterAgent("echo", fun ctx ->
    task {
        do! ctx.EmitStatusAsync("running", None, ctx.CancellationToken)
        do! ctx.EmitLogAsync(LogLevel.Info, "received", ctx.CancellationToken)
        return Json.serializeToElement<{| echoed: obj |}> {| echoed = ctx.Input |}
    })

let clientT, serverT = MemoryTransport.CreatePair()
let _ = server.HandleSessionAsync(serverT, CancellationToken.None)

let client =
    new ArcpClient(
        clientT,
        { ArcpClientOptions.defaults with
            Auth = AuthScheme.Bearer "demo"
            Features = Features.All })

let session = (client.ConnectAsync CancellationToken.None).Result

let request : JobSubmitRequest = {
    Agent = "echo"
    Input = Json.serializeToElement<{| hi: int |}> {| hi = 1 |}
    LeaseRequest = None
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}

let handle = (client.SubmitAsync(request, CancellationToken.None)).Result

// handle.Result : Task<Result<JobResultPayload, ARCPError>>
match handle.Result.Result with
| Ok payload ->
    payload.Result
    |> Option.iter (fun el -> printfn "%s" (el.GetRawText()))
    // → { "echoed": { "hi": 1 } }
| Error err ->
    eprintfn "[%s] %s" (ARCPError.code err) (ARCPError.message err)
```

You should see two events (`status: running`, `log: info`) arriving before
the terminal `job.result`.

## C# usage

```csharp
using System.Text.Json;
using System.Threading;
using ARCP.Core;
using ARCP.Client;
using ARCP.Client.Transport;
using ARCP.Runtime;

var server = new ArcpServer(ArcpServerOptions.defaults);
server.RegisterAgent("echo", async ctx =>
{
    await ctx.EmitStatusAsync("running", null, ctx.CancellationToken);
    await ctx.EmitLogAsync(LogLevel.Info, "received", ctx.CancellationToken);
    return JsonSerializer.SerializeToElement(new { echoed = ctx.Input });
});

var (clientT, serverT) = MemoryTransport.CreatePair();
_ = server.HandleSessionAsync(serverT, CancellationToken.None);

// `ArcpClientOptions` is an F# record; from C# you build it with the
// generated constructor, passing every field.
using var client = new ArcpClient(
    clientT,
    new ArcpClientOptions(
        // "1.0" is this demo client's application version, not the ARCP protocol version.
        Client: new ClientIdentity("demo-client", "1.0"),
        Auth: AuthScheme.NewBearer("demo"),
        Features: Features.All,
        TimeProvider: TimeProvider.System,
        AutoAck: AutoAckOptions.defaults));

await client.ConnectAsync(CancellationToken.None);

var handle = await client.SubmitAsync(
    new JobSubmitRequest(
        Agent: "echo",
        Input: JsonSerializer.SerializeToElement(new { hi = 1 }),
        LeaseRequest: null,
        LeaseConstraints: null,
        IdempotencyKey: null,
        MaxRuntimeSec: null),
    CancellationToken.None);

var result = await handle.Result;
```

## Run over WebSocket

Swap the in-memory transport for a real socket using `Arcp.AspNetCore`:

```fsharp
open Microsoft.AspNetCore.Builder
open ARCP.AspNetCore

let app = WebApplication.Create()
app.UseWebSockets() |> ignore
app.MapArcp("/arcp", server) |> ignore
app.Run("http://localhost:7878")
```

On the client side use `WebSocketClientTransport.connectAsync`, which
opens a `ClientWebSocket`, attaches the bearer token (if any) as an
`Authorization` header on the upgrade, and returns an `ITransport`.
That header is host-layer metadata; ARCP session authentication still
comes from `ArcpClientOptions.Auth`:

```fsharp
open ARCP.Client.Transport

task {
    let! transport =
        WebSocketClientTransport.connectAsync
            (System.Uri "ws://localhost:7878/arcp")
            (Some "demo")                       // bearer token, or None
            CancellationToken.None

    let client =
        new ArcpClient(
            transport,
            { ArcpClientOptions.defaults with Auth = AuthScheme.Bearer "demo" })
    let! _session = client.ConnectAsync CancellationToken.None
    return client
}
```

## Run over stdio

Useful for spawning agents as child processes:

```bash
arcp serve --stdio --token $ARCP_TOKEN
```

See [cli.md](cli.md) and [transports.md](transports.md#stdio).

## Runnable samples

Twenty-four F# samples live under [`samples/`](../samples/). Start with:

```bash
dotnet run --project samples/QuickStart
dotnet run --project samples/SubmitAndStream
dotnet run --project samples/AspNetCore   # http://127.0.0.1:7878/arcp
```

## What's next

- [Architecture](architecture.md) — how the projects fit together.
- [Sessions guide](guides/sessions.md) — the handshake and resume model.
- [Jobs guide](guides/jobs.md) — submit, stream, cancel, retry.
- [Leases guide](guides/leases.md) — capability grants per job.
