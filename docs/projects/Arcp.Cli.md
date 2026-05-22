# Arcp.Cli

Command-line tool for interacting with ARCP runtimes. Useful for
manual testing, scripting, and CI pipelines.

## Installation

Install as a global .NET tool:

```
dotnet tool install -g Arcp.Cli
```

Or as a local tool in a repo:

```
dotnet tool install Arcp.Cli
dotnet tool restore
```

## Commands

```
arcp <command> [options]

Commands:
  submit    Submit a job and stream its events to stdout.
  cancel    Cancel a running job.
  status    Print the current state of a job.
  events    Stream events from a running or completed job.
  ls        List jobs visible on a session.
  ping      Open a session and confirm the welcome handshake.
```

## `arcp submit`

```
arcp submit [options] <agent> [input]

Arguments:
  agent     Agent name to invoke.
  input     JSON input (inline). Reads from stdin if omitted.

Options:
  --url     <url>      Runtime URL (default: $ARCP_URL).
  --token   <token>    Bearer token (default: $ARCP_TOKEN).
  --lease   <json>     Lease request as JSON object.
  --key     <key>      Idempotency key.
  --timeout <sec>      max_runtime_sec override.
  --trace              Print trace_id to stderr.
  --json               Output raw JSON events instead of pretty-print.
  --no-color           Disable colour output.
```

Examples:

```bash
# inline input
arcp submit echo '{"msg":"hello"}'

# pipe JSON from a file
cat input.json | arcp submit research

# with a lease
arcp submit fetcher '{"url":"https://example.com"}' \
    --lease '{"net.fetch":["https://example.com/**"]}'

# capture structured output
arcp submit report '{"week":"2026-W20"}' --json | jq '.output'
```

## `arcp cancel`

```
arcp cancel [options] <job-id>

Options:
  --reason  <text>   Human-readable cancellation reason.
  --url, --token     same as submit
```

## `arcp status`

```
arcp status [options] <job-id>
```

Prints a one-line summary: `job_id  agent  state  created_at`.

## `arcp events`

```
arcp events [options] <job-id>

Options:
  --since   <seq>    Resume from event sequence number.
  --follow  -f       Keep streaming until the job terminates.
  --json             Raw JSON events.
  --url, --token     same as submit
```

## `arcp ls`

```
arcp ls [options]

Options:
  --url, --token     same as submit
  --json             Raw JSON list.
```

## `arcp ping`

```
arcp ping [options]

Options:
  --url, --token     same as submit
```

Exits 0 on a successful `session.welcome`, 1 otherwise. Useful as a
health-check probe in CI.

## Environment variables

| Variable    | Description                           |
| ----------- | ------------------------------------- |
| `ARCP_URL`  | Default runtime WebSocket URL.        |
| `ARCP_TOKEN`| Default bearer token.                 |

## Exit codes

| Code | Meaning                                           |
| ---- | ------------------------------------------------- |
| 0    | Success (job completed with `job.result`).        |
| 1    | Job failed (`job.error`), connection error, etc.  |
| 2    | Bad arguments or usage error.                     |

## See also

- [Jobs guide](../guides/jobs.md) — submit, cancel, idempotency keys.
- [Auth guide](../guides/auth.md) — bearer tokens.
- [Arcp.Client reference](Arcp.Client.md) — programmatic equivalent.
