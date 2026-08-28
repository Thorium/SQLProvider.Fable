namespace SQLProvider.Fable.Ado

open System
open System.Data
open System.Data.Common
open SQLProvider.Fable

/// ADO.NET implementation of ISqlConnector.
///
/// This is the whole of System.Data the runtime needs: DbConnection
/// (OpenAsync/Close/State/CreateCommand), DbCommand (CommandText/Parameters/
/// ExecuteReaderAsync/ExecuteScalarAsync/ExecuteNonQueryAsync), DbParameter
/// (ParameterName/Value) and DbDataReader (ReadAsync/FieldCount/GetName/
/// GetValue). Everything else in SQLProvider's ISqlProvider is schema
/// introspection, which stays on .NET at code-generation time and never reaches
/// a Fable target.
///
/// DbConnection rather than IDbConnection because IDbConnection has no async
/// surface at all. Every real provider derives from DbConnection.
module private Conv =

    let toSqlValue (v: obj) : SqlValue =
        match v with
        | null -> SqlNull
        | :? DBNull -> SqlNull
        | :? bool as b -> SqlBool b
        | :? int8 as i -> SqlInt(int64 i)
        | :? uint8 as i -> SqlInt(int64 i)
        | :? int16 as i -> SqlInt(int64 i)
        | :? uint16 as i -> SqlInt(int64 i)
        | :? int32 as i -> SqlInt(int64 i)
        | :? uint32 as i -> SqlInt(int64 i)
        | :? int64 as i -> SqlInt i
        | :? float32 as f -> SqlFloat(float f)
        | :? float as f -> SqlFloat f
        | :? decimal as d -> SqlDecimal d
        | :? string as s -> SqlText s
        | :? (byte[]) as b -> SqlBlob b
        // Drivers that carry these natively (SQL Server, PostgreSQL) hand back
        // real CLR values; SQLite returns TEXT, which Row parses on read.
        | :? DateTime as d -> SqlDate d
        | :? Guid as g -> SqlGuid g
        | other -> SqlText(string other)

    let toDbValue (v: SqlValue) : obj =
        match v with
        | SqlNull -> box DBNull.Value
        | SqlBool b -> box b
        | SqlInt i -> box i
        | SqlFloat f -> box f
        | SqlText s -> box s
        | SqlBlob b -> box b
        // Encoded explicitly rather than handed to the driver, so that every
        // backend stores byte-identical text for the same value. Letting each
        // driver pick its own representation is what makes a "portable" query
        // layer quietly non-portable.
        | SqlDecimal d -> box (Convert.decimalToText d)
        | SqlDate d -> box (Convert.dateToText d)
        | SqlGuid g -> box (Convert.guidToText g)

/// Wraps an already-constructed ADO.NET connection. Ownership passes to the
/// connector: Close/Dispose closes it.
///
/// `placeholder` defaults to Named, which is what every mainstream ADO.NET
/// provider wants. It is settable because a provider that only accepts
/// positional markers can be driven through the same Dialect rewriting the Rust
/// backends use.
type AdoConnector(connection: DbConnection, ?parameterPrefix: string, ?placeholder: Placeholder, ?vendor: Vendor) =

    let prefix = defaultArg parameterPrefix "@"
    let style = defaultArg placeholder Named

    // Which engine is behind the provider. Only the query builder cares, and
    // only for paging syntax and function names, so Generic is a safe default
    // for a provider we know nothing about.
    let engine = defaultArg vendor Generic

    // Microsoft.Data.Sqlite (and SqlClient) refuse to run a command while a
    // transaction is open on the connection unless the command is enlisted in
    // it explicitly, so the active one is tracked here.
    let mutable current: DbTransaction = null

    let ensureOpen () =
        async {
            if connection.State <> ConnectionState.Open then
                do! connection.OpenAsync() |> Async.AwaitTask
        }

    let newCommand (sql: string) (ps: SqlParam[]) =
        let sql, values = Dialect.bind style sql ps
        let cmd = connection.CreateCommand()
        cmd.CommandText <- sql

        if not (isNull current) then
            cmd.Transaction <- current

        // Each parameter is named exactly as it now appears in the rewritten SQL,
        // so the driver can match them up. Under Positional nothing appears at
        // all, and how a driver identifies an anonymous marker is its own
        // business -- Microsoft.Data.Sqlite, for one, will not accept a name
        // there. That style is for the sqlx backends, which bind by position.
        values
        |> Array.iteri (fun i v ->
            let dp = cmd.CreateParameter()

            dp.ParameterName <-
                match style with
                | Named -> prefix + ps.[i].Name
                | Numbered -> "$" + string (i + 1)
                | Positional -> ""

            dp.Value <- Conv.toDbValue v
            cmd.Parameters.Add dp |> ignore)

        cmd

    let readAll (reader: DbDataReader) : Async<ResultSet> =
        async {
            let columns = Array.init reader.FieldCount reader.GetName
            let rows = ResizeArray<SqlValue[]>()
            let mutable go = true

            while go do
                let! more = reader.ReadAsync() |> Async.AwaitTask

                if more then
                    rows.Add(Array.init reader.FieldCount (reader.GetValue >> Conv.toSqlValue))
                else
                    go <- false

            return
                { Columns = columns
                  Rows = rows.ToArray() }
        }

    member _.Connection = connection

    interface ISqlConnector with

        member _.Placeholder = style
        member _.Vendor = engine

        member _.Query(sql, ps) =
            async {
                do! ensureOpen ()
                use cmd = newCommand sql ps
                use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                return! readAll reader
            }

        member _.Execute(sql, ps) =
            async {
                do! ensureOpen ()
                use cmd = newCommand sql ps
                return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
            }

        member _.Scalar(sql, ps) =
            async {
                do! ensureOpen ()
                use cmd = newCommand sql ps
                let! v = cmd.ExecuteScalarAsync() |> Async.AwaitTask
                return Conv.toSqlValue v
            }

        member _.BeginTransaction() =
            async {
                do! ensureOpen ()
                let! tx = connection.BeginTransactionAsync().AsTask() |> Async.AwaitTask
                current <- tx
            }

        member _.Commit() =
            async {
                let tx = current

                if isNull tx then
                    failwith "Commit was called with no transaction open"

                // Cleared before the await so a failed commit cannot leave a
                // disposed transaction enlisted on the next command.
                current <- null

                // Disposed on the failure path too -- ADO's Dispose rolls an
                // uncommitted transaction back, so a failed commit does not
                // leave one dangling on the connection.
                try
                    do! tx.CommitAsync() |> Async.AwaitTask
                finally
                    tx.Dispose()
            }

        member _.Rollback() =
            async {
                let tx = current

                if isNull tx then
                    failwith "Rollback was called with no transaction open"

                current <- null

                try
                    do! tx.RollbackAsync() |> Async.AwaitTask
                finally
                    tx.Dispose()
            }

        member _.Close() =
            if connection.State <> ConnectionState.Closed then
                connection.Close()

    interface IDisposable with
        member _.Dispose() = connection.Dispose()
