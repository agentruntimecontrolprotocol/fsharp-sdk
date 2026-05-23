# Troubleshooting

## Handshake failures

### `UNAUTHENTICATED`

The server rejected the bearer token in `session.hello`. Causes:

- **Token mismatch** — the token passed to `AuthScheme.Bearer` doesn't
  match the one registered in `BearerVerifier` on the server.
- **Dev-mode verifier** — `ArcpServerOptions.defaults` uses a
  permissive dev-mode verifier. If you've switched to a
  `StaticBearerVerifier`, ensure the token is in the map.
- **Missing token** — `AuthScheme.None` was used but the server
  requires a real token.

```fsharp
// Server: configure a specific verifier
open ARCP.Runtime.Auth

let options =
    { ArcpServerOptions.defaults with
        BearerVerifier = StaticBearerVerifier(readOnlyDict [ "my-token", "user-1" ]) }
```

### `INVALID_REQUEST` on connect

The `session.hello` envelope failed validation. Common causes:

- `Client.Name` or `Client.Version` is empty.
- `Capabilities.Encodings` list is empty — must include `"json"`.

### Transport closed before welcome

If `ConnectAsync` throws with a closed transport rather than an
`ARCPError`, the transport closed before the handshake completed.
Check that:

- The server side is started before the client calls `ConnectAsync`.
- The server's `HandleSessionAsync` is awaited on a background task,
  not blocking the caller.

---

## Job errors

### `AGENT_NOT_AVAILABLE`

No agent was registered under the name given in `JobSubmitRequest.Agent`.
Verify you called `server.RegisterAgent("name", handler)` before
`HandleSessionAsync`.

### `AGENT_VERSION_NOT_AVAILABLE`

A specific version was requested (e.g. `"echo@2.0"`) but no agent with
that name and version was registered. Check `RegisterAgentVersion`.

### `PERMISSION_DENIED` during `ValidateOpAsync`

The job's effective lease doesn't cover the requested capability/target.
Confirm:

1. The `LeaseRequest` in `JobSubmitRequest` includes the namespace and
   a glob that covers the target.
2. The server isn't narrowing the lease in a custom `BearerVerifier`.

### `BUDGET_EXHAUSTED`

The job's `cost.budget` constraint for a currency was consumed. The
agent must call `EmitMetricAsync` with the correct currency and amount.
Check the `LeaseConstraints.Budgets` map in the submit request.

### `LEASE_EXPIRED`

`LeaseConstraints.ExpiresAt` was reached before the job completed.
Either extend the deadline or remove `lease_expires_at` from the
feature set if you don't need expiry enforcement.

---

## Streaming / chunks

### `handle.Result` never completes

If the agent calls `BeginStreamingResult()` but doesn't complete all
chunks, `handle.Result` blocks indefinitely. Ensure the agent calls
`CompleteStreamingResultAsync` (or the finalizing overload of
`EmitResultChunkAsync`) to close the stream.

### Chunks arrive out of order

The chunk assembler in `ArcpClient` reorders by `event_seq` before
reassembly. If events arrive with non-monotonic sequences, check the
transport for packet re-ordering or dropped frames.

---

## Auto-ack

Auto-ack is enabled by default (`AutoAckOptions.defaults`) and fires
at 32 events or 250 ms, whichever comes first. If you observe the
server blocking due to back-pressure, either:

- Lower `EveryEvents` or shorten `Interval` in `AutoAckOptions`.
- Process events faster inside your consumer loop.
- Set `AutoAck` thresholds to effectively infinite values and call
  `AckAsync` manually after processing.

---

## WebSocket / host integration

### 400 Bad Request on `/arcp`

The endpoint received a non-WebSocket HTTP request. Ensure the client
sends a proper WebSocket upgrade. For `curl` testing, use `websocat`
or the `arcp send` CLI.

### Connection closed immediately

Confirm `app.UseWebSockets()` is called **before** `app.MapArcp(...)`.
Without `UseWebSockets`, the middleware stack rejects the upgrade.

---

## Tests

Run the full test suite to confirm nothing is broken after local
changes:

```bash
dotnet test ARCP.slnx
```

Unit tests cover codec round-trips, lease arithmetic, and feature-set
laws. Integration tests boot a paired client + runtime over the
in-memory transport and exercise the full job lifecycle.
