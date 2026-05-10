namespace ARCP.Messages

open System.Text.Json
open ARCP.Messages.Execution

/// <summary>Streaming payload records (RFC §11, §12).</summary>
module Streaming =

    /// <summary>Logical content kind carried by a stream (RFC §11.1).</summary>
    type StreamKind =
        | Text
        | Binary
        | Event
        | Log
        | Metric
        | Thought

    [<RequireQualifiedAccess>]
    module StreamKind =
        let value =
            function
            | Text -> "text"
            | Binary -> "binary"
            | Event -> "event"
            | Log -> "log"
            | Metric -> "metric"
            | Thought -> "thought"

    /// <summary><c>stream.open</c> payload (RFC §11.1).</summary>
    type StreamOpen =
        {
            Kind: StreamKind
            ContentType: string option
            Encoding: string option
        }

    /// <summary><c>stream.chunk</c> payload (RFC §11.2).</summary>
    type StreamChunk =
        {
            Sequence: int
            Data: JsonElement
            Sha256: string option
        }

    /// <summary><c>stream.close</c> payload (RFC §11.2).</summary>
    type StreamClose = { Reason: string option }

    /// <summary><c>stream.error</c> payload (RFC §11.2, §18).</summary>
    type StreamError = ErrorPayload
