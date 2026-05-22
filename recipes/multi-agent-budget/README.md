# Multi-Agent Budget

This recipe ports the TypeScript `multi-agent-budget` pattern to F#.
A planner receives one `cost.budget` grant, emits `delegate` events for
worker sub-jobs, and debits its own remaining budget as each child grant
is allocated.

The runtime does not auto-spawn child jobs for `delegate` events; the
event records the delegation decision so a supervising system can launch
or audit the child work. The same lease subset and budget rules apply to
real child submissions.

Run it:

```sh
dotnet run --project recipes/multi-agent-budget/multi-agent-budget.fsproj
```
