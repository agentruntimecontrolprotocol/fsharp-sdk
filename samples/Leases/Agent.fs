/// Stand-in for the Anthropic tool-use loop. Real version: an
/// AsyncAnthropic client with a system prompt, yielding one LlmStep per turn.
module ARCP.Samples.Leases.Agent

open FSharp.Control

type ToolCall = { Argv: string list; Reason: string }

type LlmStep =
    {
        Thought: string
        ToolCall: ToolCall option
        Final: string option
    }

let llmLoop (userRequest: string) : IAsyncEnumerable<LlmStep> =
    taskSeq { failwith "elided: anthropic tool-use loop" }
