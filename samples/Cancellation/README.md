# Cancellation

Two scenarios that exercise the §10.4–§10.5 control surface that
distinguishes ARCP from "agent over plain HTTP":

- `cancel`: cooperative termination with a deadline.
- `interrupt`: pause the job and route through a human, no
  termination.

## Before ARCP

Cancellation usually means closing the socket or trying to kill the
process. The agent's tool was already mid-network call, so it
either completes anyway (silent waste of money) or leaves a
half-applied side effect. There's no notion of "stop and ask"; the
only knob is "stop".

## With ARCP

```fsharp
// Stop the job; the runtime drives it to a clean checkpoint inside `deadline_ms`.
match! cancelJob client jobId "user_aborted" 5_000 with
| Ok () -> printfn "cancel.accepted"
| Error e -> printfn "cancel.refused: %s" (ARCPError.message e)

// Or: pause the job, ask the human, resume.
do! interruptJob client jobId "Pause and ask before touching prod."
// runtime emits human.input.request; answer with the HumanInput sample.
```

## ARCP primitives

- `cancel` cooperative contract — RFC §10.4 (`cancel.accepted` /
  `cancel.refused`, `deadline_ms`, escalation to `ABORTED`).
- `interrupt` (distinct from cancel) — §10.5; emits
  `human.input.request`, leaves the job in `blocked`.
- `capabilities.interrupt: false` fallback to `cancel` (per §10.5).

## File tour

- `Program.fs` — two scenarios driven by `argv.[0]` (`cancel` or
  `interrupt`).

## Variations

- Pair `interrupt` with the `HumanInput` sample for a working
  pause-and-ask loop.
- Send `cancel` against a `StreamId` instead of a `JobId` to
  terminate just one stream — terminal is a `stream.error` with
  `code: CANCELLED` (§10.4).
- Race many peers, cancel the slowest once N succeed.
