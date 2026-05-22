# Arcp.Giraffe

Giraffe HTTP handler integration for the F# SDK. Use `Arcp.Giraffe`
when your server is built on Giraffe rather than bare ASP.NET Core
middleware.

## Installation

```
dotnet add package Arcp.Giraffe
```

## Namespace

```fsharp
open ARCP.Giraffe
```

## Quick start

```fsharp
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.Giraffe
open ARCP.Runtime

let server =
    let s = new ArcpServer(ArcpServerOptions.defaults)
    s.RegisterAgent("echo", fun ctx ->
        task { return ctx.Input })
    s

let webApp : HttpHandler =
    choose [
        route "/arcp" >=> arcpHandler server
        setStatusCode 404 >=> text "Not Found"
    ]

let builder = WebApplication.CreateBuilder(args)
builder.Services.AddGiraffe() |> ignore

let app = builder.Build()
app.UseWebSockets()
app.UseGiraffe(webApp)
app.Run()
```

## `arcpHandler`

```fsharp
val arcpHandler : ArcpServer -> HttpHandler
```

Upgrades the HTTP connection to a WebSocket, performs the ARCP
handshake, and hands the session off to the given `ArcpServer`.
Non-WebSocket requests receive `426 Upgrade Required`.

## `arcpHandlerWith`

```fsharp
val arcpHandlerWith : ArcpServerOptions -> ArcpServer -> HttpHandler
```

Like `arcpHandler`, but applies per-request option overrides before
the handshake. Useful when options differ by route or tenant.

## Combining with Giraffe routing

```fsharp
let webApp : HttpHandler =
    choose [
        subRoute "/api" apiRoutes
        route "/arcp"   >=> arcpHandler server
        route "/arcp/v2">=> arcpHandlerWith v2Options v2Server
        RequestErrors.NOT_FOUND "Not Found"
    ]
```

## Auth in Giraffe

Run an auth `HttpHandler` before `arcpHandler` — the pipeline composes
naturally:

```fsharp
let requireBearer : HttpHandler =
    fun next ctx ->
        task {
            let token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "")
            if not (validateToken token) then
                return! RequestErrors.UNAUTHORIZED "Bearer" "arcp" "invalid token" next ctx
            else
                return! next ctx
        }

let webApp : HttpHandler =
    route "/arcp" >=> requireBearer >=> arcpHandler server
```

Or use `ArcpServerOptions.TokenValidator` to validate inside the ARCP
handshake (see [auth guide](../guides/auth.md)).

## OTel with Giraffe

Wire `Arcp.Otel` manually on the transport before handing it to the
server — the same approach as the non-ASP.NET setup:

```fsharp
open ARCP.Otel

let server =
    new ArcpServer(
        serverOptions,
        fun rawTransport ->
            let traced = ArcpOtel.withServerTracing rawTransport tracerProvider
            sessionHandler traced)
```

## See also

- [Arcp.Runtime reference](Arcp.Runtime.md) — `ArcpServer`, `ArcpAgentHandler`.
- [Arcp.AspNetCore reference](Arcp.AspNetCore.md) — DI-based hosting alternative.
- [Auth guide](../guides/auth.md) — token validation.
- [Observability guide](../guides/observability.md) — OTel wiring.
