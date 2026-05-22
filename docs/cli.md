# CLI

The `Arcp.Cli` project ships the `arcp` global tool, installed with:

```bash
dotnet pack src/Arcp.Cli
dotnet tool install --global --add-source ./artifacts Arcp.Cli
```

## `arcp serve`

Start a runtime listening on stdio:

```
arcp serve [--stdio] [--token TOKEN]
```

| Option    | Default         | Notes                                                   |
| --------- | --------------- | ------------------------------------------------------- |
| `--stdio` | always set      | Only stdio transport is supported in v1.                |
| `--token` | `$ARCP_TOKEN`   | Bearer token. Falls back to the `ARCP_TOKEN` env var. If neither is set, a dev-mode verifier is used that accepts any token. |

Example — spawn a runtime as a child process with a fixed token:

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

Events are printed to stdout as they arrive. On completion the final
result or error is printed and the process exits.

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
```

## Samples

```bash
dotnet run --project samples/AspNetCore   # start a WebSocket runtime
arcp send --url ws://127.0.0.1:7878/arcp --agent hello --input '{"name":"world"}'
```
