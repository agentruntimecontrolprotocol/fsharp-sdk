/// Generator + reviewer stand-ins. Real version: AutoGen-style agents.
module ARCP.Samples.PermissionChallenge.Agents

open System.Threading.Tasks

type Patch = { Diff: string }

type ReviewVerdict = { Grant: bool; Reason: string }

let propose (ticket: string) (priorDenial: string option) : Task<Patch> =
    task { return failwith "elided: generator LLM" }

let review (ticket: string) (request: obj) : Task<ReviewVerdict> =
    task { return failwith "elided: reviewer LLM" }
