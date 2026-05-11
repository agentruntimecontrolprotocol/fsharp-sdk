/// Primary + critic LLM stand-ins.
module ARCP.Samples.ReasoningStreams.Agents

open System.Threading.Tasks

type Severity =
    | Nudge
    | Warn
    | Halt

type Critique =
    {
        Severity: Severity
        Summary: string
        Suggestion: string option
        ConsumedTokens: int
    }

/// One reasoning step. Folds prior critique into the prompt when present.
let primaryStep (request: string) (priorCritique: Critique option) : Task<string> =
    task { return failwith "elided: primary LLM step" }

/// Critic LLM. Returns severity + summary + suggestion + tokens consumed.
let critiqueThought (thought: string) : Task<Critique> =
    task { return failwith "elided: critic LLM" }
