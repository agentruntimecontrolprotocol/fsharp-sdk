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
type internal EventLogEntry = {
    SessionId: SessionId
    EventSeq: int64
    Envelope: Envelope
    Timestamp: DateTimeOffset
}

type internal EventLogOptions = {
    /// Resume window in seconds. Entries older than this are
    /// candidates for eviction.
    ResumeWindowSec: int
    /// Maximum buffered entries per session.
    MaxPerSession: int
    TimeProvider: TimeProvider
}

[<RequireQualifiedAccess>]
module internal EventLogOptions =
    let defaults : EventLogOptions = {
        ResumeWindowSec = 600
        MaxPerSession = 10_000
        TimeProvider = TimeProvider.System
    }

type internal EventLog(options: EventLogOptions) =
    let perSession = ConcurrentDictionary<string, List<EventLogEntry>>()
    let seqCounters = ConcurrentDictionary<string, int64 ref>()
    let lockObj = obj ()

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
        let entry = {
            SessionId = sessionId
            EventSeq = seq
            Envelope = Envelope.withEventSeq seq env
            Timestamp = options.TimeProvider.GetUtcNow()
        }
        let list = perSession.GetOrAdd(sessionId.Value, fun _ -> List<EventLogEntry>())
        lock list (fun () ->
            list.Add entry
            // Evict oldest if cap exceeded.
            if list.Count > options.MaxPerSession then
                list.RemoveAt 0)
        entry

    /// Replay events whose `event_seq > fromSeq` (spec §6.3).
    /// Returns `RESUME_WINDOW_EXPIRED` if the requested seq is older
    /// than what the buffer still holds.
    member _.Replay(sessionId: SessionId, fromSeq: int64) : Result<EventLogEntry seq, ARCPError> =
        match perSession.TryGetValue sessionId.Value with
        | false, _ -> Ok Seq.empty
        | true, list ->
            lock list (fun () ->
                if list.Count = 0 then Ok Seq.empty
                else
                    let oldest = list.[0].EventSeq
                    if fromSeq < oldest - 1L then
                        Error (ARCPError.ResumeWindowExpired(fromSeq, options.ResumeWindowSec))
                    else
                        list
                        |> Seq.filter (fun e -> e.EventSeq > fromSeq)
                        |> Seq.toList
                        |> Seq.ofList
                        |> Ok)

    /// Return all entries currently buffered for `sessionId`.
    member _.All(sessionId: SessionId) : EventLogEntry seq =
        match perSession.TryGetValue sessionId.Value with
        | true, list ->
            lock list (fun () -> list |> Seq.toList |> Seq.ofList)
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
        // The per-session buffer is a mutable `List<T>` from the BCL
        // (chosen for O(1) Add + RemoveAt 0 amortised semantics under
        // a lock); eviction has to mutate it in place.
        let evictOne (list: List<EventLogEntry>) : int =
            lock list (fun () ->
                let rec drop removed =
                    if list.Count > 0 && list.[0].Timestamp < cutoff then
                        list.RemoveAt 0
                        drop (removed + 1)
                    else removed
                drop 0)
        perSession
        |> Seq.sumBy (fun kvp -> evictOne kvp.Value)
