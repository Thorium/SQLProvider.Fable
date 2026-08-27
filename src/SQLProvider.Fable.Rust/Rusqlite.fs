namespace SQLProvider.Fable.Rust

open SQLProvider.Fable

#if FABLE_COMPILER
open Fable.Core
open Fable.Core.Rust

/// Bindings to sqlite_native.rs.
///
/// Only primitives cross this boundary. The .rs shim never sees a Fable-generated
/// `SqlValue`, so a change in how Fable lays out unions cannot break it -- values
/// travel as an integer tag plus a typed accessor call.
///
/// The emitted types are wrapped in `Rc` deliberately: Fable clones values freely,
/// and neither `rusqlite::Connection` nor the result buffers are `Clone`.
[<AutoOpen>]
module internal Native =

    // Pulls the hand-written shim into the generated crate, the same way
    // fable-library-rust's lib.fs pulls in its own .rs files. Never called.
    let private _imports () =
        importAll "./sqlite_native.rs"
        ()

    [<Erase; Emit("std::rc::Rc<crate::sqlite_native::RawResult>")>]
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

    /// `Params` needs interior mutability because F# accumulates into it one call
    /// at a time, so each member emits its own `borrow_mut()`.
    [<Erase; Emit("std::rc::Rc<std::cell::RefCell<crate::sqlite_native::Params>>")>]
    type Params =
        [<Emit("$0.borrow_mut().push_null($1)")>]
        abstract PushNull: string -> unit

        [<Emit("$0.borrow_mut().push_bool($1, $2)")>]
        abstract PushBool: string * bool -> unit

        [<Emit("$0.borrow_mut().push_int($1, $2)")>]
        abstract PushInt: string * int64 -> unit

        [<Emit("$0.borrow_mut().push_float($1, $2)")>]
        abstract PushFloat: string * float -> unit

        [<Emit("$0.borrow_mut().push_text($1, $2)")>]
        abstract PushText: string * string -> unit

        [<Emit("$0.borrow_mut().push_blob($1, $2)")>]
        abstract PushBlob: string * byte[] -> unit

    [<Erase; Emit("std::rc::Rc<crate::sqlite_native::Db>")>]
    type Db =
        [<Emit("std::rc::Rc::new($0.query($1, &$2))")>]
        abstract Query: string * Params -> RawResult

        [<Emit("$0.execute($1, &$2)")>]
        abstract Execute: string * Params -> int

    [<Emit("std::rc::Rc::new(crate::sqlite_native::Db::open($0))")>]
    let openDb (path: string) : Db = nativeOnly

    [<Emit("std::rc::Rc::new(std::cell::RefCell::new(crate::sqlite_native::Params::new()))")>]
    let newParams () : Params = nativeOnly

module internal Conv =

    let toNative (ps: SqlParam[]) : Params =
        let p = newParams ()

        for x in ps do
            match x.Value with
            | SqlNull -> p.PushNull x.Name
            | SqlBool v -> p.PushBool(x.Name, v)
            | SqlInt v -> p.PushInt(x.Name, v)
            | SqlFloat v -> p.PushFloat(x.Name, v)
            | SqlText v -> p.PushText(x.Name, v)
            | SqlBlob v -> p.PushBlob(x.Name, v)

        p

    /// Top-level rather than a lambda inside the loop below: Fable's Rust backend
    /// moves a captured value into a nested closure without cloning it first, so
    /// a doubly-nested `Array.init` over `raw` does not borrow-check.
    let private cell (raw: RawResult) (r: int) (c: int) : SqlValue =
        match raw.kind (r, c) with
        | 0 -> SqlNull
        | 1 -> SqlBool(raw.get_bool (r, c))
        | 2 -> SqlInt(raw.get_int (r, c))
        | 3 -> SqlFloat(raw.get_float (r, c))
        | 4 -> SqlText(raw.get_text (r, c))
        | 5 -> SqlBlob(raw.get_blob (r, c))
        | k -> failwith ("Unknown SQLite value tag: " + string k)

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

    let query (db: Db) (sql: string) (ps: SqlParam[]) =
        Conv.toResultSet (db.Query(sql, Conv.toNative ps))

    let execute (db: Db) (sql: string) (ps: SqlParam[]) = db.Execute(sql, Conv.toNative ps)

    let statement (db: Db) (sql: string) = db.Execute(sql, newParams ()) |> ignore

    let scalar (db: Db) (sql: string) (ps: SqlParam[]) =
        let rs = query db sql ps

        if rs.Rows.Length = 0 || rs.Columns.Length = 0 then
            SqlNull
        else
            rs.Rows.[0].[0]

/// A concrete class rather than an object expression: an object expression that
/// calls back into its enclosing object generates a struct field literally named
/// `_` on Fable's Rust backend, which is a reserved identifier there.
type internal RusqliteTransaction(db: Db) =

    interface ISqlTransaction with
        member _.Commit() = Impl.statement db "COMMIT"
        member _.Rollback() = Impl.statement db "ROLLBACK"

/// rusqlite implementation of ISqlConnector.
///
/// SQLite has no connection state machine to model: rusqlite opens on construct
/// and closes on drop, so `Close` is a no-op and nothing corresponds to ADO's
/// ConnectionState. Transactions are plain statements rather than a driver
/// object, which keeps the borrow checker out of the interface.
type RusqliteConnector(path: string) =

    let db = openDb path

    interface ISqlConnector with
        member _.Query(sql, ps) = Impl.query db sql ps
        member _.Execute(sql, ps) = Impl.execute db sql ps
        member _.Scalar(sql, ps) = Impl.scalar db sql ps

        member _.BeginTransaction() =
            Impl.statement db "BEGIN"
            RusqliteTransaction(db) :> ISqlTransaction

        member _.Close() = ()

#else

/// .NET stub so the project still builds outside Fable (for tooling, the IDE and
/// a plain solution build). Every member is unreachable: use
/// SQLProvider.Fable.Ado on .NET.
type RusqliteConnector(path: string) =

    interface ISqlConnector with
        member _.Query(_, _) = failwith "RusqliteConnector only exists on the Fable/Rust target"
        member _.Execute(_, _) = failwith "RusqliteConnector only exists on the Fable/Rust target"
        member _.Scalar(_, _) = failwith "RusqliteConnector only exists on the Fable/Rust target"

        member _.BeginTransaction() =
            failwith "RusqliteConnector only exists on the Fable/Rust target"

        member _.Close() = ()

#endif
