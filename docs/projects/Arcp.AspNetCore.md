# Arcp.AspNetCore

ASP.NET Core hosting integration for the F# SDK. `Arcp.AspNetCore`
exposes a single endpoint extension that accepts a WebSocket upgrade
and hands the resulting transport to an `ArcpServer`.

## Installation

```
dotnet add package Arcp.AspNetCore
```

## Namespace

```fsharp
open ARCP.AspNetCore
```

## `MapArcp`

```fsharp
ArcpEndpointRouteBuilderExtensions.MapArcp
    (endpoints: IEndpointRouteBuilder, path: string, server: ArcpServer)
    : IEndpointConventionBuilder
```

Mounts the supplied `ArcpServer` at `path` as a `GET` endpoint that
accepts WebSocket upgrades. Non-WebSocket requests get
`400 Bad Request`. Each accepted socket becomes one ARCP session and
runs to completion of `HandleSessionAsync`.

```fsharp
open Microsoft.AspNetCore.Builder
open ARCP.AspNetCore
open ARCP.Runtime

let server = ArcpServer(ArcpServerOptions.defaults)
server.RegisterAgent("echo", fun ctx -> task { return ctx.Input })

let app = WebApplication.Create()
app.UseWebSockets() |> ignore
app.MapArcp("/arcp", server) |> ignore
app.Run("http://localhost:7878")
```

`app.UseWebSockets()` must be called before `MapArcp` — without it the
HTTP middleware rejects the upgrade and the client sees the connection
close immediately.

## What's not in this package

`Arcp.AspNetCore` does not provide a DI builder, an `AddArcp`
extension, an `[ApiController]`-style attribute, or token-validator
middleware. Authentication lives on the runtime side, via
`ArcpServerOptions.BearerVerifier` (see the [auth guide](../guides/auth.md)).
If you want per-route option overrides, construct multiple `ArcpServer`
instances and call `MapArcp` once per route.

## Hosting multiple endpoints

```fsharp
let publicServer = ArcpServer(publicOptions)
let internalServer = ArcpServer(internalOptions)

app.UseWebSockets() |> ignore
app.MapArcp("/arcp", publicServer) |> ignore
app.MapArcp("/arcp-internal", internalServer) |> ignore
```

## Custom HTTP middleware

Because `MapArcp` returns an `IEndpointConventionBuilder`, you can apply
ASP.NET Core conventions to it like any other endpoint:

```fsharp
app.MapArcp("/arcp", server)
   .RequireAuthorization()
   .WithMetadata("internal")
```

Note that any auth middleware runs before the WebSocket upgrade — once
the socket is accepted, ARCP-level auth (`session.hello.payload.auth`)
takes over inside the protocol handshake.

## See also

- [Sessions guide](../guides/sessions.md) — handshake, auth, disconnect.
- [Auth guide](../guides/auth.md) — `IBearerVerifier` and dev-mode auth.
- [Transports guide](../transports.md#websocket) — wire-level WebSocket framing.
- [Arcp.Runtime reference](Arcp.Runtime.md) — `ArcpServer`, `ArcpAgentHandler`.
- [Arcp.Giraffe reference](Arcp.Giraffe.md) — Giraffe HTTP handler alternative.
