namespace ARCP.Runtime

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Registry
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime.Session

/// <summary>Configuration for an ARCP <see cref="Runtime"/>.</summary>
type RuntimeOptions =
    {
        /// <summary>Runtime identity advertised in <c>session.accepted</c>.</summary>
        RuntimeIdentity: RuntimeIdentity
        /// <summary>Server-offered capability set; the negotiated set is the intersection with the client request.</summary>
        OfferedCapabilities: Capabilities
    }

[<RequireQualifiedAccess>]
module RuntimeOptions =
    /// <summary>Reasonable defaults: identity-only, no optional capabilities.</summary>
    let defaults: RuntimeOptions =
        {
            RuntimeIdentity =
                {
                    Kind = "arcp-fsharp"
                    Version = Version.Sdk
                    Fingerprint = None
                    TrustLevel = None
                }
            OfferedCapabilities = Capabilities.empty
        }

/// <summary>
/// ARCP runtime (RFC §9). Phase 2 implements the session handshake and
/// drains subsequent messages with <c>nack UNIMPLEMENTED</c>; later phases
/// will route them to tool/job/stream subsystems.
/// </summary>
type Runtime(transport: ITransport, validator: IAuthValidator, logger: ILogger, options: RuntimeOptions) =

    let cts = new CancellationTokenSource()

    let send (env: Envelope<MessageType>) (ct: CancellationToken) =
        task { do! transport.SendAsync(env, ct) }

    let nack (correlationId: MessageId) (code: string) (message: string) : Envelope<MessageType> =
        Envelopes.nack
            {
                Code = code
                Message = message
                Details = None
            }
        |> Envelope.withCorrelation correlationId

    let rejectAndClose (code: string) (reason: string) (ct: CancellationToken) =
        task {
            logger.LogWarning("session rejected: {Code} {Reason}", code, reason)

            let env = Envelopes.sessionRejected { Code = code; Reason = Some reason }

            do! send env ct
            return Closed reason
        }

    let handleOpen (env: Envelope<MessageType>) (payload: SessionOpen) (ct: CancellationToken) =
        task {
            let schemeOpt =
                match payload.Auth.Scheme with
                | "bearer" -> payload.Auth.Token |> Option.map Bearer
                | "signed_jwt" -> payload.Auth.Token |> Option.map Jwt
                | "none" -> Some Anonymous
                | _ -> None

            match schemeOpt with
            | None ->
                let! st = rejectAndClose "UNAUTHENTICATED" (sprintf "unsupported scheme %s" payload.Auth.Scheme) ct
                return st
            | Some scheme ->
                let! result = validator.ValidateAsync(scheme, ct)

                match result with
                | Error err ->
                    let! st = rejectAndClose (ARCPError.code err) (ARCPError.message err) ct
                    return st
                | Ok authResult ->
                    if scheme = Anonymous && not options.OfferedCapabilities.Anonymous then
                        let! st = rejectAndClose "UNAUTHENTICATED" "anonymous sessions not permitted" ct
                        return st
                    else
                        match negotiate payload.Capabilities options.OfferedCapabilities with
                        | Error missing ->
                            let! st = rejectAndClose "UNIMPLEMENTED" (sprintf "unsupported capabilities: %s" missing) ct

                            return st
                        | Ok negotiated ->
                            let sid = SessionId.create ()

                            let acceptedEnv =
                                Envelopes.sessionAccepted
                                    {
                                        SessionId = sid
                                        Runtime = options.RuntimeIdentity
                                        Capabilities = negotiated
                                        Lease = None
                                    }
                                |> Envelope.withCorrelation env.Id
                                |> Envelope.withSession sid

                            do! send acceptedEnv ct
                            return Authenticated(authResult.Principal, sid, negotiated, None)
        }

    let handleAuthenticated (env: Envelope<MessageType>) (ct: CancellationToken) =
        task {
            match env.Payload with
            | Ping p ->
                let pong = Envelopes.pong { Nonce = p.Nonce } |> Envelope.withCorrelation env.Id
                do! send pong ct
                return ()
            | SessionClose _ ->
                logger.LogInformation("session closed by peer")
                return ()
            | _ ->
                let n =
                    nack env.Id "UNIMPLEMENTED" (sprintf "%s not implemented in phase 2" env.Type)

                do! send n ct
                return ()
        }

    let loop (ct: CancellationToken) : Task<unit> =
        task {
            let mutable state = Unauthenticated
            let mutable running = true

            while running && not ct.IsCancellationRequested do
                let! incoming = transport.ReceiveAsync ct

                match incoming with
                | None -> running <- false
                | Some env ->
                    match state with
                    | Unauthenticated ->
                        match env.Payload with
                        | SessionOpen openPayload ->
                            let! next = handleOpen env openPayload ct
                            state <- next

                            match next with
                            | Closed _ -> running <- false
                            | _ -> ()
                        | _ -> logger.LogWarning("dropping {Type} before session.open", env.Type)
                    | AwaitingAuthenticate _ ->
                        // Phase 2 does not yet require challenge/response; treat as misuse.
                        let! st = rejectAndClose "FAILED_PRECONDITION" "challenge flow not implemented" ct
                        state <- st
                        running <- false
                    | Authenticated _ ->
                        do! handleAuthenticated env ct

                        match env.Payload with
                        | SessionClose _ -> running <- false
                        | _ -> ()
                    | Closed _ -> running <- false

            return ()
        }

    /// <summary>Begin dispatching. The returned task completes when the loop exits.</summary>
    member _.StartAsync(ct: CancellationToken) : Task =
        let linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token)
        loop linked.Token :> Task

    /// <summary>Request cancellation; the receive loop will exit on the next iteration.</summary>
    member _.StopAsync() : Task =
        cts.Cancel()
        Task.CompletedTask

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            ValueTask(
                task {
                    cts.Cancel()
                    do! transport.DisposeAsync()
                    cts.Dispose()
                }
            )
