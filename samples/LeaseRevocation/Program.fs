/// Warehouse DB admin agent. Reads pre-granted; writes prompt operator.
module ARCP.Samples.LeaseRevocation.Program

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Samples.LeaseRevocation.Sql

let preGranted =
    [ "public.orders"; "public.customers"; "warehouse.fct_revenue_daily" ]

let readLeaseSeconds = 60 * 60
let writeLeaseSeconds = 5 * 60

type LeaseHandle =
    {
        LeaseId: string
        ExpiresAt: DateTimeOffset
    }

type LeaseKey = string * Op
type LeaseCache = ConcurrentDictionary<LeaseKey, LeaseHandle>

/// Send permission.request and unwrap permission.grant; deny → raise.
let requestLease
    (client: Client)
    (permission: string)
    (table: string)
    (operation: Op)
    (seconds: int)
    (reason: string)
    : Task<LeaseHandle> =
    task {
        // Real call: client.RequestPermissionAsync(...).
        return failwith "elided: permission.request → lease handle"
    }

let opName =
    function
    | Read -> "read"
    | Write -> "write"
    | Ddl -> "ddl"

/// Authorize a SQL statement: pull leases for each touched table, reusing the cache.
let authorize (client: Client) (sql: string) (cache: LeaseCache) : Task<Op> =
    task {
        let klass = classify sql

        if Set.isEmpty klass.Tables then
            failwith "no table referenced"

        let seconds =
            if klass.Op = Read then
                readLeaseSeconds
            else
                writeLeaseSeconds

        for table in klass.Tables do
            let key = (table, klass.Op)
            let mutable cached = Unchecked.defaultof<_>

            let live =
                cache.TryGetValue(key, &cached) && cached.ExpiresAt > DateTimeOffset.UtcNow

            if not live then
                let! lease =
                    requestLease
                        client
                        (sprintf "db.%s" (opName klass.Op))
                        table
                        klass.Op
                        seconds
                        (sprintf "%s on %s: %s" (opName klass.Op).ToUpper table (sql.Substring(0, min 80 sql.Length)))

                cache.[key] <- lease

        return klass.Op
    }

/// Wire `lease.revoked` into the cache so the next call re-prompts.
let handleInbound (env: Envelope<JsonElement>) (cache: LeaseCache) : unit =
    if env.Type = "lease.revoked" then
        let lid = env.Payload.GetProperty("lease_id").GetString()

        for kv in cache do
            if kv.Value.LeaseId = lid then
                cache.TryRemove(kv.Key) |> ignore

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided
        let cache: LeaseCache = ConcurrentDictionary()
        use cts = new CancellationTokenSource()

        // Drain runtime events for lease.revoked / lease.extended.
        let drain =
            task {
                // In real code: subscribe to client's inbound event stream.
                // for env in client.Events do handleInbound env cache
                return ()
            }

        // Pre-grant the broad reads at session open. SELECT against these tables runs free.
        for table in preGranted do
            let! lease = requestLease client "db.read" table Read readLeaseSeconds "bootstrap"
            cache.[(table, Read)] <- lease

        // SELECT — covered by the bootstrap lease.
        let! _ = authorize client "SELECT count(*) FROM public.orders WHERE shipped_at::date = current_date - 1" cache

        // UPDATE — triggers permission.request; operator must approve.
        let! _ = authorize client "UPDATE public.orders SET status='refunded' WHERE id=4812" cache

        cts.Cancel()
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
