module ARCP.UnitTests.ExpiryWatchdogTests

open System
open System.Threading
open Microsoft.Extensions.Time.Testing
open Xunit
open FsUnit.Xunit
open ARCP.Runtime.Internal

[<Fact>]
let ``Watchdog fires after expires_at when virtual time advances`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.Parse "2026-01-01T00:00:00Z")
    let w = new ExpiryWatchdog(fake)
    let fired = ref 0
    w.Start(DateTimeOffset.Parse "2026-01-01T00:00:10Z", fun () -> Interlocked.Increment(fired) |> ignore)
    fake.Advance(TimeSpan.FromSeconds 5.0)
    fired.Value |> should equal 0
    fake.Advance(TimeSpan.FromSeconds 6.0)
    // FakeTimeProvider's CreateTimer callback runs synchronously on Advance.
    fired.Value |> should equal 1

[<Fact>]
let ``Stop cancels pending fires`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.Parse "2026-01-01T00:00:00Z")
    let w = new ExpiryWatchdog(fake)
    let fired = ref 0
    w.Start(DateTimeOffset.Parse "2026-01-01T00:00:10Z", fun () -> Interlocked.Increment(fired) |> ignore)
    w.Stop()
    fake.Advance(TimeSpan.FromSeconds 30.0)
    fired.Value |> should equal 0
