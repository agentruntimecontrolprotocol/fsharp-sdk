module ArcpSamples.LeaseExpiresAt

// Demonstrates `lease_expires_at` (§9.5). The job's lease has an
// expiry deadline; the runtime rejects operations attempted after
// `expires_at` with `LEASE_EXPIRED`.

open System
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
            let lease =
                Lease.empty
                |> Lease.withCapability Capabilities.FsRead [ "/data/**" ]
            let expires = DateTimeOffset.UtcNow.AddSeconds 2.0
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent("indexer", fun ctx ->
                            task {
                                // The first read succeeds; after the deadline the next
                                // ValidateOpAsync throws LEASE_EXPIRED.
                                do! ctx.ValidateOpAsync(Capabilities.FsRead, "/data/file.txt", ctx.CancellationToken)
                                do! Task.Delay(2500)
                                do! ctx.ValidateOpAsync(Capabilities.FsRead, "/data/late.txt", ctx.CancellationToken)
                                return jsonString "unreachable"
                            }))
                    (Set.ofList [ Features.LeaseExpiresAt ])
            let! handle = p.Client.SubmitAsync(
                { Agent = "indexer"; Input = jsonInt 0
                  LeaseRequest = Some lease
                  LeaseConstraints = Some { ExpiresAt = expires }
                  IdempotencyKey = None; MaxRuntimeSec = None },
                CancellationToken.None)
            let! r = handle.Result
            match r with
            | Error (ARCPError.LeaseExpired _) -> writeLine "got LEASE_EXPIRED as expected"
            | Error e -> writeErr (sprintf "wrong error: %s" (ARCPError.code e))
            | Ok _ -> writeErr "should not have succeeded"
            do! teardown p
            return 0
        })
