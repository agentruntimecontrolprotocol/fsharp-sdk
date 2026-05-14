namespace ARCP.Runtime.Internal

open System.Collections.Concurrent
open System.Collections.Generic
open ARCP.Core

/// Fan-out registry: which sessions subscribe to which jobs.
///
/// The key spec subtlety (§7.6) is that replayed events for a
/// subscriber MUST use the **subscriber's** session seq, not the
/// owning session's. This module just tracks who subscribes to
/// what; the seq remap happens at emit time inside the runtime.
type SubscriptionFanout() =
    let byJob = ConcurrentDictionary<string, HashSet<string>>()
    let byPrincipal = ConcurrentDictionary<string, HashSet<string>>()
    let lockObj = obj ()

    /// Register `sessionId` as a subscriber of `jobId`.
    member _.Subscribe(jobId: JobId, sessionId: SessionId) : unit =
        lock lockObj (fun () ->
            let set = byJob.GetOrAdd(jobId.Value, fun _ -> HashSet<string>())
            set.Add sessionId.Value |> ignore)

    member _.Unsubscribe(jobId: JobId, sessionId: SessionId) : bool =
        lock lockObj (fun () ->
            match byJob.TryGetValue jobId.Value with
            | true, set -> set.Remove sessionId.Value
            | _ -> false)

    member _.UnsubscribeAll(sessionId: SessionId) : unit =
        lock lockObj (fun () ->
            for kvp in byJob do
                kvp.Value.Remove sessionId.Value |> ignore)

    /// Sessions subscribing to `jobId`.
    member _.Subscribers(jobId: JobId) : SessionId list =
        lock lockObj (fun () ->
            match byJob.TryGetValue jobId.Value with
            | true, set -> set |> Seq.map SessionId.ofString |> Seq.toList
            | _ -> [])
