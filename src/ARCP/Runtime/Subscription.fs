namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Subscriptions
open ARCP.Messages.Registry
open ARCP.Store.EventLog

/// <summary>
/// Observer subscriptions (RFC §13). Provides backfill from the
/// <see cref="EventLog"/> followed by live tail of envelopes flowing through
/// the runtime's send chokepoint.
/// </summary>
module Subscription =

    /// <summary>Default per-subscription channel capacity.</summary>
    let defaultChannelCapacity = 1024

    /// <summary>
    /// One active subscription. The <see cref="Channel"/> carries
    /// <c>subscribe.event</c> envelopes (wire-form) ready for the dispatch
    /// path to forward to the observer (RFC §13.1).
    /// </summary>
    type SubscriptionEntry =
        {
            /// <summary>Stable identifier (RFC §13.1).</summary>
            SubscriptionId: SubscriptionId
            /// <summary>The observer's session id (RFC §13).</summary>
            SessionId: SessionId
            /// <summary>Principal that owns the observer session.</summary>
            Principal: string
            /// <summary>Filter predicate evaluated on every published envelope.</summary>
            Filter: SubscribeFilter
            /// <summary>Optional backfill cursor (RFC §13.2).</summary>
            Since: SubscribeSince option
            /// <summary>Bounded delivery channel; writers drop the subscription on overflow.</summary>
            Channel: Channel<Envelope<MessageType>>
            /// <summary>Cancellation tied to the subscription's lifetime.</summary>
            Cts: CancellationTokenSource
            /// <summary>True once a terminal <c>subscribe.closed</c> has been emitted.</summary>
            Closed: bool ref
        }

    let private priorityOrdinal (p: string) : int =
        match p.ToLowerInvariant() with
        | "low" -> 0
        | "high" -> 2
        | "critical" -> 3
        | _ -> 1

    /// <summary>
    /// Evaluate the subscription filter (RFC §13.1) against an envelope. An
    /// empty filter matches everything.
    /// </summary>
    let matches (filter: SubscribeFilter) (env: Envelope<MessageType>) : bool =
        let okSession =
            match filter.SessionId with
            | None
            | Some [] -> true
            | Some ids ->
                env.SessionId
                |> Option.map (fun s -> List.contains s ids)
                |> Option.defaultValue false

        let okTrace =
            match filter.TraceId with
            | None
            | Some [] -> true
            | Some ids ->
                env.TraceId
                |> Option.map (fun t -> List.contains t ids)
                |> Option.defaultValue false

        let okJob =
            match filter.JobId with
            | None
            | Some [] -> true
            | Some ids ->
                env.JobId
                |> Option.map (fun j -> List.contains j ids)
                |> Option.defaultValue false

        let okStream =
            match filter.StreamId with
            | None
            | Some [] -> true
            | Some ids ->
                env.StreamId
                |> Option.map (fun s -> List.contains s ids)
                |> Option.defaultValue false

        let okType =
            match filter.Types with
            | None
            | Some [] -> true
            | Some ts -> List.contains env.Type ts

        let okPriority =
            match filter.MinPriority with
            | None -> true
            | Some min ->
                let envPri =
                    env.Priority |> Option.map Priority.value |> Option.defaultValue "normal"

                priorityOrdinal envPri >= priorityOrdinal min

        okSession && okTrace && okJob && okStream && okType && okPriority

/// <summary>
/// Per-runtime registry of observer subscriptions (RFC §13). Drives backfill
/// (from <see cref="EventLog"/>) and live fanout on every envelope routed
/// through the runtime's send chokepoint.
/// </summary>
type SubscriptionManager(eventLog: EventLog, send: Envelope<MessageType> -> Task) =

    let subs = ConcurrentDictionary<SubscriptionId, Subscription.SubscriptionEntry>()

    let wrapAsEvent
        (subId: SubscriptionId)
        (observerSession: SessionId)
        (original: Envelope<MessageType>)
        : Envelope<MessageType> =
        let asJson = Json.toElement original

        Envelope.create "subscribe.event" (SubscribeEvent { Event = asJson })
        |> Envelope.withSession observerSession
        |> Envelope.withSubscription subId

    let synthEventEnv (subId: SubscriptionId) (observerSession: SessionId) (eventType: string) : Envelope<MessageType> =
        // A synthetic informational event delivered through subscribe.event
        // with a payload that contains an "event" object describing the
        // synthetic notification (RFC §13.2 backfill completion semantics).
        let obj =
            JsonSerializer.SerializeToElement<{| ``type``: string |}>({| ``type`` = eventType |})

        Envelope.create "subscribe.event" (SubscribeEvent { Event = obj })
        |> Envelope.withSession observerSession
        |> Envelope.withSubscription subId

    let emitClosed (entry: Subscription.SubscriptionEntry) (reason: string) (code: string option) : Task =
        task {
            if not entry.Closed.Value then
                entry.Closed.Value <- true

                let env =
                    Envelope.create "subscribe.closed" (SubscribeClosed { Reason = reason; Code = code })
                    |> Envelope.withSession entry.SessionId
                    |> Envelope.withSubscription entry.SubscriptionId

                entry.Channel.Writer.TryComplete() |> ignore
                entry.Cts.Cancel()
                do! send env
        }
        :> Task

    let tryWriteOrOverflow (entry: Subscription.SubscriptionEntry) (env: Envelope<MessageType>) : Task =
        task {
            if entry.Closed.Value then
                return ()
            else
                let wrote = entry.Channel.Writer.TryWrite env

                if not wrote then
                    do! emitClosed entry "backpressure_overflow" (Some "BACKPRESSURE_OVERFLOW")
                    subs.TryRemove entry.SubscriptionId |> ignore
        }
        :> Task

    /// <summary>
    /// Begin a subscription for <paramref name="observerSessionId"/>. Emits
    /// <c>subscribe.accepted</c>, drains backfill from the event log, emits
    /// a synthetic <c>subscription.backfill_complete</c>, then transitions to
    /// live tail (RFC §13).
    /// </summary>
    member _.SubscribeAsync
        (
            observerSessionId: SessionId,
            observerPrincipal: string,
            filter: SubscribeFilter,
            since: SubscribeSince option,
            principalOf: SessionId -> string option,
            ?correlationId: MessageId,
            ?capacity: int
        ) : Task<SubscriptionId> =
        let capacity = defaultArg capacity Subscription.defaultChannelCapacity

        task {
            let subId = SubscriptionId.create ()

            // Authorization (RFC §13.2): filter session_ids must all be
            // owned by the observer's principal, OR be the observer's own
            // session.
            let authorized =
                match filter.SessionId with
                | None
                | Some [] -> true
                | Some ids ->
                    ids
                    |> List.forall (fun sid ->
                        sid = observerSessionId
                        || (principalOf sid
                            |> Option.map (fun p -> p = observerPrincipal)
                            |> Option.defaultValue false))

            if not authorized then
                let closed =
                    Envelope.create
                        "subscribe.closed"
                        (SubscribeClosed
                            {
                                Reason = "permission_denied"
                                Code = Some "PERMISSION_DENIED"
                            })
                    |> Envelope.withSession observerSessionId
                    |> Envelope.withSubscription subId

                let closed =
                    match correlationId with
                    | Some c -> closed |> Envelope.withCorrelation c
                    | None -> closed

                do! send closed
                return subId
            else
                let ch =
                    Channel.CreateBounded<Envelope<MessageType>>(
                        BoundedChannelOptions(capacity, FullMode = BoundedChannelFullMode.Wait)
                    )

                let entry: Subscription.SubscriptionEntry =
                    {
                        SubscriptionId = subId
                        SessionId = observerSessionId
                        Principal = observerPrincipal
                        Filter = filter
                        Since = since
                        Channel = ch
                        Cts = new CancellationTokenSource()
                        Closed = ref false
                    }

                subs.[subId] <- entry

                // Drain pump: forward buffered subscribe.event/closed envelopes
                // from the entry's bounded channel onto the wire (RFC §13.1).
                // Without this loop the runtime would buffer events forever and
                // observers would never see them.
                let _pump =
                    Task.Run(fun () ->
                        task {
                            let reader = entry.Channel.Reader

                            try
                                let mutable running = true

                                while running && not entry.Cts.IsCancellationRequested do
                                    let! hasMore = reader.WaitToReadAsync(entry.Cts.Token)

                                    if not hasMore then
                                        running <- false
                                    else
                                        match reader.TryRead() with
                                        | true, env ->
                                            try
                                                do! send env
                                            with _ ->
                                                ()
                                        | _ -> ()
                            with _ ->
                                ()
                        }
                        :> Task)

                let accepted =
                    Envelope.create "subscribe.accepted" (SubscribeAccepted { SubscriptionId = subId })
                    |> Envelope.withSession observerSessionId
                    |> Envelope.withSubscription subId

                let accepted =
                    match correlationId with
                    | Some c -> accepted |> Envelope.withCorrelation c
                    | None -> accepted

                do! send accepted

                // Backfill from log. Restrict to filter session_ids if any;
                // otherwise the observer's own session.
                let sessionsToReplay =
                    match filter.SessionId with
                    | Some(_ :: _ as ids) -> ids
                    | _ -> [ observerSessionId ]

                let after = since |> Option.bind (fun s -> s.AfterMessageId)

                for sid in sessionsToReplay do
                    let events =
                        match after with
                        | Some mid -> eventLog.Replay(sid, mid)
                        | None -> eventLog.Replay(sid)

                    for ev in events do
                        try
                            let parsedPayload = JsonDocument.Parse(ev.EnvelopeJson).RootElement

                            let typed = Json.fromElement<Envelope<MessageType>> parsedPayload

                            if Subscription.matches filter typed then
                                let wrapped = wrapAsEvent subId observerSessionId typed
                                do! tryWriteOrOverflow entry wrapped
                        with _ ->
                            ()

                // Synthetic backfill-complete marker (RFC §13.2).
                let backfillComplete =
                    synthEventEnv subId observerSessionId "subscription.backfill_complete"

                do! tryWriteOrOverflow entry backfillComplete

                return subId
        }

    /// <summary>Remove a subscription without emitting closure (RFC §13.3).</summary>
    member _.UnsubscribeAsync(subscriptionId: SubscriptionId) : Task<unit> =
        task {
            match subs.TryRemove subscriptionId with
            | true, entry ->
                entry.Closed.Value <- true
                entry.Channel.Writer.TryComplete() |> ignore
                entry.Cts.Cancel()
                return ()
            | _ -> return ()
        }

    /// <summary>Emit <c>subscribe.closed</c> for a subscription and remove it (RFC §13.3).</summary>
    member _.CloseAsync(subscriptionId: SubscriptionId, reason: string, ?code: string) : Task<unit> =
        task {
            match subs.TryRemove subscriptionId with
            | true, entry -> do! emitClosed entry reason code
            | _ -> return ()
        }

    /// <summary>
    /// Forward an envelope to every matching live subscription (RFC §13.1).
    /// Called by the runtime's send chokepoint after the event has been
    /// appended to the log.
    /// </summary>
    member _.PublishAsync(env: Envelope<MessageType>) : Task<unit> =
        task {
            // Never re-fanout subscribe.event envelopes — that would loop.
            if env.Type = "subscribe.event" then
                return ()
            else
                let snapshot = subs.Values |> Seq.toArray

                for entry in snapshot do
                    if not entry.Closed.Value && Subscription.matches entry.Filter env then
                        let wrapped = wrapAsEvent entry.SubscriptionId entry.SessionId env
                        do! tryWriteOrOverflow entry wrapped

                return ()
        }

    /// <summary>
    /// Try to retrieve a subscription entry for the runtime's pump loop.
    /// </summary>
    member _.TryGet(subscriptionId: SubscriptionId) : Subscription.SubscriptionEntry option =
        match subs.TryGetValue subscriptionId with
        | true, e -> Some e
        | _ -> None

    /// <summary>Snapshot all active subscriptions.</summary>
    member _.Snapshot() : Subscription.SubscriptionEntry seq = subs.Values |> Seq.toArray :> _

    /// <summary>Total active subscriptions.</summary>
    member _.Count: int = subs.Count

    interface IDisposable with
        member _.Dispose() =
            for kv in subs do
                let entry = kv.Value
                entry.Closed.Value <- true
                entry.Channel.Writer.TryComplete() |> ignore

                try
                    entry.Cts.Cancel()
                with _ ->
                    ()

                try
                    entry.Cts.Dispose()
                with _ ->
                    ()

            subs.Clear()
