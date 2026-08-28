namespace SQLProvider.Fable.Js

open SQLProvider.Fable

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.JsInterop

/// Bindings to node:sqlite, the SQLite driver built into Node 22.5+.
///
/// Chosen over better-sqlite3 or node-sqlite3 because it ships with the runtime:
/// the suite runs with no npm install and nothing to compile. The API is
/// synchronous, which is why the connector below completes immediately rather
/// than actually yielding.
[<AutoOpen>]
module internal Native =

    /// `run` reports how many rows an INSERT/UPDATE/DELETE touched.
    type RunResult =
        abstract changes: float

    /// One column of a prepared statement's result shape. Available before the
    /// statement runs, so column names survive an empty result set.
    type ColumnInfo =
        abstract name: string

    type Statement =
        abstract columns: unit -> ColumnInfo[]

        /// Both take the bound values as separate arguments, hence the spread in
        /// the Emit: `Dialect.bind` has already put them in the right order.
        [<Emit("$0.all(...$1)")>]
        abstract all: obj[] -> obj[]

        [<Emit("$0.run(...$1)")>]
        abstract run: obj[] -> RunResult

    type Database =
        abstract prepare: string -> Statement
        abstract exec: string -> unit
        abstract close: unit -> unit

    [<Import("DatabaseSync", "node:sqlite")>]
    let DatabaseSync: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (ctor: obj) (path: string) : Database = jsNative

    let openDb (path: string) : Database = construct DatabaseSync path

module internal Conv =

    /// node:sqlite hands back JS values: null, number, bigint, string, or a
    /// Uint8Array for a BLOB. SQLite has no separate integer and float storage
    /// for a whole number, so an integral `number` reads back as SqlInt --
    /// `Row.float` widens it, which is why a REAL column still works.
    let toSqlValue (v: obj) : SqlValue =
        if isNull v then
            SqlNull
        else
            match jsTypeof v with
            | "number" ->
                let n: float = unbox v

                // Number.isInteger rather than Double.IsInteger, which Fable
                // does not map on this target.
                if emitJsExpr n "Number.isInteger($0)" then
                    SqlInt(int64 n)
                else
                    SqlFloat n
            | "bigint" -> SqlInt(unbox v)
            | "boolean" -> SqlBool(unbox v)
            | "string" -> SqlText(unbox v)
            | _ ->
                if emitJsExpr v "$0 instanceof Uint8Array" then
                    SqlBlob(unbox v)
                else
                    SqlText(string v)

    /// SQLite has no boolean, decimal, date or uuid type. Booleans go down as
    /// 0/1 the way the engine stores them; the other three are encoded to text
    /// through the shared Convert helpers, so every backend writes identical
    /// bytes for the same value.
    let toJsValue (v: SqlValue) : obj =
        match v with
        | SqlNull -> null
        | SqlBool b -> box (if b then 1 else 0)
        | SqlInt i -> box (float i)
        | SqlFloat f -> box f
        | SqlText s -> box s
        | SqlBlob b -> box b
        | SqlDecimal d -> box (Convert.decimalToText d)
        | SqlDate d -> box (Convert.dateToText d)
        | SqlGuid g -> box (Convert.guidToText g)

    let toResultSet (stmt: Statement) (rows: obj[]) : ResultSet =
        let columns = stmt.columns () |> Array.map (fun c -> c.name)

        let cells =
            rows
            |> Array.map (fun row ->
                // Read by name rather than by Object.values, so the row array
                // lines up with `columns` even if the driver ever reorders.
                columns |> Array.map (fun name -> toSqlValue (row?(name))))

        { Columns = columns; Rows = cells }

/// node:sqlite implementation of ISqlConnector.
///
/// The path is a file name, or ":memory:" for a private in-memory database that
/// lives exactly as long as the connector.
///
/// Placeholders are positional: query code is written with `@name` and
/// `Dialect.bind` rewrites it, the same as on the Rust backends. Transactions
/// are plain statements. `Close` is explicit; the connector does not implement
/// IDisposable, to stay identical to the other targets.
type SqliteConnector(path: string) =

    let db = openDb path

    // The driver is synchronous, so every member here completes without ever
    // yielding. The async signature is the shared contract, not a claim that
    // this backend overlaps work.
    let ret x = async { return x }

    interface ISqlConnector with

        member _.Placeholder = Positional
        member _.Vendor = Sqlite

        member _.Query(sql, ps) =
            let sql, values = Dialect.bind Positional sql ps
            let stmt = db.prepare sql
            let args = values |> Array.map Conv.toJsValue
            ret (Conv.toResultSet stmt (stmt.all args))

        member _.Execute(sql, ps) =
            let sql, values = Dialect.bind Positional sql ps
            let stmt = db.prepare sql
            let args = values |> Array.map Conv.toJsValue
            ret (int (stmt.run args).changes)

        member this.Scalar(sql, ps) =
            async {
                let! rs = (this :> ISqlConnector).Query(sql, ps)

                if rs.Rows.Length = 0 || rs.Columns.Length = 0 then
                    return SqlNull
                else
                    return rs.Rows.[0].[0]
            }

        member _.BeginTransaction() = ret (db.exec "BEGIN")
        member _.Commit() = ret (db.exec "COMMIT")
        member _.Rollback() = ret (db.exec "ROLLBACK")
        member _.Close() = db.close ()

#else

/// .NET stub so the project still builds outside Fable (for tooling, the IDE and
/// a plain solution build). Every member is unreachable: use
/// SQLProvider.Fable.Ado on .NET.
type SqliteConnector(path: string) =

    interface ISqlConnector with
        member _.Placeholder = Positional
        member _.Vendor = Sqlite

        member _.Query(_, _) =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.Execute(_, _) =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.Scalar(_, _) =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.BeginTransaction() =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.Commit() =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.Rollback() =
            failwith "SqliteConnector only exists on the Fable/JavaScript target"

        member _.Close() = ()

#endif
