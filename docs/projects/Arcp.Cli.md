# Arcp.Cli

The `arcp` global tool — a thin command-line wrapper around `ArcpClient`
and `ArcpServer`. Useful for ad-hoc job submission, stdio child
processes, and CI smoke tests.

## Installation

Install as a global .NET tool from the local pack output:

```sh
dotnet pack src/Arcp.Cli
dotnet tool install --global --add-source ./artifacts Arcp.Cli
```

Or, once published, install from NuGet:

```sh
dotnet tool install --global Arcp.Cli
```

## Commands

```
arcp <command> [options]

Commands:
  serve     Run an in-process ARCP runtime over stdio.
  send      Submit a job to a WebSocket runtime and stream its events.
  --version Print the SDK version and ARCP protocol version.
```

The CLI ships exactly these two subcommands plus `--version`. There is
no built-in `cancel`, `status`, `events`, or `ls` — call the
corresponding `ArcpClient` method from a script for those.

## `arcp serve`

Run a stdio runtime with a single `echo` agent registered, suitable for
spawning as a child process inside a trust boundary.

```
arcp serve [--stdio | -s] [--token TOKEN]
```

| Option    | Default     | Notes                                                                                                            |
| --------- | ----------- | ---------------------------------------------------------------------------------------------------------------- |
| `--stdio` | (implied)   | Only stdio is supported. Reserved for future transports.                                                         |
| `--token` | dev mode    | When set, only this exact bearer token is accepted. When unset and `$ARCP_TOKEN` is empty, a permissive dev-mode verifier accepts any non-empty token. |

The process reads newline-delimited JSON envelopes from `stdin` and
writes responses to `stdout`. It exits when stdin closes.

```bash
ARCP_TOKEN=secret arcp serve --stdio
```

## `arcp send`

Submit one job to a runtime and stream its events to stdout. Exits 0
on a successful `job.result`, 1 on `job.error`.

```
arcp send --url URL --agent AGENT [--token TOKEN] [--input JSON]
```

| Option    | Required | Notes                                                          |
| --------- | -------- | -------------------------------------------------------------- |
| `--url`   | yes      | `ws://` or `wss://` URL of the runtime, e.g. `ws://localhost:7878/arcp`. |
| `--agent` | yes      | Agent identifier (`name` or `name@version`).                   |
| `--token` | no       | Bearer token. Falls back to `$ARCP_TOKEN`.                     |
| `--input` | no       | JSON-encoded input. Defaults to `null`.                        |

Each received event prints one line of the form `event: <kind>`; the
final inline result (or error message) is printed before exit.

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
arcp --version
# → arcp 1.0.0 (protocol 1.1)
```

## Environment variables

| Variable     | Used by         | Description                                       |
| ------------ | --------------- | ------------------------------------------------- |
| `ARCP_TOKEN` | `serve`, `send` | Bearer token fallback when `--token` is omitted.  |

## Exit codes

| Code | Meaning                                          |
| ---- | ------------------------------------------------ |
| 0    | Job completed with `job.result`, or `--version`. |
| 1    | Job failed (`job.error`) or runtime error.       |
| 2    | Argument parsing failure.                        |

## See also

- [CLI reference](../cli.md) — same content with longer prose.
- [Transports guide](../transports.md#stdio) — how stdio framing works.
- [Arcp.Client reference](Arcp.Client.md) — programmatic equivalent of `arcp send`.
