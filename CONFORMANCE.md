# ARCP F# SDK Conformance

This SDK targets `spec/docs/draft-arcp-1.1.md`.

## v1.1 Feature Coverage

- `model.use` lease capability: supported through `Capabilities.ModelUse`,
  `Lease.validateLeaseOp`, and `Lease.isSubset`.
- `provisioned_credentials` feature flag: negotiated only when a runtime has
  an `ICredentialProvisioner` configured.
- Provisioned credential wire shape: `Credential` and
  `CredentialConstraints` serialize through `Json.Options`.
- Credential lifecycle: runtime issues credentials before `job.accepted`,
  exposes them on submitter `JobHandle.Credentials`, emits
  `credential_rotated` status events, and revokes tracked ids on terminal job
  states.
- Credential confidentiality: `JobSummary`, `session.list_jobs`, and
  `job.subscribed` omit credential values.
