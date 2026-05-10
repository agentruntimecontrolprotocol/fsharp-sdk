module ARCP.IntegrationTests.InterruptTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Auth.Auth
open ARCP.Runtime

let private jsonZero () : JsonElement =
    JsonSerializer.SerializeToElement<int>(0)

[<Fact>]
let ``InterruptAsync transitions job to Blocked`` () =
    task {
        let sentEnvelopes = System.Collections.Generic.List<_>()

        let send env =
            lock sentEnvelopes (fun () -> sentEnvelopes.Add env)
            Task.CompletedTask

        let mgr =
            ARCP.Runtime.JobManager(TimeProvider.System, None, TimeSpan.FromSeconds 60.0, 2, send)

        let runDelay =
            fun (ct: CancellationToken) ->
                task {
                    try
                        do! Task.Delay(TimeSpan.FromSeconds 10.0, ct)
                        return Ok(jsonZero ())
                    with _ ->
                        return Error(Cancelled "cancelled")
                }

        let sid = SessionId.create ()
        let! jid = mgr.AcceptAsync(sid, "wait", runDelay)
        do! Task.Delay(50)

        let! result = mgr.InterruptAsync(jid, "please confirm")

        match result with
        | Ok() -> ()
        | Error e -> failwithf "expected Ok, got %A" e

        match mgr.TryGetState jid with
        | Some(Job.Blocked _) -> ()
        | other -> failwithf "expected Blocked, got %A" other

        let sawHumanInputRequest =
            sentEnvelopes |> Seq.exists (fun e -> e.Type = "human.input.request")

        Assert.True(sawHumanInputRequest, "expected a human.input.request envelope")

        // Cancel cleans up
        let! _ = mgr.CancelAsync(jid, Some "cleanup", Some 500)
        ()
    }
