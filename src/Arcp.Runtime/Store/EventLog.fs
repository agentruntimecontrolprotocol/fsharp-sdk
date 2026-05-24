namespace ARCP.Runtime.Store

open System
open System.Collections.Concurrent
open System.Collections.Generic
open ARCP.Core

/// Buffered event log used for resume replay (spec §6.3) and
/// subscriber back-history (spec §7.6).
///
/// The default implementation is in-memory: events are buffered
/// per session inside a ring-style list and aged out by the
/// resume window. Restart-persistence is out of scope here; an
/// `Arcp.Storage.Sqlite` adapter can be added later.
type internal EventLogEntry =
    {
        SessionId: SessionId
        EventSeq: int64
        Envelope: Envelope
        Timestamp: DateTimeOffset
    }

type internal EventLogOptions =
    {
        /// Resume window in seconds. Entries older than this are
        /// candidates for eviction.
        ResumeWindowSec: int
        /// Maximum buffered entries per session.
        MaxPerSession: int
        TimeProvider: TimeProvider
    }

[<RequireQualifiedAccess>]
module internal EventLogOptions =
    let defaults: EventLogOptions =
        {
            ResumeWindowSec = 600
            MaxPerSession = 10_000
            TimeProvider = TimeProvider.System
        }

type internal EventLog(options: EventLogOptions) =
    // Per-session buffer uses Queue<T> so the hot eviction paths
    // (Append-over-cap, EvictExpired) are O(1) per removed entry
    // instead of the O(n) shift cost of List<T>.RemoveAt(0).
    let perSession = ConcurrentDictionary<string, Queue<EventLogEntry>>()
    let seqCounters = ConcurrentDictionary<string, int64 ref>()

    member _.NextSeq(sessionId: SessionId) : int64 =
        let counter = seqCounters.GetOrAdd(sessionId.Value, fun _ -> ref 0L)

        lock counter (fun () ->
            counter.Value <- counter.Value + 1L
            counter.Value)

    member _.CurrentSeq(sessionId: SessionId) : int64 =
        match seqCounters.TryGetValue sessionId.Value with
        | true, c -> c.Value
        | _ -> 0L

    member this.Append(sessionId: SessionId, env: Envelope) : EventLogEntry =
        let seq = this.NextSeq sessionId

        let entry =
            {
                SessionId = sessionId
                EventSeq = seq
                Envelope = Envelope.withEventSeq seq env
                Timestamp = options.TimeProvider.GetUtcNow()
            }

        let queue = perSession.GetOrAdd(sessionId.Value, fun _ -> Queue<EventLogEntry>())

        lock queue (fun () ->
            queue.Enqueue entry
            // Evict oldest if cap exceeded — Dequeue is O(1).
            if queue.Count > options.MaxPerSession then
                queue.Dequeue() |> ignore)

        entry

    /// Replay events whose `event_seq > fromSeq` (spec §6.3).
    /// Returns `RESUME_WINDOW_EXPIRED` if the requested seq is older
    /// than what the buffer still holds.
    member _.Replay(sessionId: SessionId, fromSeq: int64) : Result<EventLogEntry seq, ARCPError> =
        match perSession.TryGetValue sessionId.Value with
        | false, _ -> Ok Seq.empty
        | true, queue ->
            lock queue (fun () ->
                if queue.Count = 0 then
                    Ok Seq.empty
                else
                    let oldest = (queue.Peek()).EventSeq

                    if fromSeq < oldest - 1L then
                        Error(ARCPError.ResumeWindowExpired(fromSeq, options.ResumeWindowSec))
                    else
                        let snapshot = queue.ToArray()

                        snapshot |> Array.filter (fun e -> e.EventSeq > fromSeq) |> Array.toSeq |> Ok)

    /// Return all entries currently buffered for `sessionId`.
    member _.All(sessionId: SessionId) : EventLogEntry seq =
        match perSession.TryGetValue sessionId.Value with
        | true, queue -> lock queue (fun () -> queue.ToArray() |> Array.toSeq)
        | _ -> Seq.empty

    /// Forget a session's buffer entirely (e.g. on `session.bye`).
    member _.Drop(sessionId: SessionId) : unit =
        perSession.TryRemove(sessionId.Value) |> ignore
        seqCounters.TryRemove(sessionId.Value) |> ignore

    /// Age out entries whose timestamp is older than the resume
    /// window. Caller invokes periodically.
    member _.EvictExpired() : int =
        let now = options.TimeProvider.GetUtcNow()
        let cutoff = now.AddSeconds(-float options.ResumeWindowSec)

        let evictOne (queue: Queue<EventLogEntry>) : int =
            lock queue (fun () ->
                let mutable removed = 0

                while queue.Count > 0 && (queue.Peek()).Timestamp < cutoff do
                    queue.Dequeue() |> ignore
                    removed <- removed + 1

                removed)

        perSession |> Seq.sumBy (fun kvp -> evictOne kvp.Value)
