namespace ARCP.Giraffe

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime

/// Giraffe `HttpHandler` that upgrades to a WebSocket and hands
/// the resulting transport to an `ArcpServer`. Composes inside
/// `choose [ … ]` pipelines.
[<AutoOpen>]
module ArcpGiraffe =
    let useArcp (path: string) (server: ArcpServer) : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) ->
            task {
                if ctx.Request.Path.Value <> path then
                    return! next ctx
                elif not ctx.WebSockets.IsWebSocketRequest then
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                    return! next ctx
                else
                    let! socket = ctx.WebSockets.AcceptWebSocketAsync()

                    let transport =
                        new WebSocketClientTransport(socket, ownsSocket = true) :> ITransport

                    do! server.HandleSessionAsync(transport, ctx.RequestAborted)
                    return! next ctx
            }
