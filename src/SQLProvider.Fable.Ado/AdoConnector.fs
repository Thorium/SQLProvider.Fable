namespace SQLProvider.Fable.Ado

open System
open System.Data
open SQLProvider.Fable

/// ADO.NET implementation of ISqlConnector.
///
/// This is the whole of System.Data the runtime needs: IDbConnection
/// (Open/Close/State/CreateCommand), IDbCommand (CommandText/Parameters/
/// ExecuteReader/ExecuteScalar/ExecuteNonQuery), IDbDataParameter
/// (ParameterName/Value) and IDataReader (Read/FieldCount/GetName/GetValue).
/// Everything else in SQLProvider's ISqlProvider is schema introspection, which
/// stays on .NET at code-generation time and never reaches a Fable target.
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
        | :? decimal as d -> SqlFloat(float d)
        | :? string as s -> SqlText s
        | :? (byte[]) as b -> SqlBlob b
        // Matching the placeholder the union documents: dates travel as
        // ISO-8601 text until the union grows a native date case.
        | :? DateTime as d -> SqlText(d.ToString "o")
        | :? Guid as g -> SqlText(g.ToString())
        | other -> SqlText(string other)

    let toDbValue (v: SqlValue) : obj =
        match v with
        | SqlNull -> box DBNull.Value
        | SqlBool b -> box b
        | SqlInt i -> box i
        | SqlFloat f -> box f
        | SqlText s -> box s
        | SqlBlob b -> box b

/// Wraps an already-constructed ADO.NET connection. Ownership passes to the
/// connector: Close/Dispose closes it.
type AdoConnector(connection: IDbConnection, ?parameterPrefix: string) =

    let prefix = defaultArg parameterPrefix "@"

    // Microsoft.Data.Sqlite (and SqlClient) refuse to run a command while a
    // transaction is open on the connection unless the command is enlisted in
    // it explicitly, so the active one is tracked here.
    let mutable current: IDbTransaction = null

    let ensureOpen () =
        if connection.State <> ConnectionState.Open then
            connection.Open()

    let newCommand (sql: string) (ps: SqlParam[]) =
        let cmd = connection.CreateCommand()
        cmd.CommandText <- sql

        if not (isNull current) then
            cmd.Transaction <- current

        for p in ps do
            let dp = cmd.CreateParameter()
            dp.ParameterName <- prefix + p.Name
            dp.Value <- Conv.toDbValue p.Value
            cmd.Parameters.Add dp |> ignore

        cmd

    let readAll (reader: IDataReader) : ResultSet =
        let columns = Array.init reader.FieldCount reader.GetName
        let rows = ResizeArray<SqlValue[]>()

        while reader.Read() do
            rows.Add(Array.init reader.FieldCount (fun i -> Conv.toSqlValue (reader.GetValue i)))

        { Columns = columns; Rows = rows.ToArray() }

    member _.Connection = connection

    interface ISqlConnector with

        member _.Query(sql, ps) =
            ensureOpen ()
            use cmd = newCommand sql ps
            use reader = cmd.ExecuteReader()
            readAll reader

        member _.Execute(sql, ps) =
            ensureOpen ()
            use cmd = newCommand sql ps
            cmd.ExecuteNonQuery()

        member _.Scalar(sql, ps) =
            ensureOpen ()
            use cmd = newCommand sql ps
            Conv.toSqlValue (cmd.ExecuteScalar())

        member _.BeginTransaction() =
            ensureOpen ()
            let tx = connection.BeginTransaction()
            current <- tx

            { new ISqlTransaction with
                member _.Commit() =
                    tx.Commit()
                    tx.Dispose()
                    current <- null

                member _.Rollback() =
                    tx.Rollback()
                    tx.Dispose()
                    current <- null
            }

        member _.Close() =
            if connection.State <> ConnectionState.Closed then
                connection.Close()

    interface IDisposable with
        member _.Dispose() = connection.Dispose()
