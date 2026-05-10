namespace ARCP.Messages

open System.Text.Json
open ARCP.Ids

/// <summary>Observer subscription payload records (RFC §13).</summary>
module Subscriptions =

    /// <summary>Subscription filter (RFC §13.1).</summary>
    type SubscribeFilter =
        {
            SessionId: SessionId list option
            TraceId: TraceId list option
            JobId: JobId list option
            StreamId: StreamId list option
            Types: string list option
            MinPriority: string option
        }

    /// <summary>Backfill cursor for a subscription (RFC §13.2).</summary>
    type SubscribeSince = { AfterMessageId: MessageId option }

    /// <summary><c>subscribe</c> payload (RFC §13.1).</summary>
    type Subscribe =
        {
            Filter: SubscribeFilter
            Since: SubscribeSince option
        }

    /// <summary><c>subscribe.accepted</c> payload (RFC §13.1).</summary>
    type SubscribeAccepted = { SubscriptionId: SubscriptionId }

    /// <summary><c>subscribe.event</c> payload (RFC §13.1).</summary>
    type SubscribeEvent = { Event: JsonElement }

    /// <summary><c>unsubscribe</c> payload (RFC §13.3).</summary>
    type Unsubscribe = { Reason: string option }

    /// <summary><c>subscribe.closed</c> payload (RFC §13.3).</summary>
    type SubscribeClosed = { Reason: string; Code: string option }
