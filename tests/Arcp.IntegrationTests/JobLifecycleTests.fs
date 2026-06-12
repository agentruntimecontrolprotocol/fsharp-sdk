module ARCP.IntegrationTests.JobLifecycleTests

open System
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

        // §7.4: cancellation terminates with job.error(CANCELLED).
        match r with
        | Ok rp -> failwithf "expected CANCELLED error, got result %A" rp
        | Error(ARCPError.Cancelled _) -> ()
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

[<Fact>]
let ``result_chunk events are not forwarded to Events but assemble via TryReadResultBytes`` () =
    task {
        let! p =
            connect
                (fun s ->
                    s.RegisterAgent(
                        "chunker",
                        fun ctx ->
                            task {
                                let rid = ctx.BeginStreamingResult()
                                let payload = System.Text.Encoding.UTF8.GetBytes("hello")

                                do!
                                    ctx.EmitResultChunkAsync(
                                        rid,
                                        0L,
                                        ReadOnlyMemory<byte>(payload),
                                        ChunkEncoding.Utf8,
                                        false,
                                        ctx.CancellationToken
                                    )

                                return Json.serializeToElement<string option> None
                            }
                    ))
                (Set.ofList [ Features.ResultChunk ])

        let! handle = p.Client.SubmitAsync(mkRequest "chunker", CancellationToken.None)

        let events = ResizeArray<JobEventBody>()
        let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync().AsTask()

                if has then events.Add enumerator.Current else more <- false
        finally
            ignore (enumerator.DisposeAsync().AsTask())

        events
        |> Seq.exists (function
            | JobEventBody.ResultChunk _ -> true
            | _ -> false)
        |> should equal false

        let! result = handle.Result

        match result with
        | Ok rp ->
            rp.ResultId.IsSome |> should equal true

            match handle.TryReadResultBytes(ResultId.ofString rp.ResultId.Value) with
            | Some bytes -> System.Text.Encoding.UTF8.GetString bytes |> should equal "hello"
            | None -> failwith "expected assembled bytes"
        | Error e -> failwithf "%A" e

        do! teardown p
    }
