/// Worker work. Real version: a CrewAI-style crew sized per role,
/// run via `crew.kickoff(inputs=...)` on a background task.
module ARCP.Samples.Heartbeats.Work

open System.Text.Json
open System.Threading.Tasks

let doWork (payload: JsonElement) : Task<JsonElement> =
    task { return failwith "elided: actual job execution" }
