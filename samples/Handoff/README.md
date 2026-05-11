# Handoff

Cheap-tier first; escalate to a deep tier via `agent.handoff` when
the cheap model's confidence drops below threshold.

## Before ARCP

Routing layers either burn the most expensive model on every call,
or shard by a static rule (regex on the prompt) and silently
mis-route hard requests to the cheap path. Conversation context
gets POSTed across as a JSON blob with no integrity check, no
trace propagation, and no runtime verification.

## With ARCP

```fsharp
let! artifactId = packageContext cheap transcript    // artifact.put → id
do! emitHandoff cheap artifactId traceId             // agent.handoff
```

The deep runtime is identified by `kind` + pinned `fingerprint`;
mismatch = refuse. Context rides as an `ArtifactId`, not an inline
blob — sha256 + media_type included.

## ARCP primitives

- Capability negotiation distinguishes the tiers — RFC §7.
- `agent.handoff` + runtime identity verification — §14, §8.3.
- Artifacts for transcript transfer — §16.
- Trace propagation across the handoff — §17.1.

## File tour

- `Program.fs` — open cheap (pinned), ask, escalate if needed.
- `Cheap.fs` — `attempt` stub returning `(answer, confidence)`.

## Variations

- Three tiers (haiku → sonnet → opus) with two thresholds.
- Sticky-handoff cache keyed on a request fingerprint so repeated
  hard prompts skip the cheap pass.
- Deep tier holds the session; cheap closes immediately after
  emitting the handoff (current shape) — or both stay open for a
  back-and-forth.
