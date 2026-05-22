module ArcpSamples.AgentVersions

// Demonstrates `agent_versions` (§7.5). Multiple versions of one
// agent name are registered; the client pins a version via
// `name@version` syntax.

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgentVersion("refactor", "1.0.0", fun _ -> task { return jsonString "v1 result" })
                        s.RegisterAgentVersion("refactor", "2.0.0", fun _ -> task { return jsonString "v2 result" })
                        s.SetDefaultAgentVersion("refactor", "2.0.0"))
                    (Set.ofList [ Features.AgentVersions ])

            // Pin an agent implementation version explicitly. This is
            // unrelated to the ARCP protocol version.
            let! h1 =
                p.Client.SubmitAsync(
                    {
                        Agent = "refactor@1.0.0"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            let! r1 = h1.Result
            writeLine (sprintf "pinned v1.0.0 -> %A" (r1 |> Result.map (fun p -> p.Result)))

            // Bare name -> default (2.0.0).
            let! h2 =
                p.Client.SubmitAsync(
                    {
                        Agent = "refactor"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            let! r2 = h2.Result
            writeLine (sprintf "default -> %A" (r2 |> Result.map (fun p -> p.Result)))

            do! teardown p
            return 0
        })
