module ARCP.IntegrationTests.LeaseExpiryTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``submit with past expires_at returns INVALID_REQUEST`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("a", fun _ -> task { return Json.serializeToElement<int> 0 }))
                Features.All

        let req =
            { mkRequest "a" with
                LeaseConstraints =
                    Some
                        {
                            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10.0)
                        }
            }

        let! threw =
            task {
                try
                    let! _ = p.Client.SubmitAsync(req, CancellationToken.None)
                    return None
                with :? ArcpException as ax ->
                    return Some ax.Error
            }

        match threw with
        | Some(ARCPError.InvalidRequest _) -> ()
        | other -> failwithf "expected InvalidRequest, got %A" other

        do! teardown p
    }

[<Fact>]
let ``ValidateOpAsync after expires_at raises LEASE_EXPIRED`` () =
    task {
        let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/data/**" ]

        let! p =
            connect
                (fun s ->
                    s.RegisterAgent(
                        "indexer",
                        fun ctx ->
                            task {
                                // Poll until expires_at passes; the first post-expiry
                                // call raises LEASE_EXPIRED and fails the job. Avoids
                                // racing a fixed delay against the expiry window.
                                while true do
                                    do! ctx.ValidateOpAsync(Capabilities.FsRead, "/data/file", ctx.CancellationToken)
                                    do! Task.Delay(25)

                                return Json.serializeToElement<int> 0
                            }
                    ))
                (Set.singleton Features.LeaseExpiresAt)

        let req =
            { mkRequest "indexer" with
                LeaseRequest = Some lease
                LeaseConstraints =
                    Some
                        {
                            // Far enough out that submit-time validation can't see an
                            // already-expired lease on a slow CI runner.
                            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1.0)
                        }
            }

        let! handle = p.Client.SubmitAsync(req, CancellationToken.None)
        let! r = handle.Result

        match r with
        | Error(ARCPError.LeaseExpired _) -> ()
        | other -> failwithf "expected LeaseExpired, got %A" other

        do! teardown p
    }
