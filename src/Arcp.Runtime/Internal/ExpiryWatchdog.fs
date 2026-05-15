namespace ARCP.Runtime.Internal

open System
open System.Threading

/// Per-job lease-expiry watchdog (spec §9.5).
///
/// Built on `TimeProvider.CreateTimer` so tests can use a
/// `FakeTimeProvider` to advance virtual time.
type internal ExpiryWatchdog(timeProvider: TimeProvider) =
    let mutable timer : ITimer voption = ValueNone

    /// Schedule `onExpired` to fire at `expiresAt`. If `expiresAt`
    /// is already in the past the callback fires after a single
    /// `0` delay so it always runs on a timer thread, never inline.
    member _.Start(expiresAt: DateTimeOffset, onExpired: unit -> unit) : unit =
        let now = timeProvider.GetUtcNow()
        let delay =
            if expiresAt <= now then TimeSpan.Zero
            else expiresAt - now
        timer <-
            ValueSome (
                timeProvider.CreateTimer(
                    TimerCallback(fun _ -> onExpired ()),
                    null,
                    delay,
                    Timeout.InfiniteTimeSpan))

    member _.Stop() : unit =
        match timer with
        | ValueSome t ->
            try t.Dispose() with _ -> ()
            timer <- ValueNone
        | _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
