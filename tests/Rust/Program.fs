module SQLProvider.Fable.Tests.Rust.Program

open SQLProvider.Fable
open SQLProvider.Fable.Rust
open SQLProvider.Fable.SmokeTests

/// Runs the shared suites over sqlx. The .NET runner
/// (tests/SQLProvider.Fable.Tests.Net) runs the very same suites over ADO.NET.
///
/// The URL decides the engine and comes from the first argument, defaulting to a
/// private in-memory SQLite database -- which lives exactly as long as its
/// connection, so each run starts from an empty schema with no file to clean up.
/// Point it at a server to run the same assertions there:
///
///   sqlprovider_fable_smoke "postgres://user:pw@localhost/testdb"
///   sqlprovider_fable_smoke "mysql://user:pw@localhost/testdb"
///
/// Those two need an empty database: the suite creates its own tables and does
/// not drop them.
let private fixtureFor (url: string) =
    match Dialect.scheme url with
    | "postgres"
    | "postgresql" -> Smoke.Fixture.postgres
    | "mysql"
    | "mariadb" -> Smoke.Fixture.mysql
    | _ -> Smoke.Fixture.sqlite

[<EntryPoint>]
let main argv =
    let url =
        if argv.Length > 0 && argv.[0] <> "" then
            argv.[0]
        else
            "sqlite::memory:"

    // Pure logic, no database: these run identically whatever the URL says.
    let dialect = DialectTests.run ()
    printfn "Dialect:"
    printfn "%s" (Harness.report dialect)

    let queries = Array.append (QueryTests.run ()) (QueryTests.runGenerated ())
    printfn "Query:"
    printfn "%s" (Harness.report queries)

    let connector = SqlxConnector(url) :> ISqlConnector

    let smoke = Smoke.run connector (fixtureFor url) |> Async.RunSynchronously

    printfn "Smoke (%s):" url
    printfn "%s" (Harness.report smoke)

    connector.Close()

    let failed =
        Array.concat [ dialect; queries; smoke ] |> Array.filter (fun r -> not r.Passed)

    if failed.Length = 0 then 0 else 1
