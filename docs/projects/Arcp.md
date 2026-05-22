# Arcp

`Arcp` is the umbrella facade package for the ARCP F# SDK. It re-exports
the curated public surface from the core, client, and runtime projects so
small applications can reference one NuGet package and use one import.

```sh
dotnet add package Arcp
```

## What it includes

| Included project | Purpose |
| ---------------- | ------- |
| `Arcp.Core` | Wire primitives, envelopes, leases, errors, identifiers. |
| `Arcp.Client` | `ArcpClient`, transports, result handles, subscriptions. |
| `Arcp.Runtime` | `ArcpServer`, job handlers, lease validation, credentials. |

Optional host and tooling projects are published separately:

| Project | When to add it |
| ------- | -------------- |
| `Arcp.AspNetCore` | ASP.NET Core WebSocket endpoint hosting. |
| `Arcp.Giraffe` | Giraffe `HttpHandler` integration. |
| `Arcp.Otel` | OpenTelemetry `ActivitySource` and span attribute constants. |
| `Arcp.Cli` | The `arcp` command-line tool. |

## Single import

```fsharp
open ARCP.Public

let server = ArcpServer(ArcpServerOptions.defaults)
```

`ARCP.Public` exposes type aliases only. Canonical implementations still
live in their source projects and namespaces, so larger applications can
reference `ARCP.Core`, `ARCP.Client`, or `ARCP.Runtime` directly when
they want tighter dependency boundaries.

## Related

- [Getting started](../getting-started.md)
- [Architecture](../architecture.md)
- [Arcp.Core](Arcp.Core.md)
- [Arcp.Client](Arcp.Client.md)
- [Arcp.Runtime](Arcp.Runtime.md)
