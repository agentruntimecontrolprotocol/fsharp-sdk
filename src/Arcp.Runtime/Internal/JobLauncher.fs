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

    let private buildSuccess (result: JsonElement) : JobResultPayload = {
        FinalStatus = JobStatus.Success
        Result = Some result
        ResultId = None
        ResultSize = None
        Summary = None
    }

    let private buildCancelled () : JobResultPayload = {
        FinalStatus = JobStatus.Cancelled
        Result = None
        ResultId = None
        ResultSize = None
        Summary = Some "cancelled"
    }

    let private buildError (e: ARCPError) : JobErrorPayload = {
        FinalStatus = JobStatus.Error
        Code = ARCPError.code e
        Message = ARCPError.message e
        Retryable = ARCPError.retryable e
        Details = ARCPError.details e
    }

    let private buildInternal (ex: exn) : JobErrorPayload = {
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
                let message =
                    Json.serialize {| id = credentialId; value = newValue |}
                do! emit (JobEventBody.Status(StatusPhases.CredentialRotated, Some message))
                do! credentialRegistry.RevokeCredentialAsync(credentialId, ct)
            } :> Task
        let beginStream () : ResultId = ResultId.newId ()
        let context =
            JobContext(
                record.JobId,
                record.SessionId,
                record.Lease,
                record.Constraints,
                record.Budgets,
                timeProvider,
                record.Cancellation.Token,
                emit,
                rotateCredential,
                beginStream,
                onCostMetric)
        record.Status <- JobStatus.Running
        Task.Run(fun () ->
            task {
                try
                    let! result = handler context
                    do! jobs.EmitResultAsync(record, buildSuccess result)
                with
                | :? OperationCanceledException ->
                    do! jobs.EmitResultAsync(record, buildCancelled ())
                | :? ArcpException as ax ->
                    do! jobs.EmitErrorAsync(record, buildError ax.Error)
                | ex ->
                    do! jobs.EmitErrorAsync(record, buildInternal ex)
                try
                    do! credentialRegistry.RevokeJobAsync(record.JobId, CancellationToken.None)
                with _ -> ()
            } :> Task) |> ignore
