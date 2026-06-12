module ArcpSamples.Giraffe

// Demonstrates the `Arcp.Giraffe` adapter. The ARCP `HttpHandler`
// composes into any `choose [ ... ]` pipeline.

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ARCP.Core
open ARCP.Runtime
open ARCP.Giraffe

[<EntryPoint>]
let main argv =
    let builder = WebApplication.CreateBuilder(argv)

    let server =
        new ArcpServer(
            { ArcpServerOptions.defaults with
                Features = Features.All
            }
        )

    server.RegisterAgent("hello", fun _ -> task { return Json.serializeToElement<string> "hi" })
    builder.Services.AddGiraffe() |> ignore
    let app = builder.Build()
    app.UseWebSockets() |> ignore
    app.UseGiraffe(choose [ useArcp "/arcp" server; route "/" >=> text "ARCP runtime online" ])
    app.Run("http://127.0.0.1:7879")
    0
