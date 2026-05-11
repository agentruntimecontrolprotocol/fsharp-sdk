/// Step bodies. Real version: an Anthropic call per step (plan / synth /
/// critique / finalize) and a retriever for `gather`.
module ARCP.Samples.Resumability.Steps

open System.Threading.Tasks
open ARCP.Client
open ARCP.Ids

let runStep (client: Client) (jobId: JobId) (step: string) (inputs: Map<string, obj>) : Task<obj> =
    task { return failwith "elided: step body" }
