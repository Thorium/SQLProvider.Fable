namespace SQLProvider.Fable.Rust

open SQLProvider.Fable

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.Rust

/// Bindings to sqlx_native.rs.
///
/// Only primitives cross this boundary. The .rs shim never sees a
/// Fable-generated `SqlValue`, so a change in how Fable lays out unions cannot
/// break it -- values travel as an integer tag plus a typed accessor call.
///
/// The emitted types are wrapped in `Arc` deliberately: Fable clones values
/// freely, neither a sqlx connection nor the result buffers are `Clone`, and
/// under the `threaded` feature (which `Async` requires) anything captured by an
/// async block must be `Send + Sync`.
[<AutoOpen>]
module internal Native =

    // Pulls the hand-written shim into the generated crate, the same way
    // fable-library-rust's lib.fs pulls in its own .rs files. Never called.
    let private _imports () =
        importAll "./sqlx_native.rs"
        ()

    [<Erase; Emit("std::sync::Arc<crate::sqlx_native::RawResult>")>]
    type RawResult =
        abstract col_count: unit -> int
        abstract row_count: unit -> int
        abstract col_name: int -> string
        abstract kind: int * int -> int
        abstract get_bool: int * int -> bool
        abstract get_int: int * int -> int64
        abstract get_float: int * int -> float
        abstract get_text: int * int -> string
        abstract get_blob: int * int -> byte[]

    /// `Params` needs interior mutability because F# accumulates into it one
    /// call at a time, so each member emits its own `lock()`.
    [<Erase; Emit("std::sync::Arc<std::sync::Mutex<crate::sqlx_native::Params>>")>]
    type Params =
        [<Emit("$0.lock().unwrap().push_null()")>]
        abstract PushNull: unit -> unit

        [<Emit("$0.lock().unwrap().push_bool($1)")>]
        abstract PushBool: bool -> unit

        [<Emit("$0.lock().unwrap().push_int($1)")>]
        abstract PushInt: int64 -> unit

        [<Emit("$0.lock().unwrap().push_float($1)")>]
        abstract PushFloat: float -> unit

        [<Emit("$0.lock().unwrap().push_text($1)")>]
        abstract PushText: string -> unit

        [<Emit("$0.lock().unwrap().push_blob($1)")>]
        abstract PushBlob: byte[] -> unit

    [<Erase; Emit("std::sync::Arc<crate::sqlx_native::Db>")>]
    type Db =
        [<Emit("$0.query($1, &$2)")>]
        abstract Query: string * Params -> Async<RawResult>

        [<Emit("$0.execute($1, &$2)")>]
        abstract Execute: string * Params -> Async<int>

    [<Emit("std::sync::Arc::new(crate::sqlx_native::Db::open($0))")>]
    let openDb (url: string) : Db = nativeOnly

    [<Emit("std::sync::Arc::new(std::sync::Mutex::new(crate::sqlx_native::Params::new()))")>]
    let newParams () : Params = nativeOnly

module internal Conv =

    /// The parameters are already in the order the rewritten SQL expects, and
    /// their names are gone from it, so they are pushed positionally.
    let toNative (vs: SqlValue[]) : Params =
        let p = newParams ()

        for v in vs do
            match v with
            | SqlNull -> p.PushNull()
            | SqlBool x -> p.PushBool x
            | SqlInt x -> p.PushInt x
            | SqlFloat x -> p.PushFloat x
            | SqlText x -> p.PushText x
            | SqlBlob x -> p.PushBlob x
            // Encoded to TEXT here rather than given a native tag: sqlx's Any
            // driver has no decimal, date or uuid kind, and the ADO backend
            // encodes the same three the same way, so both write identical
            // bytes.
            | SqlDecimal x -> p.PushText(Convert.decimalToText x)
            | SqlDate x -> p.PushText(Convert.dateToText x)
            | SqlGuid x -> p.PushText(Convert.guidToText x)

        p

    /// Top-level rather than a lambda inside the loop below: Fable's Rust
    /// backend moves a captured value into a nested closure without cloning it
    /// first, so a doubly-nested `Array.init` over `raw` does not borrow-check.
    let private cell (raw: RawResult) (r: int) (c: int) : SqlValue =
        match raw.kind (r, c) with
        | 0 -> SqlNull
        | 1 -> SqlBool(raw.get_bool (r, c))
        | 2 -> SqlInt(raw.get_int (r, c))
        | 3 -> SqlFloat(raw.get_float (r, c))
        | 4 -> SqlText(raw.get_text (r, c))
        | 5 -> SqlBlob(raw.get_blob (r, c))
        | k -> failwith ("Unknown sqlx value tag: " + string k)

    let toResultSet (raw: RawResult) : ResultSet =
        let colCount = raw.col_count ()
        let rowCount = raw.row_count ()

        let columns = Array.create colCount ""

        for c in 0 .. colCount - 1 do
            columns.[c] <- raw.col_name c

        let rows = Array.create rowCount [||]

        for r in 0 .. rowCount - 1 do
            let row = Array.create colCount SqlNull

            for c in 0 .. colCount - 1 do
                row.[c] <- cell raw r c

            rows.[r] <- row

        { Columns = columns; Rows = rows }

/// Free functions rather than members, so nothing needs to cast `this` back to
/// ISqlConnector -- Fable's Rust backend emits a plain `as` for that self-cast,
/// which does not compile.
module internal Impl =

    let query (db: Db) (style: Placeholder) (sql: string) (ps: SqlParam[]) =
        async {
            let sql, values = Dialect.bind style sql ps
            let! raw = db.Query(sql, Conv.toNative values)
            return Conv.toResultSet raw
        }

    let execute (db: Db) (style: Placeholder) (sql: string) (ps: SqlParam[]) =
        async {
            let sql, values = Dialect.bind style sql ps
            return! db.Execute(sql, Conv.toNative values)
        }

    /// A statement with no parameters, run for its effect. Used for the
    /// transaction verbs, which are plain SQL here rather than a driver object.
    let statement (db: Db) (sql: string) =
        async {
            let! _ = db.Execute(sql, newParams ())
            return ()
        }

    let scalar (db: Db) (style: Placeholder) (sql: string) (ps: SqlParam[]) =
        async {
            let! rs = query db style sql ps

            if rs.Rows.Length = 0 || rs.Columns.Length = 0 then
                return SqlNull
            else
                return rs.Rows.[0].[0]
        }

/// sqlx implementation of ISqlConnector, for SQLite, PostgreSQL and
/// MySQL/MariaDB.
///
/// The URL picks the engine: `sqlite::memory:`, `sqlite://path/to.db`,
/// `postgres://user:pw@host/db`, `mysql://user:pw@host/db`. It also picks the
/// placeholder style, so the SQL handed to Query/Execute is always written with
/// `@name` regardless of backend.
///
/// There is no connection state machine to model: sqlx connects on construct and
/// closes on drop, so `Close` is a no-op and nothing corresponds to ADO's
/// ConnectionState. Transactions are plain statements rather than a driver
/// object, which keeps the borrow checker out of the interface.
type SqlxConnector(url: string) =

    let db = openDb url
    let style = Dialect.forUrl url
    let engine = Dialect.vendorOf url

    interface ISqlConnector with
        member _.Placeholder = style
        member _.Vendor = engine
        member _.Query(sql, ps) = Impl.query db style sql ps
        member _.Execute(sql, ps) = Impl.execute db style sql ps
        member _.Scalar(sql, ps) = Impl.scalar db style sql ps
        member _.BeginTransaction() = Impl.statement db "BEGIN"
        member _.Commit() = Impl.statement db "COMMIT"
        member _.Rollback() = Impl.statement db "ROLLBACK"
        member _.Close() = ()

#else

/// .NET stub so the project still builds outside Fable (for tooling, the IDE and
/// a plain solution build). Every member is unreachable: use
/// SQLProvider.Fable.Ado on .NET.
type SqlxConnector(url: string) =

    let style = Dialect.forUrl url
    let engine = Dialect.vendorOf url

    interface ISqlConnector with
        member _.Placeholder = style
        member _.Vendor = engine

        member _.Query(_, _) =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.Execute(_, _) =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.Scalar(_, _) =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.BeginTransaction() =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.Commit() =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.Rollback() =
            failwith "SqlxConnector only exists on the Fable/Rust target"

        member _.Close() = ()

#endif
