# Arcp.Giraffe

Giraffe `HttpHandler` integration for the F# SDK. Use `Arcp.Giraffe`
when your server pipeline is composed with Giraffe combinators rather
than bare ASP.NET Core endpoint routing.

## Installation

```
dotnet add package Arcp.Giraffe
```

## Namespace

```fsharp
open ARCP.Giraffe   // brings `useArcp` into scope (AutoOpen)
```

## `useArcp`

```fsharp
val useArcp : path: string -> server: ArcpServer -> HttpHandler
```

- Matches against `ctx.Request.Path.Value` and passes through to `next`
  for non-matching paths, so it composes naturally inside `choose`.
- Returns `400 Bad Request` when the request matches the path but is
  not a WebSocket upgrade.
- On a successful upgrade, hands the resulting transport to
  `ArcpServer.HandleSessionAsync` and runs the session to completion.

## Quick start

```fsharp
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.Giraffe
open ARCP.Runtime

let server =
    let s = ArcpServer(ArcpServerOptions.defaults)
    s.RegisterAgent("echo", fun ctx -> task { return ctx.Input })
    s

let webApp : HttpHandler =
    choose [
        useArcp "/arcp" server
        route "/"      >=> text "ok"
        setStatusCode 404 >=> text "Not Found"
    ]

let builder = WebApplication.CreateBuilder()
builder.Services.AddGiraffe() |> ignore

let app = builder.Build()
app.UseWebSockets() |> ignore
app.UseGiraffe webApp
app.Run()
```

`UseWebSockets` must run before the Giraffe pipeline — without it the
upgrade is rejected at the middleware layer before `useArcp` sees the
request.

## Composing with other handlers

`useArcp` is just a `HttpHandler`, so per-tenant or per-route options
work by registering one `ArcpServer` per branch:

```fsharp
let webApp : HttpHandler =
    choose [
        subRoute "/api" apiRoutes
        useArcp "/arcp" publicServer
        useArcp "/arcp-internal" internalServer
        RequestErrors.NOT_FOUND "Not Found"
    ]
```

## Auth in Giraffe

Run a Giraffe auth handler before `useArcp` for HTTP-level rejections:

```fsharp
let requireBearer : HttpHandler =
    fun next ctx ->
        task {
            let auth = ctx.Request.Headers.Authorization.ToString()
            if not (validateToken (auth.Replace("Bearer ", ""))) then
                return! RequestErrors.UNAUTHORIZED "Bearer" "arcp" "invalid token" next ctx
            else
                return! next ctx
        }

let webApp : HttpHandler =
    choose [
        route "/arcp" >=> requireBearer >=> useArcp "/arcp" server
    ]
```

Alternatively, plug a custom `IBearerVerifier` into
`ArcpServerOptions.BearerVerifier` to authenticate inside the ARCP
handshake. See the [auth guide](../guides/auth.md).

## See also

- [Arcp.Runtime reference](Arcp.Runtime.md) — `ArcpServer`, `ArcpAgentHandler`.
- [Arcp.AspNetCore reference](Arcp.AspNetCore.md) — endpoint-routing alternative.
- [Auth guide](../guides/auth.md) — bearer verifiers.
- [Transports guide](../transports.md#websocket) — wire-level WebSocket framing.
