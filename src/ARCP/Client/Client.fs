namespace ARCP.Client

open System.Threading
open System.Threading.Tasks
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Registry
open ARCP.Auth.Auth
open ARCP.Transport

/// <summary>
/// Minimal ARCP client that drives the session handshake (RFC §9). Phase 2
/// supports <see cref="OpenAsync"/>; <see cref="InvokeAsync"/> is a stub.
/// </summary>
type Client(transport: ITransport, scheme: AuthScheme) =

    let buildAuthBlock () : AuthBlock =
        match scheme with
        | Bearer t ->
            {
                Scheme = "bearer"
                Token = Some t
                Fingerprint = None
            }
        | Jwt t ->
            {
                Scheme = "signed_jwt"
                Token = Some t
                Fingerprint = None
            }
        | Anonymous ->
            {
                Scheme = "none"
                Token = None
                Fingerprint = None
            }

    /// <summary>
    /// Perform <c>session.open</c> and await <c>session.accepted</c>. Any
    /// other terminal envelope (<c>session.rejected</c>,
    /// <c>session.unauthenticated</c>, or a closed transport) becomes an
    /// <see cref="ARCPError"/>.
    /// </summary>
    member _.OpenAsync(capabilities: Capabilities, ct: CancellationToken) : Task<Result<SessionId, ARCPError>> =
        task {
            let openPayload: SessionOpen =
                {
                    Arcp = Version.Protocol
                    Client =
                        {
                            Kind = "arcp-fsharp-client"
                            Version = Version.Sdk
                            Fingerprint = None
                            Principal = None
                        }
                    Auth = buildAuthBlock ()
                    Capabilities = capabilities
                }

            let env = Envelopes.sessionOpen openPayload
            do! transport.SendAsync(env, ct)
            let! reply = transport.ReceiveAsync ct

            match reply with
            | None -> return Error(Unavailable "transport closed before handshake response")
            | Some r ->
                match r.Payload with
                | SessionAccepted accepted -> return Ok accepted.SessionId
                | SessionRejected rej ->
                    return Error(Unauthenticated(sprintf "%s: %s" rej.Code (rej.Reason |> Option.defaultValue "")))
                | SessionUnauthenticated rej ->
                    return Error(Unauthenticated(sprintf "%s: %s" rej.Code (rej.Reason |> Option.defaultValue "")))
                | other -> return Error(InvalidArgument("envelope.type", sprintf "unexpected %s" r.Type))
        }

    /// <summary>Invoke a tool. Phase 2 stub.</summary>
    member _.InvokeAsync
        (_tool: string, _args: System.Text.Json.JsonElement, _ct: CancellationToken)
        : Task<Result<unit, ARCPError>> =
        task { return Error(Unimplemented "client.InvokeAsync is a phase 2 stub") }

    interface System.IAsyncDisposable with
        member _.DisposeAsync() = transport.DisposeAsync()
