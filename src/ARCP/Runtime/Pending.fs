namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Errors
open ARCP.Ids

/// <summary>
/// Registry of pending request/response correlations. Each <c>RegisterAsync</c>
/// returns a <see cref="Task{T}"/> that completes when <see cref="Resolve"/>,
/// <see cref="Fail"/>, or <see cref="Cancel"/> is called for the same id, or
/// when the registered deadline / cancellation token fires.
/// </summary>
module Pending =

    /// <summary>
    /// Threadsafe pending-task registry keyed by <see cref="MessageId"/>.
    /// The TaskCompletionSource is constructed with
    /// <c>RunContinuationsAsynchronously</c> so resolving a pending entry never
    /// runs continuations on the resolver's thread.
    /// </summary>
    type PendingRegistry<'T>() =

        let entries =
            ConcurrentDictionary<MessageId, TaskCompletionSource<'T> * IDisposable>()

        let tryRemove (id: MessageId) =
            match entries.TryRemove(id) with
            | true, entry -> Some entry
            | _ -> None

        /// <summary>
        /// Register a pending entry. Returns a task that completes with the
        /// resolved value, faults with the supplied error, or cancels.
        /// </summary>
        member this.RegisterAsync(id: MessageId, deadline: TimeSpan option, ct: CancellationToken) : Task<'T> =
            let tcs =
                TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

            let ctRegistration = ct.Register(fun () -> this.Cancel id |> ignore)

            let deadlineCts =
                match deadline with
                | Some d when d > TimeSpan.Zero ->
                    let cts = new CancellationTokenSource(d)

                    cts.Token.Register(fun () ->
                        let dx = exn (sprintf "deadline exceeded for %s" (MessageId.value id))
                        this.Fail(id, dx) |> ignore)
                    |> ignore

                    Some cts
                | _ -> None

            let cleanup =
                { new IDisposable with
                    member _.Dispose() =
                        ctRegistration.Dispose()

                        match deadlineCts with
                        | Some cts -> cts.Dispose()
                        | None -> ()
                }

            if not (entries.TryAdd(id, (tcs, cleanup))) then
                cleanup.Dispose()
                tcs.SetException(InvalidOperationException(sprintf "duplicate pending id %A" id))

            tcs.Task

        /// <summary>Resolve the entry with a value. Returns true if it was pending.</summary>
        member _.Resolve(id: MessageId, value: 'T) : bool =
            match tryRemove id with
            | Some(tcs, cleanup) ->
                cleanup.Dispose()
                tcs.TrySetResult value
            | None -> false

        /// <summary>Fail the entry with an exception. Returns true if it was pending.</summary>
        member _.Fail(id: MessageId, error: exn) : bool =
            match tryRemove id with
            | Some(tcs, cleanup) ->
                cleanup.Dispose()
                tcs.TrySetException error
            | None -> false

        /// <summary>Cancel the entry. Returns true if it was pending.</summary>
        member _.Cancel(id: MessageId) : bool =
            match tryRemove id with
            | Some(tcs, cleanup) ->
                cleanup.Dispose()
                tcs.TrySetCanceled()
            | None -> false

        /// <summary>Number of currently pending entries.</summary>
        member _.Count: int = entries.Count
