/// Generates F# from a live database schema.
///
///   dotnet tool install --global SQLProvider.Fable.Generator
///   sqlprovider-fable-gen --sqlite ./app.db --module Northwind --out ./src/Schema.fs
///
/// (or from this repo: dotnet run --project tools/SQLProvider.Fable.Generator -- ...)
///
/// The output is ordinary source: check it in, read it, diff it. Regenerate it
/// when the schema changes rather than editing it, because the column names in
/// it have to keep matching the database.
module SQLProvider.Fable.Generator.Program

open System
open SQLProvider.Fable
open SQLProvider.Fable.Ado
open SQLProvider.Fable.Design

let private usage =
    "usage: generator (--sqlite <file> | --postgres <connection-string>) --module <Name> --out <file.fs>"

let private arg (name: string) (argv: string[]) =
    argv
    |> Array.tryFindIndex (fun a -> a = name)
    |> Option.bind (fun i -> Array.tryItem (i + 1) argv)

[<EntryPoint>]
let main argv =
    let moduleName = arg "--module" argv |> Option.defaultValue "Schema"
    let outPath = arg "--out" argv

    let connector: ISqlConnector option =
        match arg "--sqlite" argv, arg "--postgres" argv with
        | Some file, _ ->
            let c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + file)
            Some(new AdoConnector(c, vendor = Sqlite) :> ISqlConnector)
        | _, Some cs ->
            // Npgsql is referenced directly: this ships as a packed dotnet
            // tool, which is a closed world -- a DbProviderFactory the caller
            // was supposed to register could never appear inside it.
            let c = new Npgsql.NpgsqlConnection(cs)
            Some(new AdoConnector(c, vendor = Postgres) :> ISqlConnector)
        | _ -> None

    match connector, outPath with
    | Some conn, Some out ->
        try
            let db = SchemaReader.read conn |> Async.RunSynchronously
            let source = CodeGen.emit moduleName db
            IO.File.WriteAllText(out, source)

            printfn "Wrote %d table(s) to %s" db.Tables.Length out
            0
        finally
            conn.Close()
    | None, _ ->
        eprintfn "%s" usage
        1
    | _, None ->
        eprintfn "%s" usage
        1
