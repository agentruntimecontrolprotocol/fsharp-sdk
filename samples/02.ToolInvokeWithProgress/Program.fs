module ARCP.Samples.ToolInvokeWithProgress.Program

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open FSharp.Control
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

/// <summary>
/// Sample 02 — register a tool that emits three progress events then
/// returns a value. The client subscribes to per-job progress and prints
/// each event plus the final result. Exercises RFC §10.3 (job.progress)
/// and §10.4 (job.completed).
/// </summary>
[<EntryPoint>]
let main _argv =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator

        let opts =
            { RuntimeOptions.defaults with
                OfferedCapabilities = Capabilities.empty
            }

        let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
        let _ = runtime.StartAsync System.Threading.CancellationToken.None

        runtime.RegisterTool(
            "long-task",
            fun (ctx: ToolContext) _args ->
                task {
                    for pct in [ 25; 50; 75 ] do
                        do! ctx.ProgressAsync(Some pct, Some(sprintf "step %d%%" pct))
                        do! Task.Delay(20, ctx.CancellationToken)

                    return Ok(JsonSerializer.SerializeToElement("done"))
                }
        )

        let client = new Client(clientT, Bearer "secret")
        let! _ = client.OpenAsync(Capabilities.empty, System.Threading.CancellationToken.None)

        let! jobId, resultTask =
            client.InvokeWithJobIdAsync("long-task", JsonSerializer.SerializeToElement<string>("go"))

        let progressTask =
            task {
                let progresses = client.SubscribeProgress jobId
                let enumerator = progresses.GetAsyncEnumerator()

                try
                    let mutable running = true

                    while running do
                        let! moved = enumerator.MoveNextAsync().AsTask()

                        if not moved then
                            running <- false
                        else
                            let p = enumerator.Current

                            printfn
                                "progress: %s %s"
                                (p.Percent
                                 |> Option.map (sprintf "%d%%")
                                 |> Option.defaultValue "?")
                                (p.Message |> Option.defaultValue "")
                finally
                    let _ = enumerator.DisposeAsync()
                    ()
            }

        let! result = resultTask
        do! Task.Delay 100

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (client :> IAsyncDisposable).DisposeAsync()

        try
            do! progressTask.WaitAsync(TimeSpan.FromMilliseconds 200.0)
        with _ ->
            ()

        match result with
        | Ok(Some v) ->
            printfn "tool returned: %s" (v.GetString())
            return 0
        | other ->
            eprintfn "unexpected result: %A" other
            return 1
    }
    |> fun t -> t.GetAwaiter().GetResult()
