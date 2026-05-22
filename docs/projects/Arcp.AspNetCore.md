# Arcp.AspNetCore

ASP.NET Core hosting integration for the F# SDK. Wires `ArcpServer`
into the ASP.NET Core pipeline with WebSocket upgrade, DI, and
middleware support.

## Installation

```
dotnet add package Arcp.AspNetCore
```

## Namespace

```fsharp
open ARCP.AspNetCore
```

## Quick start

```fsharp
// Program.fs
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.AspNetCore

let builder = WebApplication.CreateBuilder(args)

builder.Services
    .AddArcp(fun opts ->
        opts.Features     <- Features.All
        opts.Capabilities <- {
            ServerCapabilities.defaults with
                Agents = [ "echo"; "research" ]
        })
    |> ignore

let app = builder.Build()

app.UseWebSockets()
app.MapArcp("/arcp")

app.Run()
```

Register agents via DI or inline on the `IServiceCollection`:

```fsharp
builder.Services.AddArcp()
    .AddAgent("echo", fun ctx ->
        task { return ctx.Input })
    .AddAgent("research", fun ctx ->
        task {
            do! ctx.EmitStatusAsync("thinking", None, ctx.CancellationToken)
            return Json.serializeToElement<bool> true
        })
    |> ignore
```

## `IServiceCollection` extensions

```fsharp
// Core registration
services.AddArcp() : IArcpBuilder
services.AddArcp(configure: ArcpServerOptions -> unit) : IArcpBuilder

// Optional OTel wiring (requires Arcp.Otel)
services.AddArcpTracing() : IArcpBuilder
```

## `IArcpBuilder`

Fluent builder returned by `AddArcp`:

```fsharp
type IArcpBuilder =
    member AddAgent : name: string -> handler: ArcpAgentHandler -> IArcpBuilder
    member AddProvisioner<'T when 'T :> ILeaseProvisioner> : unit -> IArcpBuilder
    member AddCredentialStore<'T when 'T :> ICredentialStore> : unit -> IArcpBuilder
```

## `IApplicationBuilder` / `IEndpointRouteBuilder` extensions

```fsharp
// Middleware form
app.UseArcp(path: string) : IApplicationBuilder

// Endpoint routing form (preferred for .NET 10)
app.MapArcp(pattern: string) : RouteHandlerBuilder
app.MapArcp(pattern: string, options: ArcpServerOptions) : RouteHandlerBuilder
```

Both forms accept WebSocket connections at `pattern`, negotiate the
ARCP handshake, and dispatch to registered agents.

## `WebSocketTransport`

Adapts a `System.Net.WebSockets.WebSocket` to `ITransport`:

```fsharp
// Server side (from HttpContext)
let ws = context.WebSockets.AcceptWebSocketAsync() |> Async.AwaitTask
let transport = WebSocketTransport.fromServerSocket ws

// Client side
let ws = new ClientWebSocket()
do! ws.ConnectAsync(Uri("wss://example.com/arcp"), ct)
let transport = WebSocketTransport.fromClientSocket ws
```

`WebSocketTransport` handles fragmentation, UTF-8 framing, and graceful
close handshake automatically.

## Dependency injection for agents

Agents can receive services from the DI container:

```fsharp
type ResearchAgent(logger: ILogger<ResearchAgent>, http: HttpClient) =
    member _.Run(ctx: JobContext) =
        task {
            logger.LogInformation("starting {JobId}", ctx.JobId)
            // …
            return Json.serializeToElement<bool> true
        }

// Registration
builder.Services.AddArcp()
    .AddAgent("research", fun sp ->
        let agent = sp.GetRequiredService<ResearchAgent>()
        agent.Run)
    |> ignore
```

## Auth middleware

Supply a token validator via `ArcpServerOptions`:

```fsharp
builder.Services.AddArcp(fun opts ->
    opts.TokenValidator <- Some (fun token ct ->
        task {
            let claims = validateJwt token
            return if claims.IsValid then Some claims.Principal else None
        }))
    |> ignore
```

The validator is called for every `session.hello`. Returning `None`
causes the runtime to send `session.error` with code `UNAUTHENTICATED`.

## OTel wiring (with `Arcp.Otel`)

```fsharp
builder.Services
    .AddArcp()
    .AddArcpTracing()    // injects Arcp.Otel into the transport pipeline
    |> ignore

builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun b ->
        b.AddArcpInstrumentation()
         .AddOtlpExporter() |> ignore)
```

## See also

- [Sessions guide](../guides/sessions.md) — handshake, auth, disconnect.
- [Auth guide](../guides/auth.md) — token validation, bearer schemes.
- [Arcp.Runtime reference](Arcp.Runtime.md) — `ArcpServer`, `ArcpAgentHandler`.
- [Arcp.Otel reference](Arcp.Otel.md) — `AddArcpTracing`, `AddArcpInstrumentation`.
- [Arcp.Giraffe reference](Arcp.Giraffe.md) — Giraffe HTTP handler alternative.
