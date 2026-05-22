module ARCP.UnitTests.CredentialTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core

let private credential =
    {
        Id = "cred_test"
        Scheme = "bearer"
        Value = "sk-test"
        Endpoint = "https://llm.example.test"
        Profile = Some "fast"
        Constraints =
            Some
                {
                    CostBudget = Some [ "USD:1.00" ]
                    ModelUse = Some [ "tier-fast/*" ]
                    ExpiresAt = Some(DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
                }
    }

[<Fact>]
let ``Credential round-trips through Json options`` () =
    let wire = Json.serialize credential
    let decoded = Json.deserialize<Credential> wire
    decoded |> should equal credential

[<Fact>]
let ``JobAcceptedPayload omits credentials when None`` () =
    let accepted: JobAcceptedPayload =
        {
            JobId = "job_test"
            Lease = Lease.empty
            LeaseConstraints = None
            Budget = None
            Credentials = None
            AcceptedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            TraceId = None
        }

    (Json.serialize accepted).Contains("\"credentials\"") |> should equal false

[<Fact>]
let ``JobAcceptedPayload includes credentials when Some`` () =
    let accepted: JobAcceptedPayload =
        {
            JobId = "job_test"
            Lease = Lease.empty
            LeaseConstraints = None
            Budget = None
            Credentials = Some [ credential ]
            AcceptedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            TraceId = None
        }

    let wire = Json.serialize accepted
    wire.Contains("\"credentials\"") |> should equal true
    wire.Contains("\"sk-test\"") |> should equal true
