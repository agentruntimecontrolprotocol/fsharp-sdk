module ARCP.IntegrationTests.PermissionLeaseTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Time.Testing
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Permissions
open ARCP.Messages.Registry
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private jsonString (s: string) : JsonElement =
    JsonSerializer.SerializeToElement<string>(s)

let private startPair (timeProvider: TimeProvider) =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            TimeProvider = timeProvider
            LeaseSweepInterval = TimeSpan.FromMilliseconds 50.0
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

[<Fact>]
let ``permission grant -> lease created in LeaseManager`` () =
    task {
        let runtime, client = startPair TimeProvider.System

        client.PermissionHandler <- Some(AlwaysAllowPermissionHandler())

        let leaseIdHolder = ref (LeaseId.ofString "")

        runtime.RegisterTool(
            "doit",
            fun (ctx: ToolContext) _ ->
                task {
                    let! result =
                        ctx.RequestPermissionAsync(
                            ("fs.write", "/tmp/x", "append", None, Some 60, ctx.CancellationToken)
                        )

                    match result with
                    | Ok lease ->
                        leaseIdHolder.Value <- lease.LeaseId
                        return Ok(jsonString (LeaseId.value lease.LeaseId))
                    | Error e -> return Error e
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("doit", jsonString "go")

        match result with
        | Ok(Some _) -> ()
        | other -> failwithf "expected lease grant, got %A" other

        // The LeaseManager should now hold a valid lease.
        match runtime.LeaseManager.CheckAsync leaseIdHolder.Value with
        | Ok lease -> Assert.False(lease.Revoked)
        | Error e -> failwithf "expected lease ok, got %A" e

        do! runtime.StopAsync()
    }

[<Fact>]
let ``permission deny -> tool gets PermissionDenied`` () =
    task {
        let runtime, client = startPair TimeProvider.System

        let denyHandler =
            { new IPermissionHandler with
                member _.HandleAsync(_p, _r, _o, _reason, _ls, _ct) = task { return Deny(Some "nope") }
            }

        client.PermissionHandler <- Some denyHandler

        runtime.RegisterTool(
            "doit",
            fun (ctx: ToolContext) _ ->
                task {
                    let! result =
                        ctx.RequestPermissionAsync(
                            ("fs.write", "/tmp/x", "append", None, Some 60, ctx.CancellationToken)
                        )

                    match result with
                    | Ok _ -> return Ok(jsonString "granted")
                    | Error e -> return Error e
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("doit", jsonString "go")

        match result with
        | Error(ARCPError.PermissionDenied _) -> ()
        | other -> failwithf "expected PermissionDenied, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``lease.refresh extends ExpiresAt`` () =
    task {
        let sent = List<Envelope<MessageType>>()

        let send env =
            lock sent (fun () -> sent.Add env)
            Task.CompletedTask

        let mgr = new LeaseManager(TimeProvider.System, send, TimeSpan.FromHours 1.0)

        let! lease = mgr.GrantAsync("fs.write", "/tmp/x", "append", "alice", 60, None)

        let originalExpiry = lease.ExpiresAt

        let! result = mgr.ExtendAsync(lease.LeaseId, 120)

        match result with
        | Ok extended -> Assert.True(extended.ExpiresAt > originalExpiry)
        | Error e -> failwithf "expected Ok, got %A" e

        match mgr.CheckAsync lease.LeaseId with
        | Ok l -> Assert.True(l.ExpiresAt > originalExpiry)
        | Error e -> failwithf "expected Ok lease, got %A" e

        (mgr :> IDisposable).Dispose()
    }

[<Fact>]
let ``runtime revokes lease -> CheckAsync returns LeaseRevoked`` () =
    task {
        let sent = List<Envelope<MessageType>>()

        let send env =
            lock sent (fun () -> sent.Add env)
            Task.CompletedTask

        let mgr = new LeaseManager(TimeProvider.System, send, TimeSpan.FromHours 1.0)

        let! lease = mgr.GrantAsync("fs.write", "/tmp/x", "append", "alice", 60, None)

        let! revoked = mgr.RevokeAsync(lease.LeaseId, "policy")

        match revoked with
        | Ok() -> ()
        | Error e -> failwithf "expected Ok revoke, got %A" e

        match mgr.CheckAsync lease.LeaseId with
        | Error(ARCPError.LeaseRevoked(_, reason)) -> Assert.Equal("policy", reason)
        | other -> failwithf "expected LeaseRevoked, got %A" other

        (mgr :> IDisposable).Dispose()
    }

[<Fact>]
let ``lease expiry via fake time provider -> sweeper emits lease.revoked`` () =
    task {
        let fake = FakeTimeProvider(DateTimeOffset.UtcNow)

        let sent = List<Envelope<MessageType>>()

        let send env =
            lock sent (fun () -> sent.Add env)
            Task.CompletedTask

        let mgr = new LeaseManager(fake, send, TimeSpan.FromMilliseconds 100.0)

        let! lease = mgr.GrantAsync("fs.write", "/tmp/x", "append", "alice", 10, None)

        // Advance past expiry, then run the sweep directly to keep the test
        // deterministic regardless of FakeTimeProvider's timer scheduling.
        fake.Advance(TimeSpan.FromSeconds 30.0)
        do! mgr.SweepNowAsync()

        match mgr.CheckAsync lease.LeaseId with
        | Error(ARCPError.LeaseRevoked(_, reason)) -> Assert.Equal("expired", reason)
        | Error(ARCPError.LeaseExpired _) -> () // also acceptable if revoked race not observed
        | other -> failwithf "expected LeaseRevoked/LeaseExpired, got %A" other

        let sawRevoked =
            lock sent (fun () -> sent |> Seq.exists (fun e -> e.Type = "lease.revoked"))

        Assert.True(sawRevoked, "expected a lease.revoked envelope from the sweeper")

        (mgr :> IDisposable).Dispose()
    }
