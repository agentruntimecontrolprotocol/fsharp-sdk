module ARCP.Samples.Subscriptions.Sinks

open System.Text.Json
open System.Threading.Tasks
open ARCP.Envelope

/// Sink for log / job.* / tool.error envelopes — structlog-style stdout.
type StdoutSink() =
    member _.Handle(env: Envelope<JsonElement>) : Task =
        task { printfn "[stdout] %s" env.Type } :> Task

/// Sink for everything — replays into SQLite via arcp.store.eventlog schema.
type SqliteSink(path: string) =
    member _.Handle(env: Envelope<JsonElement>) : Task =
        task {
            // Real impl: ARCP.Store.EventLog.appendAsync ...
            return failwith "elided: write to sqlite eventlog"
        }
        :> Task

    interface System.IAsyncDisposable with
        member _.DisposeAsync() = System.Threading.Tasks.ValueTask()

/// Sink for metric / trace.span — forwards to OTLP.
type OtlpSink(endpoint: string) =
    member _.Handle(env: Envelope<JsonElement>) : Task =
        task { return failwith "elided: forward to OTLP collector" } :> Task
