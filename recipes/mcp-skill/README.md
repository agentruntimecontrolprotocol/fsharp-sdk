# MCP Skill Bridge

This recipe ports the TypeScript `mcp-skill` pattern to F#. The sample
keeps the MCP-facing part deliberately small: `callResearchTool` is the
function an MCP tool handler would call after parsing its request.

The long-lived ARCP session belongs to the bridge process. Each MCP tool
call submits a fresh ARCP job with its own lease, cost cap, and result.

Run it:

```sh
dotnet run --project recipes/mcp-skill/mcp-skill.fsproj
```

In a production MCP server, keep the ARCP setup and replace `main` with
the handler registration from your MCP library.
