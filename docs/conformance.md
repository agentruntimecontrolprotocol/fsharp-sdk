# Conformance

This SDK targets `spec/docs/draft-arcp-1.1.md`. See the
[root conformance document](../CONFORMANCE.md) for the full matrix.

## Feature coverage

All eleven feature flags are enabled by default (`Features.All`).
`model.use` and `provisioned_credentials` are only advertised when the
runtime is configured with an `ICredentialProvisioner` and
`ICredentialStore`.

| Feature                  | Flag string                | Notes                                           |
| ------------------------ | -------------------------- | ----------------------------------------------- |
| Heartbeat                | `heartbeat`                | `session.ping` / `session.pong`                 |
| Ack                      | `ack`                      | `session.ack`; auto-ack at 32 events / 250 ms   |
| List jobs                | `list_jobs`                | `session.list_jobs` / `session.jobs`            |
| Subscribe                | `subscribe`                | `job.subscribe` / `job.subscribed` / `job.unsubscribe` |
| Lease expires at         | `lease_expires_at`         | `lease_constraints.expires_at`; per-job `ExpiryWatchdog` |
| Cost budget              | `cost.budget`              | Per-currency counters; `BUDGET_EXHAUSTED` error  |
| Progress                 | `progress`                 | `progress` event body                           |
| Result chunk             | `result_chunk`             | Streamed `result_chunk` events + reassembly     |
| Agent versions           | `agent_versions`           | `name@version`; rich agent inventory            |
| Model use                | `model.use`                | Model-tier lease namespace                      |
| Provisioned credentials  | `provisioned_credentials`  | Lease-bound credentials on `job.accepted`       |

## v1.1-specific coverage

- `model.use` is a normal lease namespace — same glob matching and
  subset validation as other capabilities.
- Provisioned credential wire shape: `Credential` and
  `CredentialConstraints` serialize through `Json.Options`.
- Credential lifecycle: issued before `job.accepted`, exposed on
  `JobHandle.Credentials`, `credential_rotated` status events emitted
  on rotation, revoked via the `ICredentialStore` on terminal job
  states.
- Credential confidentiality: `JobSummary`, `session.list_jobs`, and
  `job.subscribed` omit credential values.

## Spec

[`../../spec/docs/draft-arcp-1.1.md`](../../spec/docs/draft-arcp-1.1.md)
