module ARCP.Samples.ObserverSubscription.Program

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open FSharp.Control
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Subscriptions
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private filter: SubscribeFilter =
    {
        SessionId = None
        TraceId = None
        JobId = None
        StreamId = None
        Types = Some [ "job.progress"; "job.completed" ]
        MinPriority = None
    }

/// <summary>
/// Sample 05 — an observer subscribes to <c>job.progress</c> and
/// <c>job.completed</c> events (RFC §13). The runtime emits a few events
/// from a background job; the observer prints each matching event until
/// <c>job.completed</c> arrives.
/// </summary>
[<EntryPoint>]
let main _argv =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator

        let opts =
            { RuntimeOptions.defaults with
                OfferedCapabilities =
                    { Capabilities.empty with
                        Subscriptions = true
                    }
            }

        let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
        let _ = runtime.StartAsync CancellationToken.None

        runtime.RegisterTool(
            "job",
            fun (ctx: ToolContext) _ ->
                task {
                    for pct in [ 33; 66; 100 ] do
                        do! ctx.ProgressAsync(Some pct, Some(sprintf "tick %d" pct))
                        do! Task.Delay(20, ctx.CancellationToken)

                    return Ok(JsonSerializer.SerializeToElement("done"))
                }
        )

        let client = new Client(clientT, Bearer "secret")

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )

        let! sub = client.SubscribeAsync(filter)

        match sub with
        | Error e ->
            eprintfn "subscribe failed: %A" e
            do! runtime.StopAsync()
            return 1
        | Ok(sid, seq) ->
            printfn "subscribed: %s" (SubscriptionId.value sid)

            let observer =
                task {
                    let enumerator = seq.GetAsyncEnumerator()

                    try
                        let mutable running = true

                        while running do
                            let! moved = enumerator.MoveNextAsync().AsTask()

                            if not moved then
                                running <- false
                            else
                                let env = enumerator.Current

                                let kind =
                                    try
                                        env.Payload.GetProperty("type").GetString()
                                    with _ ->
                                        env.Type

                                printfn "observer saw: %s" kind

                                if kind = "job.completed" then
                                    running <- false
                    finally
                        let _ = enumerator.DisposeAsync()
                        ()
                }

            do! Task.Delay 100
            let! _ = client.InvokeAsync("job", JsonSerializer.SerializeToElement<string>("go"))

            try
                do! observer.WaitAsync(TimeSpan.FromSeconds 3.0)
            with :? TimeoutException ->
                eprintfn "observer timed out"

            do! runtime.StopAsync()
            do! (runtime :> IAsyncDisposable).DisposeAsync()
            do! (client :> IAsyncDisposable).DisposeAsync()
            return 0
    }
    |> fun t -> t.GetAwaiter().GetResult()
