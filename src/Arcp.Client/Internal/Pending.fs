namespace ARCP.Client.Internal

open System.Collections.Concurrent
open System.Threading.Tasks
open ARCP.Core

/// In-flight request → response correlation by envelope `id`.
///
/// Used by the client to await `session.welcome` after sending
/// `session.hello`, `job.accepted` after `job.submit`, and
/// `session.jobs` after `session.list_jobs`.
type internal PendingRegistry() =
    let pending = ConcurrentDictionary<string, TaskCompletionSource<Envelope>>()

    /// Reserve a slot keyed on `requestId`. Resolves when `complete`
    /// is called with a matching envelope.
    member _.Register(requestId: string) : Task<Envelope> =
        let tcs = TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously)
        if not (pending.TryAdd(requestId, tcs)) then
            failwithf "Duplicate pending request id: %s" requestId
        tcs.Task

    /// Mark a pending request complete. Returns true if a waiter
    /// was found, false otherwise (caller is free to ignore).
    member _.TryComplete(requestId: string, env: Envelope) : bool =
        match pending.TryRemove(requestId) with
        | true, tcs ->
            tcs.TrySetResult env |> ignore
            true
        | _ -> false

    /// Fail all pending operations (e.g. on transport close).
    member _.FailAll(error: exn) : unit =
        for kvp in pending do
            kvp.Value.TrySetException error |> ignore
        pending.Clear()
