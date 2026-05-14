namespace ARCP.AspNetCore

open System
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime

/// ASP.NET Core integration for ARCP. `MapArcp` mounts a runtime
/// at a path that accepts WebSocket upgrades. The runtime is the
/// single shared instance per process; one accepted socket
/// becomes one ARCP session.
[<AbstractClass; Sealed>]
type ArcpEndpointRouteBuilderExtensions private () =

    static member MapArcp(endpoints: IEndpointRouteBuilder, path: string, server: ArcpServer) : IEndpointConventionBuilder =
        endpoints.MapGet(path, RequestDelegate(fun ctx ->
            task {
                if not ctx.WebSockets.IsWebSocketRequest then
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                    return ()
                else
                    let! socket = ctx.WebSockets.AcceptWebSocketAsync()
                    let transport =
                        new WebSocketClientTransport(socket, ownsSocket = true) :> ITransport
                    do! server.HandleSessionAsync(transport, ctx.RequestAborted)
            } :> Task))
