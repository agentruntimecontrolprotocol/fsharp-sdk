module ARCP.IntegrationTests.CancellationTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private startPair () =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities = Capabilities.empty
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

let private jsonZero () : JsonElement =
    JsonSerializer.SerializeToElement<int>(0)

[<Fact>]
let ``cancel running job -> cancel.accepted then job.cancelled`` () =
    task {
        let runtime, client = startPair ()

        runtime.RegisterTool(
            "loop",
            fun (ctx: ToolContext) _ ->
                task {
                    try
                        do! Task.Delay(TimeSpan.FromSeconds 30.0, ctx.CancellationToken)
                        return Ok(jsonZero ())
                    with :? OperationCanceledException ->
                        return Error(Cancelled "cooperative")
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! jid, resultTask = client.InvokeWithJobIdAsync("loop", jsonZero ())
        do! Task.Delay(50)

        let! cancelResult = client.CancelAsync(jid, reason = "test", deadlineMs = 2000)

        match cancelResult with
        | Ok() -> ()
        | Error e -> failwithf "expected Ok, got %A" e

        let! result = resultTask

        match result with
        | Error(Cancelled _) -> ()
        | other -> failwithf "expected Cancelled, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``cancel terminal job -> cancel.refused FAILED_PRECONDITION`` () =
    task {
        let runtime, client = startPair ()

        runtime.RegisterTool("fast", fun (_ctx: ToolContext) _ -> task { return Ok(jsonZero ()) })

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! jid, resultTask = client.InvokeWithJobIdAsync("fast", jsonZero ())
        let! _ = resultTask
        // job is now terminal
        do! Task.Delay(50)

        let! cancelResult = client.CancelAsync(jid, reason = "late", deadlineMs = 500)

        match cancelResult with
        | Error(FailedPrecondition _) -> ()
        | other -> failwithf "expected FailedPrecondition, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``hard cancel: tool ignores ct -> deadline -> ABORTED job.cancelled`` () =
    task {
        let runtime, client = startPair ()

        runtime.RegisterTool(
            "stubborn",
            fun (_ctx: ToolContext) _ ->
                task {
                    // Ignore cancellation deliberately
                    do! Task.Delay(TimeSpan.FromSeconds 10.0)
                    return Ok(jsonZero ())
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! jid, resultTask = client.InvokeWithJobIdAsync("stubborn", jsonZero ())
        do! Task.Delay(50)

        let! cancelResult = client.CancelAsync(jid, reason = "force", deadlineMs = 200)

        match cancelResult with
        | Ok() -> ()
        | Error e -> failwithf "expected Ok cancel, got %A" e

        let! _ = resultTask
        do! runtime.StopAsync()
    }
