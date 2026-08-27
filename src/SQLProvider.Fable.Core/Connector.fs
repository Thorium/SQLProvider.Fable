namespace SQLProvider.Fable

/// One bound parameter. `Name` is the bare name with no prefix; SQL text uses
/// `@name` on every backend (rusqlite accepts `@`, `:` and `$`; Microsoft.Data.Sqlite
/// accepts `@`), and each connector adds whatever marker its driver wants.
type SqlParam = { Name: string; Value: SqlValue }

/// A fully materialised result set.
///
/// Both backends read eagerly, on purpose. rusqlite ties the lifetime of a `Rows`
/// iterator to the `Statement` that produced it, so a lazily-advanced reader
/// interface would need a self-referential struct to hold both. SQLProvider's own
/// `Sql.dataReaderToArray` already materialises the whole reader into an array
/// before projecting, so this gives up nothing it had.
type ResultSet =
    { Columns: string[]
      Rows: SqlValue[][] }

/// One row, carried with its result set so column names stay available.
/// Named SqlRow, not Row, so the `Row` module of column readers can keep that name.
type SqlRow = { Set: ResultSet; Index: int }

type ISqlTransaction =
    abstract Commit: unit -> unit
    abstract Rollback: unit -> unit

/// The whole native surface. Everything else in this library is portable F# on
/// top of these five members.
///
/// Note it does NOT inherit IDisposable: object expressions implementing
/// IDisposable are only partly supported on Fable's Rust target (several such
/// cases are commented out in Fable's own MiscTests). `Close` is explicit, and
/// the .NET implementation additionally implements IDisposable so `use` works there.
type ISqlConnector =
    abstract Query: sql: string * ps: SqlParam[] -> ResultSet
    abstract Execute: sql: string * ps: SqlParam[] -> int
    abstract Scalar: sql: string * ps: SqlParam[] -> SqlValue
    abstract BeginTransaction: unit -> ISqlTransaction
    abstract Close: unit -> unit

module Sql =

    let noParams: SqlParam[] = [||]

    let p name value = { Name = name; Value = value }
    let pText name (v: string) = { Name = name; Value = SqlText v }
    let pInt name (v: int) = { Name = name; Value = SqlInt(int64 v) }
    let pInt64 name (v: int64) = { Name = name; Value = SqlInt v }
    let pFloat name (v: float) = { Name = name; Value = SqlFloat v }
    let pBool name (v: bool) = { Name = name; Value = SqlBool v }
    let pNull name = { Name = name; Value = SqlNull }

    /// Text of an optional parameter, mapping None to SQL NULL.
    let pTextOpt name (v: string option) =
        match v with
        | Some s -> { Name = name; Value = SqlText s }
        | None -> { Name = name; Value = SqlNull }

module ResultSet =

    let empty = { Columns = [||]; Rows = [||] }

    let rowCount (rs: ResultSet) = rs.Rows.Length

    let rows (rs: ResultSet) : SqlRow[] =
        rs.Rows |> Array.mapi (fun i _ -> { Set = rs; Index = i })

    /// Map every row through a mapper. This is the shape generated code targets:
    /// `ResultSet.map Customer.ofRow rs`.
    let map (f: SqlRow -> 'T) (rs: ResultSet) : 'T[] = rows rs |> Array.map f

    /// First row, or None on an empty set.
    let tryHead (rs: ResultSet) : SqlRow option =
        if rs.Rows.Length = 0 then
            None
        else
            Some { Set = rs; Index = 0 }
