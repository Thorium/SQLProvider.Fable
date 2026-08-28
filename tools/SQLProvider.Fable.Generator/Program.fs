/// Generates F# from a live database schema.
///
///   dotnet run --project tools/SQLProvider.Fable.Generator -- \
///       --sqlite ./app.db --module Northwind --out ./src/Schema.fs
///
/// The output is ordinary source: check it in, read it, diff it. Regenerate it
/// when the schema changes rather than editing it, because the column names in
/// it have to keep matching the database.
module SQLProvider.Fable.Generator.Program

open System
open System.Data.Common
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
            // Npgsql is not referenced here on purpose: the generator takes any
            // DbProviderFactory-registered provider, so a caller can bring its
            // own without this tool depending on every driver in existence.
            match DbProviderFactories.GetFactory "Npgsql" with
            | null -> None
            | factory ->
                let c = factory.CreateConnection()
                c.ConnectionString <- cs
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
        eprintfn "  (a --postgres connection needs Npgsql registered with DbProviderFactories)"
        1
    | _, None ->
        eprintfn "%s" usage
        1
