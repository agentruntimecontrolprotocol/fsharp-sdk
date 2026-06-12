namespace ARCP.Client.Internal

open System
open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Settings for the auto-acknowledgement scheduler (spec §6.5).
type AutoAckOptions =
    {
        /// Maximum number of events to receive before forcing an `ack`.
        EveryEvents: int
        /// Minimum elapsed time before the next event triggers an ack.
        /// NOTE: the scheduler is event-driven — it is evaluated only on
        /// `OnEvent`, so this interval gates acks on the next event
        /// arrival rather than firing on its own timer. `session.ack` is
        /// advisory (spec §6.5), so no standalone timer is used.
        Interval: TimeSpan
    }

[<RequireQualifiedAccess>]
module AutoAckOptions =
    let defaults: AutoAckOptions =
        {
            EveryEvents = 32
            Interval = TimeSpan.FromMilliseconds(250.0)
        }

/// Tracks `last_processed_seq` and decides when to emit a
/// `session.ack`. Evaluation is event-driven: `OnEvent` returns the
/// seq to ack when the event-count threshold is hit, or when the
/// configured interval has elapsed *as observed on the current event*.
/// There is no background timer — if events stop arriving, no ack is
/// produced (acks are advisory per spec §6.5).
///
/// The scheduler does NOT send the ack itself — it returns the seq
/// to send so the client can build/send the envelope.
type internal AutoAckScheduler(options: AutoAckOptions, timeProvider: TimeProvider) =
    let lockObj = obj ()
    let mutable lastSeq: int64 = 0L
    let mutable countSinceAck: int = 0
    let mutable lastAckedSeq: int64 = 0L
    let mutable lastAckedAt: DateTimeOffset = timeProvider.GetUtcNow()

    /// Record that an event with `seq` has been processed.
    /// Returns `Some seq` if an ack should be sent now, otherwise `None`.
    member _.OnEvent(seq: int64) : int64 option =
        lock lockObj (fun () ->
            lastSeq <- max lastSeq seq
            countSinceAck <- countSinceAck + 1
            let now = timeProvider.GetUtcNow()
            let elapsed = now - lastAckedAt

            if countSinceAck >= options.EveryEvents || elapsed >= options.Interval then
                let toAck = lastSeq
                countSinceAck <- 0
                lastAckedSeq <- toAck
                lastAckedAt <- now
                Some toAck
            else
                None)

    member _.LastProcessedSeq = lastSeq
    member _.LastAckedSeq = lastAckedSeq
