# Leases And Provisioned Credentials

ARCP leases are immutable grants from capability namespace to glob patterns.
The F# SDK exposes them as `LeaseGrant` and validates operations with
`JobContext.ValidateOpAsync`.

```fsharp
let lease =
    Lease.empty
    |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
    |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]

do! ctx.ValidateOpAsync(
        Capabilities.ModelUse,
        "tier-fast/gpt-4o-mini",
        ctx.CancellationToken)
```

`model.use` is a normal lease namespace, so it follows the same matching and
subsetting behavior as other glob capabilities. A child lease may keep or
narrow the parent model set, but may not add a model pattern the parent did
not grant.

Provisioned credentials are enabled by configuring both
`ArcpServerOptions.Provisioner` and `ArcpServerOptions.CredentialStore`.
When a job is accepted, the runtime calls the provisioner after the lease is
finalized and before `job.accepted` is sent. Returned credentials are attached
to `job.accepted.payload.credentials` and exposed on `JobHandle.Credentials`
for the submitting client.

Credential values are secrets. The SDK does not include credentials in
`JobSummary`, `session.list_jobs`, or `job.subscribed`. On terminal job states
the runtime asks the provisioner to revoke every tracked credential, with
bounded retry through the configured `ICredentialStore`.
