namespace ARCP.Runtime.Internal

open System
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ARCP.Runtime.Auth
open ARCP.Runtime.Store

/// Handle a `session.hello` arrival: authenticate the principal,
/// compute the negotiated feature set, mint a session id, write
/// `session.welcome`, and return the constructed server-side
/// `ServerSessionContext`.
[<RequireQualifiedAccess>]
module internal SessionHandshake =

    let private authenticateAsync
            (verifier: IBearerVerifier)
            (auth: AuthPayload)
            (ct: CancellationToken)
            : Task<Result<IPrincipal, ARCPError>> =
        task {
            match auth.Scheme with
            | "bearer" ->
                match auth.Token with
                | Some t -> return! verifier.VerifyAsync(t, ct)
                | None -> return Error (ARCPError.Unauthenticated "Missing bearer token")
            | "none" ->
                return Ok (AnonymousPrincipal() :> IPrincipal)
            | other ->
                return Error (ARCPError.Unauthenticated (sprintf "Unsupported auth scheme: %s" other))
        }

    let private writeAuthError
            (transport: ITransport)
            (requestId: string)
            (err: ARCPError)
            (ct: CancellationToken)
            : Task =
        let payload: SessionErrorPayload = {
            Code = ARCPError.code err
            Message = ARCPError.message err
            Retryable = ARCPError.retryable err
            Details = ARCPError.details err
        }
        let envOut =
            Message.SessionError payload
            |> Codec.toEnvelope
            |> Envelope.withId requestId
        transport.SendAsync(envOut, ct)

    let private buildWelcome
            (runtime: RuntimeIdentity)
            (resumeWindow: int)
            (heartbeat: int option)
            (negotiated: Set<string>)
            (resumeToken: string)
            (agents: AgentInventory)
            : SessionWelcomePayload =
        {
            Runtime = runtime
            ResumeToken = resumeToken
            ResumeWindowSec = resumeWindow
            HeartbeatIntervalSec = heartbeat
            Capabilities = {
                Encodings = [ "json" ]
                Features = negotiated
                Agents = agents
            }
        }

    /// Returns `Some ctx` on a successful handshake, `None` if
    /// authentication failed (in which case `session.error` has
    /// already been written).
    let handleAsync
            (transport: ITransport)
            (runtime: RuntimeIdentity)
            (verifier: IBearerVerifier)
            (timeProvider: TimeProvider)
            (eventLog: EventLog)
            (supportedFeatures: Set<string>)
            (heartbeatIntervalSec: int)
            (resumeWindowSec: int)
            (inventory: AgentInventoryStore)
            (requestId: string)
            (hello: SessionHelloPayload)
            (ct: CancellationToken)
            : Task<ServerSessionContext option> =
        task {
            let! authResult = authenticateAsync verifier hello.Auth ct
            match authResult with
            | Error err ->
                do! writeAuthError transport requestId err ct
                return None
            | Ok principal ->
                let negotiated = Features.intersect hello.Capabilities.Features supportedFeatures
                let agents =
                    if negotiated.Contains Features.AgentVersions then
                        AgentInventory.Rich (inventory.ToRichInventory())
                    else
                        AgentInventory.Flat (inventory.ToFlatInventory())
                let sid = SessionId.newId ()
                let resumeToken = (MessageId.newId ()).Value
                let heartbeat =
                    if negotiated.Contains Features.Heartbeat then Some heartbeatIntervalSec
                    else None
                let welcome =
                    buildWelcome runtime resumeWindowSec heartbeat negotiated resumeToken agents
                let envOut =
                    Message.SessionWelcome welcome
                    |> Codec.toEnvelope
                    |> Envelope.withSessionId sid
                    |> Envelope.withId requestId
                do! transport.SendAsync(envOut, ct)
                let ctx =
                    ServerSessionContext.create
                        sid principal negotiated heartbeat resumeToken resumeWindowSec
                        transport eventLog (timeProvider.GetUtcNow())
                return Some ctx
        }
