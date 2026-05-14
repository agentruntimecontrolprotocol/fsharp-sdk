module ArcpSamples.Delegate

// Demonstrates a `delegate` event (§10). The parent agent reports
// a child sub-job via a `delegate` event body. The runtime in this
// SDK does not auto-spawn — the agent expresses delegation by
// emitting the event itself.

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
            let parentLease =
                Lease.empty
                |> Lease.withCapability Capabilities.FsRead [ "/data/**" ]
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent("parent", fun ctx ->
                            task {
                                let body = {
                                    DelegateBody.ChildJobId = (JobId.newId()).Value
                                    Agent = "child"
                                    Lease = parentLease
                                    LeaseConstraints = None
                                }
                                let _ = body
                                do! ctx.EmitToolCallAsync("child.invoke", jsonString "args", "c1", ctx.CancellationToken)
                                return jsonString "delegated"
                            }))
                    Features.All
            let! handle = p.Client.SubmitAsync(
                { Agent = "parent"; Input = jsonInt 0
                  LeaseRequest = Some parentLease
                  LeaseConstraints = None
                  IdempotencyKey = None; MaxRuntimeSec = None },
                CancellationToken.None)
            let! _ = handle.Result
            writeLine "parent finished"
            do! teardown p
            return 0
        })
