/// Cheap-tier first; escalate to deep tier via agent.handoff.
module ARCP.Samples.Handoff.Program

open System.Text
open System.Text.Json
open System.Threading.Tasks
open ARCP.Client
open ARCP.Ids
open ARCP.Trace
open ARCP.Samples.Handoff.Cheap

let confidenceThreshold = 0.65
let cheapUrl = "wss://haiku-pool.tier1.internal"
let deepUrl = "wss://opus-pool.tier3.internal"
let deepKind = "arcp-opus-pool"
let deepFingerprint = "sha256:0a37bf7d61cca21f00..." // pinned

type Transcript =
    {
        UserRequest: string
        Turns: (string * string) list
        CheapConfidence: float
    }

type RuntimeRef =
    {
        Url: string
        Kind: string
        Fingerprint: string
    }

/// Upload the transcript as an inline artifact; returns the artifact_id.
let packageContext (cheap: Client) (transcript: Transcript) : Task<ArtifactId> =
    task {
        let json = JsonSerializer.SerializeToUtf8Bytes transcript

        match! cheap.PutArtifactAsync("application/json", json) with
        | Ok r -> return r.ArtifactId
        | Error e -> return failwithf "artifact.put failed: %A" e
    }

/// Emit agent.handoff with target runtime pinned + transcript pointer.
let emitHandoff (cheap: Client) (artifactId: ArtifactId) (traceId: TraceId) : Task =
    task {
        let _target =
            {
                Url = deepUrl
                Kind = deepKind
                Fingerprint = deepFingerprint
            }
        // Real call: cheap.HandoffAsync(target = _target, sharedMemoryRef = artifactId, traceId = traceId)
        // RFC §14 / §8.3: kind + fingerprint pinned; receiver MUST refuse on mismatch.
        return failwith "elided: agent.handoff envelope"
    }
    :> Task

[<EntryPoint>]
let main _ =
    task {
        let cheap: Client = Unchecked.defaultof<_> // transport=WebSocketTransport(cheapUrl), pinned

        // After cheap.OpenAsync(...): pin runtime kind + fingerprint (RFC §8.3); refuse on mismatch.
        // if accepted.Runtime.Kind <> "arcp-haiku-pool" then failwith "cheap kind mismatch"

        let request = "what does CRDT stand for?"
        let traceId = TraceId.create ()

        let! answer, confidence = attempt request

        if confidence >= confidenceThreshold then
            printfn "%s" answer
        else
            let! artifactId =
                packageContext
                    cheap
                    {
                        UserRequest = request
                        Turns = [ "user", request; "assistant", answer ]
                        CheapConfidence = confidence
                    }

            do! emitHandoff cheap artifactId traceId
            printfn "[handed off to %s trace_id=%s]" deepKind (TraceId.value traceId)

        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
