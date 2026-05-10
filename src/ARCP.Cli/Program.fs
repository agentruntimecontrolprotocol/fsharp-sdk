module ARCP.Cli.Program

[<EntryPoint>]
let main _argv =
    printfn "arcp v%s — protocol %s (skeleton)" ARCP.Version.Sdk ARCP.Version.Protocol
    0
