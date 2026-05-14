namespace ARCP.Core

open System
open Cysharp

/// Strongly-typed identifier wrappers. Each is a `[<Struct>]` over a
/// single string so the runtime cost is one register and there is no
/// allocation for parameter passing.

[<Struct>]
type MessageId = MessageId of string with
    member this.Value = let (MessageId v) = this in v
    override this.ToString() = this.Value

[<Struct>]
type SessionId = SessionId of string with
    member this.Value = let (SessionId v) = this in v
    override this.ToString() = this.Value

[<Struct>]
type JobId = JobId of string with
    member this.Value = let (JobId v) = this in v
    override this.ToString() = this.Value

[<Struct>]
type ResultId = ResultId of string with
    member this.Value = let (ResultId v) = this in v
    override this.ToString() = this.Value

[<RequireQualifiedAccess>]
module MessageId =
    let newId () : MessageId = MessageId(Ulid.NewUlid().ToString())
    let ofString (s: string) : MessageId = MessageId s

[<RequireQualifiedAccess>]
module SessionId =
    let newId () : SessionId = SessionId("sess_" + Ulid.NewUlid().ToString())
    let ofString (s: string) : SessionId = SessionId s

[<RequireQualifiedAccess>]
module JobId =
    let newId () : JobId = JobId("job_" + Ulid.NewUlid().ToString())
    let ofString (s: string) : JobId = JobId s

[<RequireQualifiedAccess>]
module ResultId =
    let newId () : ResultId = ResultId("res_" + Ulid.NewUlid().ToString())
    let ofString (s: string) : ResultId = ResultId s
