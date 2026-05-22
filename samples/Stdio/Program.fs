module ArcpSamples.Stdio

// Demonstrates stdio transport (§4). The runtime reads
// newline-delimited JSON envelopes on stdin and writes them on
// stdout. Pipe this against the `arcp` CLI to exercise it.

open System.Threading
open ARCP.Core
open ARCP.Client.Transport
open ARCP.Runtime

[<EntryPoint>]
let main _argv =
    let server =
        ArcpServer(
            { ArcpServerOptions.defaults with
                Features = Features.All
            }
        )

    server.RegisterAgent("echo", fun _ -> task { return Json.serializeToElement<string> "echo" })
    let transport = StdioTransport.fromConsole ()
    server.HandleSessionAsync(transport, CancellationToken.None).GetAwaiter().GetResult()
    0
