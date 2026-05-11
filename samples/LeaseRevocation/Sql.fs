/// SQL classifier — sqlglot-style parsing in production.
module ARCP.Samples.LeaseRevocation.Sql

type Op =
    | Read
    | Write
    | Ddl

type StatementClass = { Op: Op; Tables: Set<string> }

/// Real version: sqlparser-net or a port of sqlglot. Returns op + tables touched.
let classify (sql: string) : StatementClass =
    failwith "elided: sql parse → op + tables"
