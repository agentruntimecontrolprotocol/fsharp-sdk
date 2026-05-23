# CLI

The `Arcp.Cli` project ships the `arcp` global tool, installed with:

```bash
dotnet pack src/Arcp.Cli
dotnet tool install --global --add-source ./artifacts Arcp.Cli
```

## `arcp serve`

Start a runtime listening on stdio. A single `echo` agent is registered;
it is intended as a smoke-test runtime, not a production binary.

```
arcp serve [--stdio | -s] [--token TOKEN]
```

| Option    | Default     | Notes                                                                                                            |
| --------- | ----------- | ---------------------------------------------------------------------------------------------------------------- |
| `--stdio` | (implied)   | Only stdio is supported; the flag is reserved for future transports.                                             |
| `--token` | dev mode    | When set, only this exact bearer token is accepted (`StaticBearerVerifier`). When unset and `$ARCP_TOKEN` is empty, a permissive dev-mode verifier accepts any non-empty token. |

`ARCP_TOKEN` is consulted only when `--token` is omitted.

Example — run with a fixed token:

```bash
ARCP_TOKEN=secret arcp serve --stdio
```

The process reads newline-framed JSON envelopes from `stdin` and
writes responses to `stdout`. It exits when stdin closes.

## `arcp send`

Submit a job to a running WebSocket runtime and stream its events:

```
arcp send --url URL --agent AGENT [--token TOKEN] [--input JSON]
```

| Option    | Required | Notes                                                          |
| --------- | -------- | -------------------------------------------------------------- |
| `--url`   | yes      | WebSocket URL, e.g. `ws://localhost:7878/arcp`                 |
| `--agent` | yes      | Agent name (and optional version as `name@version`)            |
| `--token` | no       | Bearer token. Falls back to `$ARCP_TOKEN`.                     |
| `--input` | no       | JSON-encoded input. Defaults to `null`.                        |

Each received event prints one `event: <kind>` line to stdout. On a
`job.result` the inline result (or the literal `null`) is printed and
the process exits with code 0; on a `job.error` the error code and
message are written to stderr and the process exits with code 1.

```bash
arcp send \
  --url ws://localhost:7878/arcp \
  --agent hello \
  --token secret \
  --input '{"name":"world"}'
```

## `arcp --version`

Prints the SDK version and the ARCP protocol version it targets:

```
$ arcp --version
arcp 1.0.0 (protocol 1.1)
```

There is no `arcp cancel`, `arcp status`, `arcp events`, or `arcp ls` —
the CLI is intentionally tiny. Drive those operations from a script
that uses `ArcpClient` directly.

## Samples

```bash
dotnet run --project samples/AspNetCore   # start a WebSocket runtime
arcp send --url ws://127.0.0.1:7878/arcp --agent hello --input '{"name":"world"}'
```
