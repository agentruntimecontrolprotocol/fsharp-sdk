/// Final-pass synthesizer. Real version: an Anthropic call that folds
/// successful subagent outputs into prose, ignoring failed peers.
module ARCP.Samples.Delegation.Synth

let synthesize (request: string) (jobs: obj list) : string =
    failwith "elided: final synthesis LLM call"
