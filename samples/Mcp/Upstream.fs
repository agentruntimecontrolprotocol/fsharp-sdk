/// Upstream MCP server invocation.
///
/// Real version parameterizes command, args, env via your config layer.
/// Reference servers from the modelcontextprotocol org publish under
/// `mcp-server-*` (filesystem, git, postgres, slack, ...).
module ARCP.Samples.Mcp.Upstream

type StdioServerParameters = { Command: string; Args: string list }

let upstreamParams () : StdioServerParameters =
    {
        Command = "uvx"
        Args = [ "mcp-server-filesystem"; "/srv/data" ]
    }
