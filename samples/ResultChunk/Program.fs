module ArcpSamples.ResultChunk

// Demonstrates `result_chunk` (§8.4): the agent streams a large
// result as multiple chunks terminated by a `job.result` carrying
// the assembled `result_id`.

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent(
                            "report",
                            fun ctx ->
                                task {
                                    let rid = ctx.BeginStreamingResult()

                                    for i in 0L .. 2L do
                                        let chunk = Encoding.UTF8.GetBytes(sprintf "chunk %d\n" i)

                                        do!
                                            ctx.EmitResultChunkAsync(
                                                rid,
                                                i,
                                                ReadOnlyMemory(chunk),
                                                ChunkEncoding.Utf8,
                                                i < 2L,
                                                ctx.CancellationToken
                                            )

                                    return Json.serializeToElement<obj> null
                                }
                        ))
                    (Set.ofList [ Features.ResultChunk ])

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "report"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)

            try
                let mutable more = true

                while more do
                    let! has = enumerator.MoveNextAsync().AsTask()

                    if not has then
                        more <- false
                    else
                        match enumerator.Current with
                        | JobEventBody.ResultChunk(_rid, seq, data, _, more') ->
                            writeLine (sprintf "chunk %d (more=%b): %s" seq more' data)
                        | _ -> ()
            finally
                ignore (enumerator.DisposeAsync().AsTask())

            let! _ = handle.Result
            do! teardown p
            return 0
        })
