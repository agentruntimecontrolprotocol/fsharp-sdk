namespace ARCP.Messages

open System
open System.Text.Json
open ARCP.Ids

/// <summary>Telemetry payload records (RFC §17).</summary>
module Telemetry =

    /// <summary><c>event.emit</c> payload (RFC §17.1).</summary>
    type EventEmit =
        {
            Type: string
            Attributes: JsonElement option
        }

    /// <summary>
    /// <c>log</c> payload (RFC §17.2). Named <c>LogEntry</c> on the F# side
    /// because <c>Log</c> collides with the surrounding submodule.
    /// </summary>
    type LogEntry =
        {
            Level: string
            Message: string
            Attributes: JsonElement option
        }

    /// <summary>Canonical log level values (RFC §17.2).</summary>
    module Log =
        [<Literal>]
        let Trace = "trace"

        [<Literal>]
        let Debug = "debug"

        [<Literal>]
        let Info = "info"

        [<Literal>]
        let Warn = "warn"

        [<Literal>]
        let Error = "error"

        [<Literal>]
        let Fatal = "fatal"

    /// <summary><c>metric</c> payload (RFC §17.3).</summary>
    type MetricSample =
        {
            Name: string
            Value: float
            Unit: string
            Dims: JsonElement option
        }

    /// <summary>Reserved metric names (RFC §17.3.1).</summary>
    module Metric =
        [<Literal>]
        let TokensUsed = "tokens.used"

        [<Literal>]
        let CostUsd = "cost.usd"

        [<Literal>]
        let GpuSeconds = "gpu.seconds"

        [<Literal>]
        let ToolInvocations = "tool.invocations"

        [<Literal>]
        let LatencyMs = "latency.ms"

        [<Literal>]
        let BytesIn = "bytes.in"

        [<Literal>]
        let BytesOut = "bytes.out"

        [<Literal>]
        let ErrorsTotal = "errors.total"

    /// <summary><c>trace.span</c> payload (RFC §17.1).</summary>
    type TraceSpan =
        {
            Name: string
            SpanId: SpanId
            ParentSpanId: SpanId option
            StartedAt: DateTimeOffset
            EndedAt: DateTimeOffset option
            Attributes: JsonElement option
        }
