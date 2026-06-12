namespace ARCP.Client

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client.Internal

/// Client-side handle to a submitted or subscribed job.
///
/// `Events` is an `IAsyncEnumerable<JobEventBody>` over every non-
/// `result_chunk` event; consumers iterate with `await foreach`
/// (C#) or `for x in handle.Events do` (F#). `TryReadResultBytes`
/// returns the assembled bytes for a given `ResultId` once the
/// chunk stream has closed (`byte[] option`).
///
/// `Result` resolves once the terminating `job.result` / `job.error`
/// arrives.
type JobHandle
    internal
    (
        jobId: JobId,
        credentials: Credential list,
        eventChannel: Channel<JobEventBody>,
        resultTask: Task<Result<JobResultPayload, ARCPError>>,
        chunkIndex: ChunkAssemblerIndex,
        cancelDelegate: string option * CancellationToken -> Task<Result<unit, ARCPError>>
    ) =
    member _.JobId: JobId = jobId

    /// Provisioned credentials returned in `job.accepted` for the
    /// submitting client. Values are secrets; subscribers never
    /// receive this snapshot.
    member _.Credentials: Credential list = credentials

    /// Stream of `job.event` bodies as they arrive. The enumerator
    /// completes when the job terminates.
    member _.Events: IAsyncEnumerable<JobEventBody> =
        let reader = eventChannel.Reader

        { new IAsyncEnumerable<JobEventBody> with
            member _.GetAsyncEnumerator(c) =
                let mutable current = Unchecked.defaultof<JobEventBody>
                let mutable finished = false

                { new IAsyncEnumerator<JobEventBody> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        task {
                            if finished then
                                return false
                            else
                                try
                                    let! has = reader.WaitToReadAsync(c).AsTask()

                                    if not has then
                                        finished <- true
                                        return false
                                    else
                                        let success, e = reader.TryRead()

                                        if success then
                                            current <- e
                                            return true
                                        else
                                            finished <- true
                                            return false
                                with :? OperationCanceledException ->
                                    return false
                        }
                        |> ValueTask<bool>

                    member _.DisposeAsync() = ValueTask.CompletedTask
                }
        }

    /// Completes with the terminal `job.result` / `job.error`.
    member _.Result: Task<Result<JobResultPayload, ARCPError>> = resultTask

    /// If the result was emitted via `result_chunk` events, return
    /// the assembled bytes once the job has completed. Returns
    /// `None` when the job's final result was inline.
    member _.TryReadResultBytes(resultId: ResultId) : byte[] option =
        chunkIndex.TryGet resultId.Value
        |> Option.bind (fun a -> if a.IsClosed then Some(a.ToArray()) else None)

    /// Send `job.cancel` for this job (spec §7.4). Only authorised
    /// for the submitting session — subscribers receive
    /// `PERMISSION_DENIED`.
    member _.CancelAsync(reason: string option, ct: CancellationToken) : Task<Result<unit, ARCPError>> =
        cancelDelegate (reason, ct)

[<AutoOpen>]
module internal JobHandleInternal =
    /// Internal constructor surface used by `ArcpClient` to feed events
    /// and resolve the result. Not part of the public API.
    type JobHandleWriter =
        {
            Channel: Channel<JobEventBody>
            ChunkIndex: ChunkAssemblerIndex
            ResultSetter: TaskCompletionSource<Result<JobResultPayload, ARCPError>>
        }

    let internal mkHandle
        (jobId: JobId)
        (credentials: Credential list)
        (cancelDelegate: string option * CancellationToken -> Task<Result<unit, ARCPError>>)
        : JobHandle * JobHandleWriter =
        let channel = Channel.CreateUnbounded<JobEventBody>()
        let chunks = ChunkAssemblerIndex()

        let tcs =
            TaskCompletionSource<Result<JobResultPayload, ARCPError>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let handle =
            JobHandle(jobId, credentials, channel, tcs.Task, chunks, cancelDelegate)

        let writer =
            {
                Channel = channel
                ChunkIndex = chunks
                ResultSetter = tcs
            }

        handle, writer
