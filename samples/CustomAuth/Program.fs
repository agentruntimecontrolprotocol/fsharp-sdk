module ArcpSamples.CustomAuth

// Demonstrates a custom `IBearerVerifier`. Production deployments
// plug their own auth here (JWT, OAuth, mTLS, etc.).

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth
open ArcpSamples.SampleHarness

type FixedBearerVerifier(expected: string) =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                if token = expected then
                    return Ok(StringPrincipal "alice" :> IPrincipal)
                else
                    return Error(ARCPError.Unauthenticated "Bad token")
            }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let cts = new System.Threading.CancellationTokenSource()

            let server =
                ArcpServer(
                    { ArcpServerOptions.defaults with
                        BearerVerifier = FixedBearerVerifier "secret" :> IBearerVerifier
                        Features = Features.All
                    }
                )

            server.RegisterAgent("hello", echoAgent)
            let clientT, serverT = MemoryTransport.CreatePair()
            let serverTask = server.HandleSessionAsync(serverT, cts.Token)

            let client =
                new ArcpClient(
                    clientT,
                    { ArcpClientOptions.defaults with
                        Auth = AuthScheme.Bearer "secret"
                        Features = Features.All
                    }
                )

            let! ctx = client.ConnectAsync CancellationToken.None
            writeLine (sprintf "authenticated; session_id=%s" ctx.SessionId.Value)
            do! client.CloseAsync(None, CancellationToken.None)

            try
                cts.Cancel()
            with _ ->
                ()

            return 0
        })
