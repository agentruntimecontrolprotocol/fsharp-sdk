namespace ARCP.Runtime.Internal

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime

/// Spin up an agent on its own task. Captures the agent's outcome
/// into a terminal `job.result` / `job.error`, including translation
/// of `OperationCanceledException` and `ArcpException`.
[<RequireQualifiedAccess>]
module internal JobLauncher =

    let private buildSuccess (result: JsonElement) : JobResultPayload =
        {
            FinalStatus = JobStatus.Success
            Result = Some result
            ResultId = None
            ResultSize = None
            Summary = None
        }

    /// Terminal `job.result` for a streamed result (§8.4): carries
    /// `result_id` and omits the inline result.
    let private buildStreamedSuccess (resultId: string) : JobResultPayload =
        {
            FinalStatus = JobStatus.Success
            Result = None
            ResultId = Some resultId
            ResultSize = None
            Summary = None
        }

    /// True when the handler returned a meaningful inline result (i.e.
    /// not JSON `null`/`undefined`).
    let private hasInlineResult (result: JsonElement) : bool =
        result.ValueKind <> JsonValueKind.Null
        && result.ValueKind <> JsonValueKind.Undefined

    let private buildCancelled () : JobErrorPayload =
        {
            FinalStatus = JobStatus.Cancelled
            Code = "CANCELLED"
            Message = "Job cancelled"
            Retryable = false
            Details = None
        }

    let private buildError (e: ARCPError) : JobErrorPayload =
        {
            FinalStatus = JobStatus.Error
            Code = ARCPError.code e
            Message = ARCPError.message e
            Retryable = ARCPError.retryable e
            Details = ARCPError.details e
        }

    let private buildInternal (ex: exn) : JobErrorPayload =
        {
            FinalStatus = JobStatus.Error
            Code = "INTERNAL_ERROR"
            Message = ex.Message
            Retryable = true
            Details = None
        }

    /// Run `handler` against `record`. Background-task; never throws.
    let launch
        (jobs: JobManager)
        (credentialRegistry: CredentialRegistry)
        (timeProvider: TimeProvider)
        (record: JobRecord)
        (handler: ArcpAgentHandler)
        : unit =
        let onCostMetric (currency: string, amount: decimal) =
            record.Budgets.TryDecrement(currency, amount) |> ignore

        let emit (body: JobEventBody) : Task = jobs.EmitEventAsync(record, body)

        let rotateCredential (credentialId: string, newValue: string, ct: CancellationToken) : Task =
            task {
                // §14: the new value goes only to the submitting session;
                // subscribers receive a redacted status (id only).
                do! jobs.EmitCredentialRotatedAsync(record, credentialId, newValue)
                do! credentialRegistry.RevokeCredentialAsync(credentialId, ct)
            }
            :> Task

        let beginStream () : ResultId =
            let id = ResultId.newId ()
            record.StreamResultId <- Some id.Value
            id

        let context =
            JobContext(
                record.JobId,
                record.SessionId,
                record.ParentJobId |> Option.map JobId.ofString,
                record.Input,
                record.Lease,
                record.Constraints,
                record.Credentials,
                record.Budgets,
                timeProvider,
                record.Cancellation.Token,
                emit,
                rotateCredential,
                beginStream,
                onCostMetric
            )

        record.Status <- JobStatus.Running

        Task.Run(fun () ->
            task {
                try
                    let! result = handler context

                    match record.StreamResultId with
                    | Some resultId when hasInlineResult result ->
                        // §8.4: mixing inline result and result_chunk is
                        // forbidden.
                        do!
                            jobs.EmitErrorAsync(
                                record,
                                buildInternal (
                                    InvalidOperationException(
                                        "Agent returned an inline result after streaming result_chunk events; mixing is forbidden (§8.4)."
                                    )
                                )
                            )
                    | Some resultId -> do! jobs.EmitResultAsync(record, buildStreamedSuccess resultId)
                    | None -> do! jobs.EmitResultAsync(record, buildSuccess result)
                with
                | :? OperationCanceledException -> do! jobs.EmitErrorAsync(record, buildCancelled ())
                | :? ArcpException as ax -> do! jobs.EmitErrorAsync(record, buildError ax.Error)
                | ex -> do! jobs.EmitErrorAsync(record, buildInternal ex)

                try
                    do! credentialRegistry.RevokeJobAsync(record.JobId, CancellationToken.None)
                with _ ->
                    ()
            }
            :> Task)
        |> ignore
