# Contributing to Arcp

Thanks for your interest in improving the F# SDK for ARCP. This
document covers how to report issues, propose changes, and get a change merged.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Where changes belong

ARCP is two things in two places, and a change belongs to exactly one of them:

- **The protocol** — the wire format, message semantics, lease rules, error
  taxonomy, feature flags. These live in the
  [specification repository](https://github.com/agentruntimecontrolprotocol/spec).
  If your idea changes what goes *on the wire* or what a conformant runtime must
  do, it is a spec change — open it there, not here. This SDK implements the
  spec; it does not define it.
- **This SDK** — how the protocol is expressed idiomatically in F#:
  bugs, ergonomics, performance, missing-but-specified features, docs, tests.
  Those belong here.

When in doubt, open an issue here and we'll redirect if it's really a protocol
question.

## The golden rule: conform, don't extend

A change to this SDK must keep it a faithful client of
[ARCP v1.1 (draft)](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).
Concretely:

- **Don't invent wire behavior.** No envelope fields, event kinds, error codes,
  or feature flags that the spec doesn't define. If you need one, it's a spec
  proposal first.
- **Negotiate honestly.** Only advertise a feature flag in `session.hello` once
  the SDK actually implements it. The feature matrix in the README must match
  what the code negotiates — a row marked `Supported` is a promise.
- **Respect the semantics.** Sequence numbers stay gap-free and monotonic;
  `LEASE_EXPIRED` and `BUDGET_EXHAUSTED` stay non-retryable; the effective
  feature set is the intersection of client and runtime advertisements. Tests
  must not paper over a semantic the spec requires.
- **Stay layered.** This SDK controls runtimes. It does not expose tools (that's
  MCP) or export telemetry (that's OpenTelemetry). PRs that blur those layers
  will be asked to move the logic out.

## Reporting bugs

Open an issue with: the SDK version and F# version, the runtime you
connected to, a minimal reproduction (the smallest program that triggers it),
what you expected, and what happened. A failing test is the best possible bug
report. Wire-level traces (the envelopes exchanged) help enormously for protocol
behavior — redact any `auth.token` or provisioned-credential `value` first.

## Proposing a change

For anything beyond a small fix, open an issue describing the problem before
writing code, so we can agree on the approach. Small, focused PRs review faster
than large ones; if a change is big, say so early and we'll help break it down.

## Development setup

The SDK targets .NET 10 (`net10.0`). The required SDK version is pinned in
`global.json` (`10.0.203`, `rollForward: latestFeature`) — install a matching
.NET SDK from [dot.net](https://dotnet.microsoft.com/download) and the .NET CLI
will respect it automatically. NuGet is the package manager; all package
versions are pinned centrally in `Directory.Packages.props`. Clone, restore
local tools (Fantomas), then restore project dependencies:

```sh
git clone https://github.com/agentruntimecontrolprotocol/fsharp-sdk.git
cd fsharp-sdk
dotnet tool restore
dotnet restore ARCP.slnx
dotnet build ARCP.slnx --configuration Release
```

## Tests and conformance

Two layers must pass before a PR merges:

- **Unit tests** — this SDK's own suite:

  ```sh
  dotnet test tests/Arcp.UnitTests/Arcp.UnitTests.fsproj
  ```

- **Conformance** — the SDK's behavior against the reference runtime. New
  protocol-facing code (session negotiation, event sequencing, lease handling,
  error mapping) needs a test that exercises the real exchange, not a mock that
  assumes the answer. The integration suite under
  `tests/Arcp.IntegrationTests` runs the F# client against the in-process
  `Arcp.Runtime` reference implementation; run it with
  `dotnet test tests/Arcp.IntegrationTests/Arcp.IntegrationTests.fsproj`. To
  point it at an out-of-process runtime, set the `ARCP_RUNTIME_URL` environment
  variable to its WebSocket endpoint before invoking the suite.

CI runs both on every PR. A PR that changes which feature flags the SDK
negotiates must also update the README feature matrix in the same change.

### Measuring coverage

The full coverage report is regenerated with:

```sh
dotnet test ARCP.slnx --collect:"XPlat Code Coverage" \
  --results-directory TestResults/review-coverage
reportgenerator \
  -reports:"TestResults/review-coverage/*/coverage.cobertura.xml" \
  -targetdir:"TestResults/coverage-report" \
  -reporttypes:"TextSummary"
```

Install the report tool once with
`dotnet tool install -g dotnet-reportgenerator-globaltool`. The summary lands
at `TestResults/coverage-report/Summary.txt`. The target is ≥ 80 % line
coverage; transport and async-state-machine paths drive most of the
remaining branch gaps and additions there are welcome.

## Coding standards

Formatting is enforced by [Fantomas](https://fsprojects.github.io/fantomas/)
(restored as a local tool); the compiler runs with
`TreatWarningsAsErrors=true` plus an explicit `WarningsAsErrors` list
(`FS0025;FS0026;FS0040;FS0064`) so warnings are part of the build gate. Run
the same commands CI runs:

```sh
dotnet tool restore
dotnet fantomas --check src tests samples
dotnet build ARCP.slnx --configuration Release
```

Match the surrounding code. Public API changes need doc comments and an entry in
the changelog. Prefer clarity over cleverness in a library others build on.

## Commit and pull-request conventions

- Write focused commits with present-tense, imperative subjects
  (`add result_chunk reassembly`, not `added` / `adds`).
- Reference the issue a PR closes (`Closes #123`).
- Keep the PR description honest about scope and any spec sections touched.
- Rebase on the default branch and ensure CI is green before requesting review.
- Sign off your commits to certify the [Developer Certificate of Origin](https://developercertificate.org/):

  ```sh
  git commit -s -m "your message"
  ```

## Releases

Releases are cut by maintainers. Pushing a `v*` tag (for example `v1.1.0`) to
the default branch triggers the `publish` GitHub Actions workflow, which packs
`Arcp` and `Arcp.Cli` and pushes the resulting `.nupkg`/`.snupkg` artifacts to
[nuget.org](https://www.nuget.org/packages/Arcp). The SDK is versioned with
semantic versioning independently of the protocol version it speaks; a protocol
version bump is noted in the changelog when the negotiated ARCP version changes.

## License

By contributing, you agree that your contributions are licensed under the
project's [Apache-2.0](LICENSE) license.
