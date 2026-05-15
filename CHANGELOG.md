# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/).

## 1.0.0

Initial public release of the F# ARCP SDK against
[`spec/docs/draft-arcp-02.1.md`](../spec/docs/draft-arcp-02.1.md).

**Packages**

- `Arcp.Core` — wire types, codec, `ARCPError`, `LeaseGrant`.
- `Arcp.Client` — `ArcpClient`; in-memory / stdio / WebSocket
  transports; auto-ack scheduler; chunk assembler.
- `Arcp.Runtime` — `ArcpServer`; session & job managers; lease
  validator; per-job expiry watchdog; per-currency budget counters;
  agent inventory with version resolution.
- `Arcp.AspNetCore` — Kestrel endpoint mapping.
- `Arcp.Giraffe` — Giraffe `HttpHandler`.
- `Arcp.Otel` — OpenTelemetry `ActivitySource` and canonical span
  attributes.
- `Arcp` — umbrella re-exporting the curated public surface.
- `Arcp.Cli` — `arcp` global tool.

**Feature coverage**

All nine flag-gated features ship: `heartbeat`, `ack`, `list_jobs`,
`subscribe`, `lease_expires_at`, `cost.budget`, `progress`,
`result_chunk`, `agent_versions`.

**Conventions**

- Every public module and DU carries `[<RequireQualifiedAccess>]`.
- Every async public method ends with `CancellationToken`.
- Public surface uses BCL types (`Task<>`, no `Async<>`); no `obj`.
- Every `.fs` file ≤ 300 lines; every function ≤ 50 lines.
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` plus
  `<WarningsAsErrors>FS0025;FS0026;FS0040;FS0064</WarningsAsErrors>`
  on every project.
