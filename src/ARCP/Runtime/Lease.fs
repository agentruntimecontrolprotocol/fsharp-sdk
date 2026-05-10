namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Permissions
open ARCP.Messages.Registry

/// <summary>
/// Permission lease bookkeeping (RFC §15.5). The <see cref="LeaseManager"/>
/// owns a thread-safe registry of leases keyed by <see cref="LeaseId"/>,
/// emits <c>lease.granted</c>/<c>lease.extended</c>/<c>lease.revoked</c>
/// envelopes through a supplied send callback, and runs a background
/// sweeper that revokes expired leases.
/// </summary>
type Lease =
    {
        /// <summary>Stable identifier (RFC §15.5).</summary>
        LeaseId: LeaseId
        /// <summary>Permission namespace (e.g. <c>fs.write</c>).</summary>
        Permission: string
        /// <summary>Resource scope (e.g. a path).</summary>
        Resource: string
        /// <summary>Logical operation (e.g. <c>append</c>).</summary>
        Operation: string
        /// <summary>Principal the lease was granted to.</summary>
        Principal: string
        /// <summary>When the grant was issued.</summary>
        GrantedAt: DateTimeOffset
        /// <summary>Expiry time (sweeper revokes past this).</summary>
        mutable ExpiresAt: DateTimeOffset
        /// <summary>True once the lease has been revoked (terminal).</summary>
        mutable Revoked: bool
        /// <summary>Reason captured at revocation time, if any.</summary>
        mutable RevokedReason: string option
    }

/// <summary>
/// Manages the lifecycle of permission leases (RFC §15.5). All time
/// comparisons go through the supplied <see cref="TimeProvider"/>; the
/// background sweeper uses <c>TimeProvider.CreateTimer</c> so tests can drive
/// it with a fake clock.
/// </summary>
type LeaseManager(timeProvider: TimeProvider, send: Envelope<MessageType> -> Task, ?sweepInterval: TimeSpan) =

    let sweepInterval = defaultArg sweepInterval (TimeSpan.FromSeconds 5.0)
    let leases = ConcurrentDictionary<LeaseId, Lease>()

    let withSession (sid: SessionId option) (env: Envelope<MessageType>) =
        match sid with
        | Some s -> env |> Envelope.withSession s
        | None -> env

    let withCorr (corr: MessageId option) (env: Envelope<MessageType>) =
        match corr with
        | Some c -> env |> Envelope.withCorrelation c
        | None -> env

    let emit (env: Envelope<MessageType>) : Task = send env

    let revokeInternal (lease: Lease) (reason: string) : Task =
        task {
            if not lease.Revoked then
                lease.Revoked <- true
                lease.RevokedReason <- Some reason

                let env =
                    Envelopes.leaseRevoked
                        {
                            LeaseId = lease.LeaseId
                            Reason = Some reason
                        }

                do! emit env
        }
        :> Task

    let sweep () : Task =
        task {
            let now = timeProvider.GetUtcNow()

            for kv in leases do
                let lease = kv.Value

                if not lease.Revoked && lease.ExpiresAt <= now then
                    do! revokeInternal lease "expired"
        }
        :> Task

    let mutable timer: ITimer option = None

    let startSweeper () =
        let cb = TimerCallback(fun _ -> sweep().GetAwaiter().GetResult())
        let t = timeProvider.CreateTimer(cb, null, sweepInterval, sweepInterval)
        timer <- Some t

    do startSweeper ()

    /// <summary>
    /// Allocate a fresh lease, record it, and emit <c>lease.granted</c>.
    /// </summary>
    member _.GrantAsync
        (
            permission: string,
            resource: string,
            operation: string,
            principal: string,
            leaseSeconds: int,
            sessionId: SessionId option,
            ?correlationId: MessageId
        ) : Task<Lease> =
        task {
            let now = timeProvider.GetUtcNow()
            let expiresAt = now.AddSeconds(float leaseSeconds)

            let lease =
                {
                    LeaseId = LeaseId.create ()
                    Permission = permission
                    Resource = resource
                    Operation = operation
                    Principal = principal
                    GrantedAt = now
                    ExpiresAt = expiresAt
                    Revoked = false
                    RevokedReason = None
                }

            leases.[lease.LeaseId] <- lease

            let env =
                Envelopes.leaseGranted
                    {
                        LeaseId = lease.LeaseId
                        Permission = permission
                        Resource = resource
                        Operation = operation
                        ExpiresAt = expiresAt
                    }
                |> withSession sessionId
                |> withCorr correlationId

            do! emit env
            return lease
        }

    /// <summary>
    /// Push the lease's expiry forward by <paramref name="additionalSeconds"/>
    /// and emit <c>lease.extended</c>. Fails if the lease has been revoked or
    /// expired.
    /// </summary>
    member _.ExtendAsync(leaseId: LeaseId, additionalSeconds: int) : Task<Result<Lease, ARCPError>> =
        task {
            match leases.TryGetValue leaseId with
            | false, _ -> return Error(NotFound(sprintf "lease %s" (LeaseId.value leaseId)))
            | true, lease ->
                if lease.Revoked then
                    return Error(ARCPError.LeaseRevoked(leaseId, lease.RevokedReason |> Option.defaultValue "revoked"))
                else
                    let now = timeProvider.GetUtcNow()

                    if lease.ExpiresAt <= now then
                        return Error(ARCPError.LeaseExpired(leaseId, lease.ExpiresAt))
                    else
                        lease.ExpiresAt <- lease.ExpiresAt.AddSeconds(float additionalSeconds)

                        let env =
                            Envelopes.leaseExtended
                                {
                                    LeaseId = leaseId
                                    ExpiresAt = lease.ExpiresAt
                                }

                        do! emit env
                        return Ok lease
        }

    /// <summary>
    /// Revoke a lease with a reason, emitting <c>lease.revoked</c> once. A
    /// second call is a no-op success.
    /// </summary>
    member _.RevokeAsync(leaseId: LeaseId, reason: string) : Task<Result<unit, ARCPError>> =
        task {
            match leases.TryGetValue leaseId with
            | false, _ -> return Error(NotFound(sprintf "lease %s" (LeaseId.value leaseId)))
            | true, lease ->
                do! revokeInternal lease reason
                return Ok()
        }

    /// <summary>
    /// Inspect the lease's current validity (RFC §15.5). Returns
    /// <see cref="LeaseExpired"/> or <see cref="LeaseRevoked"/> for terminal
    /// leases.
    /// </summary>
    member _.CheckAsync(leaseId: LeaseId) : Result<Lease, ARCPError> =
        match leases.TryGetValue leaseId with
        | false, _ -> Error(NotFound(sprintf "lease %s" (LeaseId.value leaseId)))
        | true, lease ->
            if lease.Revoked then
                Error(ARCPError.LeaseRevoked(leaseId, lease.RevokedReason |> Option.defaultValue "revoked"))
            elif lease.ExpiresAt <= timeProvider.GetUtcNow() then
                Error(ARCPError.LeaseExpired(leaseId, lease.ExpiresAt))
            else
                Ok lease

    /// <summary>
    /// Find a non-terminal lease matching the given (principal, permission,
    /// resource, operation) tuple, if any.
    /// </summary>
    member _.GetByOperation(principal: string, permission: string, resource: string, operation: string) : Lease option =
        leases
        |> Seq.tryPick (fun kv ->
            let l = kv.Value

            if
                l.Principal = principal
                && l.Permission = permission
                && l.Resource = resource
                && l.Operation = operation
                && not l.Revoked
                && l.ExpiresAt > timeProvider.GetUtcNow()
            then
                Some l
            else
                None)

    /// <summary>Run the sweeper once synchronously (test affordance).</summary>
    member _.SweepNowAsync() : Task = sweep ()

    /// <summary>Total registered leases (terminal or not).</summary>
    member _.Count: int = leases.Count

    interface IDisposable with
        member _.Dispose() =
            match timer with
            | Some t -> t.Dispose()
            | None -> ()
