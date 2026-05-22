# Email Vendor Leases

This recipe ports the TypeScript `email-vendor-leases` pattern to F#.
The triage agent receives a lease that allows read-only inbox tools and
intentionally omits `send_reply`.

The important flow is:

- `tool.call` lease grants include `inbox_list` and `inbox_read`.
- The agent emits `x-vendor.acme.email.parsed` after reading a message.
- The attempted `send_reply` call fails lease validation.
- The denial is surfaced as a recoverable `tool_result` error, and the
  agent returns a draft instead of sending mail.

Run it:

```sh
dotnet run --project recipes/email-vendor-leases/email-vendor-leases.fsproj
```
