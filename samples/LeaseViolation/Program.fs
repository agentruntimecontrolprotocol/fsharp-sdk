module ArcpSamples.LeaseViolation

// Demonstrates `PERMISSION_DENIED` (§9.3) when an op falls outside
// the lease's glob map.

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
                |> Lease.withCapability Capabilities.FsRead [ "/allowed/**" ]
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent("bad-actor", fun ctx ->
                            task {
                                // Try to read outside the lease.
                                do! ctx.ValidateOpAsync(Capabilities.FsRead, "/secret/data", ctx.CancellationToken)
                                return jsonString "unreachable"
                            }))
                    Features.All
            let! handle = p.Client.SubmitAsync(
                { Agent = "bad-actor"; Input = jsonInt 0
                  LeaseRequest = Some lease
                  LeaseConstraints = None
                  IdempotencyKey = None; MaxRuntimeSec = None },
                CancellationToken.None)
            let! r = handle.Result
            match r with
            | Error (ARCPError.PermissionDenied(m, _)) -> writeLine (sprintf "denied: %s" m)
            | Error e -> writeErr (ARCPError.code e)
            | Ok _ -> writeErr "should have failed"
            do! teardown p
            return 0
        })
