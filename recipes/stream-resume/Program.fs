module ArcpRecipes.StreamResume

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpRecipes.RecipeHarness

let private writerAgent : ArcpAgentHandler =
    fun ctx ->
        task {
            let resultId = ctx.BeginStreamingResult()
            let chunks = [
                "Opening paragraph for the long-form article.\n"
                "Middle section with more detail and citations.\n"
                "Closing summary with the final recommendation.\n"
            ]

            for i, chunk in chunks |> List.indexed do
                let bytes = Encoding.UTF8.GetBytes chunk
                do! ctx.EmitResultChunkAsync(
                        resultId,
                        int64 i,
                        ReadOnlyMemory bytes,
                        ChunkEncoding.Utf8,
                        i < chunks.Length - 1,
                        ctx.CancellationToken)

            return Json.serializeToElement<obj> null
        }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! pair =
                connectWithOptions
                    (fun options -> { options with ResumeWindowSec = 60 })
                    (fun server -> server.RegisterAgent("long-form", writerAgent))
                    (Set.ofList [ Features.ResultChunk; Features.Subscribe ])

            match pair.Client.Session with
            | Some session ->
                writeLine (sprintf "resume_token: %s" session.ResumeToken)
                writeLine (sprintf "resume_window_sec: %d" session.ResumeWindowSec)
            | None -> writeErr "no session"

            let! handle =
                pair.Client.SubmitAsync(
                    { Agent = "long-form"
                      Input = jsonObj {| topic = "resumable result chunks" |}
                      LeaseRequest = None
                      LeaseConstraints = None
                      IdempotencyKey = None
                      MaxRuntimeSec = None },
                    CancellationToken.None)

            let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)
            try
                let mutable more = true
                while more do
                    let! has = enumerator.MoveNextAsync().AsTask()
                    if not has then more <- false
                    else
                        match enumerator.Current with
                        | JobEventBody.ResultChunk(_resultId, seq, data, _encoding, moreChunks) ->
                            writeLine (sprintf "chunk %d more=%b %s" seq moreChunks data)
                        | other -> writeLine (sprintf "event: %s" (JobEventBody.kind other))
            finally
                ignore (enumerator.DisposeAsync().AsTask())

            let! _ = handle.Result
            do! teardown pair
            return 0
        })
