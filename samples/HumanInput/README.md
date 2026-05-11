# HumanInput

A relay that turns one ARCP `human.input.request` into a fan-out
across phone, email, and Slack — and resolves on the first valid
response, cancelling the rest.

## Before ARCP

Two patterns in the wild: (a) the agent embeds Slack/Twilio/SES
clients directly and reinvents response parsing for each; (b) the
agent posts to a single channel and dies waiting if nobody's
watching. Neither lets a runtime *block* a job until a human
answers without writing a custom dispatcher.

## With ARCP

```fsharp
for env in client.Events do
    if env.Type = "human.input.request" then
        let _ = Task.Run (fun () -> fanOut client env)
        ()

// inside fanOut:
let! winner = Task.WhenAny (tasks |> Array.map (fun t -> t :> Task))
// emit human.input.response with the winning value
// emit human.input.cancelled (code = "OK") to losing channels
```

The runtime treats the answer as a typed reply to the original
request and unblocks whichever job was waiting (RFC §12.4).

## ARCP primitives

- `human.input.request` / `human.input.response` /
  `human.input.cancelled` — RFC §12.1, §12.4.
- Multi-channel resolution rule (resolve on first; cancel the rest)
  — §12.3.
- `expires_at` deadline → `DEADLINE_EXCEEDED` cancellation — §12.4.

## File tour

- `Program.fs` — opens session, dispatches each inbound HITL request.
  `fanOut` is the file: first-wins resolution, loser-channel
  cancellation, deadline handling.
- `Channels.fs` — per-destination adapters; stubbed.

## Variations

- Replace first-wins with a quorum policy (negotiated as an
  extension on `human.input.request.payload`).
- Honor `default` (§12.4): synthesize a response when the deadline
  expires instead of cancelling.
- Use `human.choice.request` for multi-option pickers; the relay
  pattern is identical.
