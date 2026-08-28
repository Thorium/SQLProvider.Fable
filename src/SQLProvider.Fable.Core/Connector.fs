namespace SQLProvider.Fable

/// How a backend spells a parameter placeholder.
///
/// sqlx does not normalise these: PostgreSQL wants `$1`, MySQL and SQLite want
/// a bare `?`, and ADO.NET providers generally want a name. Query code in this
/// library is written once with `@name`, so the connector rewrites it on the way
/// down rather than making callers pick a vendor.
type Placeholder =
    /// `@name`, kept as written. ADO.NET.
    | Named
    /// `?` in the order the parameters are first used. MySQL, SQLite under sqlx.
    | Positional
    /// `$1`, `$2`, numbered by first use. PostgreSQL.
    | Numbered

/// Which engine a connector talks to.
///
/// Separate from `Placeholder` because the two do not line up: SQLite and MySQL
/// share the positional `?` marker but disagree about paging syntax and about
/// what half the string and date functions are called. The query builder needs
/// the engine; the parameter binder needs the marker.
type Vendor =
    | Sqlite
    | Postgres
    | MySql
    /// Some other engine reached over ADO.NET. Generates the most widely
    /// accepted SQL and no vendor-specific function names.
    | Generic

/// One bound parameter. `Name` is the bare name with no prefix; query code is
/// written with `@name` and `Dialect.bind` rewrites it into whatever the backend
/// wants.
type SqlParam = { Name: string; Value: SqlValue }

/// A fully materialised result set.
///
/// Both backends read eagerly, on purpose. A driver's row cursor borrows the
/// statement that produced it, so a lazily-advanced reader interface would need
/// a self-referential struct to hold both. SQLProvider's own
/// `Sql.dataReaderToArray` already materialises the whole reader before
/// projecting, so this gives up nothing it had.
type ResultSet =
    { Columns: string[]
      Rows: SqlValue[][] }

/// One row, carried with its result set so column names stay available.
/// Named SqlRow, not Row, so the `Row` module of column readers can keep that name.
type SqlRow = { Set: ResultSet; Index: int }

/// The whole native surface. Everything else in this library is portable F# on
/// top of these members.
///
/// Async-returning throughout: PostgreSQL and MySQL are async-native, and a
/// synchronous interface would park a thread per query. SQLite has no async to
/// offer, so its connector completes immediately — the cost there is writing
/// `let!` for work that never actually yields.
///
/// The transaction is three members on the connector rather than a separate
/// object, which is what both backends already model internally: ADO tracks the
/// open DbTransaction on the connection, and the sqlx side issues plain
/// BEGIN/COMMIT/ROLLBACK statements. It also stays clear of a Fable/Rust
/// limitation -- an `Async<'T>` whose `'T` is an interface generates a trait
/// object with no `Send + Sync` bound, which does not compile (G21).
///
/// Note it does NOT inherit IDisposable: object expressions implementing
/// IDisposable are only partly supported on Fable's Rust target, and `use` does
/// not call Dispose there at all. `Close` is explicit, and the .NET
/// implementation additionally implements IDisposable so `use` works there.
type ISqlConnector =
    /// How this backend spells placeholders. Query code always writes `@name`.
    abstract Placeholder: Placeholder
    /// Which engine this is, for the SQL the query builder generates.
    abstract Vendor: Vendor
    abstract Query: sql: string * ps: SqlParam[] -> Async<ResultSet>
    abstract Execute: sql: string * ps: SqlParam[] -> Async<int>
    abstract Scalar: sql: string * ps: SqlParam[] -> Async<SqlValue>
    abstract BeginTransaction: unit -> Async<unit>
    abstract Commit: unit -> Async<unit>
    abstract Rollback: unit -> Async<unit>
    abstract Close: unit -> unit

module Sql =

    let noParams: SqlParam[] = [||]

    let p name value = { Name = name; Value = value }
    let pText name (v: string) = { Name = name; Value = SqlText v }

    let pInt name (v: int) =
        { Name = name; Value = SqlInt(int64 v) }

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
