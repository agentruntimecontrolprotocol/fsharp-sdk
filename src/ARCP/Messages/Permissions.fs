namespace ARCP.Messages

open System
open ARCP.Ids

/// <summary>Permission &amp; lease payload records (RFC §15).</summary>
module Permissions =

    /// <summary><c>permission.request</c> payload (RFC §15.4).</summary>
    type PermissionRequest =
        {
            Permission: string
            Resource: string
            Operation: string
            Reason: string option
            RequestedLeaseSeconds: int option
        }

    /// <summary><c>permission.grant</c> payload (RFC §15.4).</summary>
    type PermissionGrant = { LeaseSeconds: int option }

    /// <summary><c>permission.deny</c> payload (RFC §15.4).</summary>
    type PermissionDenied = { Reason: string option }

    /// <summary><c>lease.granted</c> payload (RFC §15.5).</summary>
    type LeaseGranted =
        {
            LeaseId: LeaseId
            Permission: string
            Resource: string
            Operation: string
            ExpiresAt: DateTimeOffset
        }

    /// <summary><c>lease.extended</c> payload (RFC §15.5).</summary>
    type LeaseExtended =
        {
            LeaseId: LeaseId
            ExpiresAt: DateTimeOffset
        }

    /// <summary><c>lease.revoked</c> payload (RFC §15.5).</summary>
    type LeaseRevoked =
        {
            LeaseId: LeaseId
            Reason: string option
        }

    /// <summary><c>lease.refresh</c> payload (RFC §15.5).</summary>
    type LeaseRefresh =
        {
            LeaseId: LeaseId
            RequestedSeconds: int option
        }
