/// Typed column readers.
///
/// These exist because reflection cannot do the job on the Rust target: Fable's
/// `FSharpPropertyInfo` carries only a name (see fable-library-rust
/// QuotationTypes.fs), so `PropertyInfo.PropertyType` is unavailable and a
/// generic "convert each column to whatever the record field wants" mapper has
/// nothing to dispatch on. Mappers therefore state the conversion explicitly --
/// which is what a code generator emits anyway, and it is faster and clearer
/// than reflection on .NET too.
module SQLProvider.Fable.Row

open SQLProvider.Fable

let private lower (s: string) = s.ToLower()

/// Column index by name, case-insensitive. Returns -1 when absent.
let tryOrdinal (rs: ResultSet) (name: string) =
    let target = lower name
    let mutable found = -1
    let mutable i = 0

    while found < 0 && i < rs.Columns.Length do
        if lower rs.Columns.[i] = target then
            found <- i

        i <- i + 1

    found

let ordinal (rs: ResultSet) (name: string) =
    match tryOrdinal rs name with
    | -1 -> failwith ("No column named '" + name + "' in result set")
    | i -> i

/// Raw value of a named column.
let value (r: SqlRow) (name: string) : SqlValue =
    let i = ordinal r.Set name
    r.Set.Rows.[r.Index].[i]

/// Raw value by position.
let valueAt (r: SqlRow) (index: int) : SqlValue = r.Set.Rows.[r.Index].[index]

let private wrongType (name: string) (expected: string) (v: SqlValue) : 'T =
    failwith ("Column '" + name + "': expected " + expected + " but got " + SqlValue.typeName v)

let private nullNotAllowed (name: string) (expected: string) : 'T =
    failwith ("Column '" + name + "' is NULL; use the *Opt reader to allow it (" + expected + ")")

// --- int64 --------------------------------------------------------------

let int64Opt (r: SqlRow) (name: string) : int64 option =
    match value r name with
    | SqlNull -> None
    | SqlInt v -> Some v
    | SqlBool b -> Some(if b then 1L else 0L)
    | v -> wrongType name "int" v

let int64 (r: SqlRow) (name: string) : int64 =
    match int64Opt r name with
    | Some v -> v
    | None -> nullNotAllowed name "int"

// --- int ----------------------------------------------------------------

let intOpt (r: SqlRow) (name: string) : int option =
    int64Opt r name |> Option.map int

let int (r: SqlRow) (name: string) : int =
    match intOpt r name with
    | Some v -> v
    | None -> nullNotAllowed name "int"

// --- float --------------------------------------------------------------

let floatOpt (r: SqlRow) (name: string) : float option =
    match value r name with
    | SqlNull -> None
    | SqlFloat v -> Some v
    // SQLite hands back an integer storage class for a whole number in a REAL
    // column, so widening here is correctness, not convenience.
    | SqlInt v -> Some(float v)
    | v -> wrongType name "float" v

let float (r: SqlRow) (name: string) : float =
    match floatOpt r name with
    | Some v -> v
    | None -> nullNotAllowed name "float"

// --- text ---------------------------------------------------------------

let textOpt (r: SqlRow) (name: string) : string option =
    match value r name with
    | SqlNull -> None
    | SqlText v -> Some v
    | v -> wrongType name "text" v

let text (r: SqlRow) (name: string) : string =
    match textOpt r name with
    | Some v -> v
    | None -> nullNotAllowed name "text"

// --- bool ---------------------------------------------------------------

let boolOpt (r: SqlRow) (name: string) : bool option =
    match value r name with
    | SqlNull -> None
    | SqlBool v -> Some v
    | SqlInt v -> Some(v <> 0L)
    | v -> wrongType name "bool" v

let bool (r: SqlRow) (name: string) : bool =
    match boolOpt r name with
    | Some v -> v
    | None -> nullNotAllowed name "bool"

// --- blob ---------------------------------------------------------------

let blobOpt (r: SqlRow) (name: string) : byte[] option =
    match value r name with
    | SqlNull -> None
    | SqlBlob v -> Some v
    | v -> wrongType name "blob" v

let blob (r: SqlRow) (name: string) : byte[] =
    match blobOpt r name with
    | Some v -> v
    | None -> nullNotAllowed name "blob"
