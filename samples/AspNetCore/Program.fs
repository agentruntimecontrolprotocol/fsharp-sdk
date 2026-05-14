module ArcpSamples.AspNetCore

// Demonstrates the `Arcp.AspNetCore` adapter. Build a minimal
// Kestrel host that mounts the runtime at `/arcp`.

open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.Core
open ARCP.Runtime
open ARCP.AspNetCore

[<EntryPoint>]
let main argv =
    let builder = WebApplication.CreateBuilder(argv)
    let server =
        ArcpServer(
            { ArcpServerOptions.defaults with
                Features = Features.All })
    server.RegisterAgent("hello", fun _ -> task { return Json.serializeToElement<string> "hi" })
    builder.Services.AddSingleton<ArcpServer>(server) |> ignore
    let app = builder.Build()
    app.UseWebSockets() |> ignore
    ArcpEndpointRouteBuilderExtensions.MapArcp(app, "/arcp", server) |> ignore
    app.Run("http://127.0.0.1:7878")
    0
