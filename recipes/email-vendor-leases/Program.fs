module ArcpRecipes.EmailVendorLeases

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpRecipes.RecipeHarness

let private runTool (name: string) =
    match name with
    | "inbox_list" ->
        jsonObj {| messages = [| {| id = "m1"; subject = "Contract question" |} |] |}
    | "inbox_read" ->
        jsonObj {| id = "m1"; from = "ops@example.test"; subject = "Contract question"; urgency = "high" |}
    | _ -> jsonObj {| ok = true |}

let private triageAgent : ArcpAgentHandler =
    fun ctx ->
        task {
            do! ctx.EmitToolCallAsync("inbox_read", jsonObj {| id = "m1" |}, "call-read", ctx.CancellationToken)
            do! ctx.ValidateOpAsync(Capabilities.ToolCall, "inbox_read", ctx.CancellationToken)
            let message = runTool "inbox_read"
            do! ctx.EmitVendorEventAsync("x-vendor.acme.email.parsed", message, ctx.CancellationToken)
            do! ctx.EmitToolResultAsync("call-read", ToolOutcome.Result message, ctx.CancellationToken)

            do! ctx.EmitToolCallAsync("send_reply", jsonObj {| id = "m1"; body = "I can help." |}, "call-send", ctx.CancellationToken)
            try
                do! ctx.ValidateOpAsync(Capabilities.ToolCall, "send_reply", ctx.CancellationToken)
                do! ctx.EmitToolResultAsync("call-send", ToolOutcome.Result(runTool "send_reply"), ctx.CancellationToken)
                return jsonObj {| drafted_reply = ""; sent = true |}
            with
            | :? ArcpException as ex ->
                do! ctx.EmitToolResultAsync(
                        "call-send",
                        ToolOutcome.Error(ex.Code, ex.Message, ex.Retryable),
                        ctx.CancellationToken)
                return jsonObj {| drafted_reply = "Drafted reply for human approval"; sent = false |}
        }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let readOnlyLease =
                Lease.empty
                |> Lease.withCapability Capabilities.ToolCall [ "inbox_list"; "inbox_read" ]

            let! pair =
                connect
                    (fun server -> server.RegisterAgent("triage", triageAgent))
                    (Set.ofList [ Features.ResultChunk ])

            let! handle =
                pair.Client.SubmitAsync(
                    { Agent = "triage"
                      Input = jsonObj {| inbox = "support" |}
                      LeaseRequest = Some readOnlyLease
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
                    else writeLine (sprintf "event: %s" (JobEventBody.kind enumerator.Current))
            finally
                ignore (enumerator.DisposeAsync().AsTask())

            let! result = handle.Result
            match result with
            | Ok payload -> writeLine (sprintf "triage result: %s" (payload.Result |> Option.map _.GetRawText() |> Option.defaultValue "null"))
            | Error err -> writeErr (sprintf "triage failed: %s" (ARCPError.code err))

            do! teardown pair
            return 0
        })
