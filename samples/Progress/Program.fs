module ArcpSamples.Progress

// Demonstrates `progress` (§8.2.1).

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
                            "counter",
                            fun ctx ->
                                task {
                                    for i in 0..4 do
                                        do!
                                            ctx.EmitProgressAsync(
                                                decimal i,
                                                Some 4m,
                                                Some "step",
                                                None,
                                                ctx.CancellationToken
                                            )

                                    return jsonString "done"
                                }
                        ))
                    (Set.ofList [ Features.Progress ])

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "counter"
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
                        | JobEventBody.Progress(c, t, _, _) -> writeLine (sprintf "progress %O/%A" c t)
                        | _ -> ()
            finally
                ignore (enumerator.DisposeAsync().AsTask())

            let! _ = handle.Result
            do! teardown p
            return 0
        })
