/// Generator proposes; reviewer holds veto via permission.request.
module ARCP.Samples.PermissionChallenge.Program

open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Errors
open ARCP.Ids
open ARCP.Samples.PermissionChallenge.Agents

let maxRevisions = 4

let fingerprint (diff: string) : string =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes diff)

    bytes
    |> Array.take 8
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

/// Generator: ask for a `repo.write` lease scoped to this exact diff.
/// Same idempotency_key per (ticket, diff): identical patches dedupe at the runtime.
let requestApply (client: Client) (ticketId: string) (patch: Patch) : Task<string> =
    task {
        let fp = fingerprint patch.Diff
        let key = IdempotencyKey.create (sprintf "review:%s:%s" ticketId fp)
        // Real call: client.RequestPermissionAsync(... idempotencyKey=key ...)
        return failwith "elided: permission.request → lease_id (or PERMISSION_DENIED)"
    }

/// Reviewer-side handler: emit permission.grant or permission.deny.
let respond (reviewer: Client) (request: obj) (verdict: ReviewVerdict) : Task =
    task {
        if verdict.Grant then
            // reviewer.SendPermissionGrantAsync(corr=request.Id, leaseSeconds=90)
            ()
        else
            // reviewer.SendPermissionDenyAsync(corr=request.Id, reason=verdict.Reason,
            //                                  code = ErrorCode.FAILED_PRECONDITION)
            ()

        return failwith "elided: permission.grant | permission.deny"
    }
    :> Task

let reviewerLoop (reviewer: Client) (ticket: string) : Task =
    task {
        // for env in reviewer.Events do
        //     if env.Type = "permission.request" then
        //         let! verdict = review ticket env
        //         do! respond reviewer env verdict
        return failwith "elided: drain inbound permission.request"
    }
    :> Task

[<EntryPoint>]
let main _ =
    task {
        // Two sessions, one per agent. In production they'd be in different
        // processes on different runtimes; the message contract is identical.
        let generator: Client = Unchecked.defaultof<_> // transport, identity, auth elided
        let reviewer: Client = Unchecked.defaultof<_>

        let ticketId = "JIRA-4812"

        let ticket =
            "Reject JWTs whose `aud` does not match the configured audience. Add a unit test."

        let revTask = reviewerLoop reviewer ticket

        let mutable priorDenial: string option = None
        let mutable applied = false
        let mutable n = 0

        while not applied && n < maxRevisions do
            let! patch = propose ticket priorDenial

            try
                let! lease = requestApply generator ticketId patch
                printfn "applied %s lease=%s" (fingerprint patch.Diff) lease
                applied <- true
            with ex ->
                // PERMISSION_DENIED → feed reason into the next prompt; else rethrow.
                priorDenial <- Some ex.Message

            n <- n + 1

        if not applied then
            printfn "abandoned after max_revisions"

        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
