module ARCP.IntegrationTests.JobLifecycleTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``submit returns job.accepted with a fresh JobId`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<string> "ok" }))
                Features.All

        let! handle = p.Client.SubmitAsync(mkRequest "ok", CancellationToken.None)
        handle.JobId.Value |> should not' (be NullOrEmptyString)
        let! r = handle.Result

        match r with
        | Ok rp -> rp.FinalStatus |> should equal JobStatus.Success
        | Error e -> failwithf "%A" e

        do! teardown p
    }

[<Fact>]
let ``cancel transitions a job to Cancelled`` () =
    task {
        let! p =
            connect
                (fun s ->
                    s.RegisterAgent(
                        "forever",
                        fun ctx ->
                            task {
                                do! Task.Delay(-1, ctx.CancellationToken)
                                return Json.serializeToElement<int> 0
                            }
                    ))
                Features.All

        let! handle = p.Client.SubmitAsync(mkRequest "forever", CancellationToken.None)
        do! Task.Delay(50)
        let! _ = handle.CancelAsync(Some "test", CancellationToken.None)
        let! r = handle.Result

        match r with
        | Ok rp -> rp.FinalStatus |> should equal JobStatus.Cancelled
        | Error e -> failwithf "expected Cancelled, got %A" e

        do! teardown p
    }

[<Fact>]
let ``idempotency key returns same JobId on second submit`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<int> 0 }))
                Features.All

        let req =
            { mkRequest "ok" with
                IdempotencyKey = Some "key-1"
            }

        let! h1 = p.Client.SubmitAsync(req, CancellationToken.None)
        let! h2 = p.Client.SubmitAsync(req, CancellationToken.None)
        h1.JobId.Value |> should equal h2.JobId.Value
        do! teardown p
    }
