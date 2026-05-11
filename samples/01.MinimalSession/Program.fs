module ARCP.Samples.MinimalSession.Program

open System.Threading
open Microsoft.Extensions.Logging.Abstractions
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

/// <summary>
/// Sample 01 — open a session against an in-process runtime, print the
/// negotiated capabilities and runtime identity, then close. No tools
/// are invoked. Exercises RFC §9 (session handshake) end-to-end.
/// </summary>
[<EntryPoint>]
let main _argv =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator

        let opts =
            { RuntimeOptions.defaults with
                OfferedCapabilities =
                    { Capabilities.empty with
                        HumanInput = true
                        Subscriptions = true
                        Artifacts = true
                    }
                RuntimeIdentity =
                    {
                        Kind = "arcp-fsharp-sample"
                        Version = ARCP.Version.Sdk
                        Fingerprint = None
                        TrustLevel = Some "sample"
                    }
            }

        let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
        let _ = runtime.StartAsync CancellationToken.None

        let client = new Client(clientT, Bearer "secret")

        let requested =
            { Capabilities.empty with
                HumanInput = true
                Subscriptions = true
            }

        let! result = client.OpenAsync(requested, CancellationToken.None)

        match result with
        | Ok sid ->
            printfn "session opened: %s" (SessionId.value sid)
            printfn "runtime kind=%s version=%s" opts.RuntimeIdentity.Kind opts.RuntimeIdentity.Version
            printfn "offered human_input=%b subscriptions=%b artifacts=%b" true true true
            printfn "requested human_input=%b subscriptions=%b" requested.HumanInput requested.Subscriptions
            do! runtime.StopAsync()
            do! (runtime :> System.IAsyncDisposable).DisposeAsync()
            do! (client :> System.IAsyncDisposable).DisposeAsync()
            return 0
        | Error e ->
            eprintfn "session open failed: %A" e
            do! runtime.StopAsync()
            return 1
    }
    |> fun t -> t.GetAwaiter().GetResult()
