/// Cheap-tier inference. Real version: anthropic / litellm call with a
/// system prompt asking for a `Confidence: X.XX` line, then heuristics.
module ARCP.Samples.Handoff.Cheap

open System.Threading.Tasks

let attempt (prompt: string) : Task<string * float> =
    task { return failwith "elided: cheap-tier LLM call" }
