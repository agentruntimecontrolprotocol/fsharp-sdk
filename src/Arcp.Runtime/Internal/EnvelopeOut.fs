namespace ARCP.Runtime.Internal

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime

/// Outbound envelope helpers. Each function takes the `sessions`
/// map and any per-message context; nothing closes over class
/// state.
[<RequireQualifiedAccess>]
module internal EnvelopeOut =

    /// Send `env` over the transport belonging to `sid`. If
    /// `attachSeq` is true the envelope is recorded in the event
    /// log first (which assigns its `event_seq`).
    let pushEnvelope
        (sessions: ConcurrentDictionary<string, ServerSessionContext>)
        (sid: SessionId)
        (env: Envelope)
        (attachSeq: bool)
        : Task =
        task {
            match sessions.TryGetValue sid.Value with
            | true, sctx ->
                let envOut =
                    if attachSeq then
                        let entry = sctx.EventLog.Append(sid, env)
                        entry.Envelope
                    else
                        Envelope.withSessionId sid env

                do! sctx.Transport.SendAsync(envOut, CancellationToken.None)
            | _ -> ()
        }
        :> Task

    let pushJobEvent
        (sessions: ConcurrentDictionary<string, ServerSessionContext>)
        (timeProvider: TimeProvider)
        (sid: SessionId)
        (jobId: JobId)
        (body: JobEventBody)
        : Task =
        let payload: JobEventPayload =
            {
                Kind = JobEventBody.kind body
                Ts = timeProvider.GetUtcNow()
                Body = body
            }

        let env =
            Message.JobEvent payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid

        pushEnvelope sessions sid env true

    let pushJobResult
        (sessions: ConcurrentDictionary<string, ServerSessionContext>)
        (sid: SessionId)
        (jobId: JobId)
        (payload: JobResultPayload)
        : Task =
        let env =
            Message.JobResult payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid

        pushEnvelope sessions sid env true

    let pushJobError
        (sessions: ConcurrentDictionary<string, ServerSessionContext>)
        (sid: SessionId)
        (jobId: JobId)
        (payload: JobErrorPayload)
        : Task =
        let env =
            Message.JobError payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid

        pushEnvelope sessions sid env true

    /// Send a `session.error` correlated to a request id.
    let respondWithError
        (ctx: ServerSessionContext)
        (requestId: string)
        (err: ARCPError)
        (ct: CancellationToken)
        : Task =
        let payload: SessionErrorPayload =
            {
                Code = ARCPError.code err
                Message = ARCPError.message err
                Retryable = ARCPError.retryable err
                Details = ARCPError.details err
            }

        let env =
            Message.SessionError payload
            |> Codec.toEnvelope
            |> Envelope.withId requestId
            |> Envelope.withSessionId ctx.SessionId

        ctx.Transport.SendAsync(env, ct)
