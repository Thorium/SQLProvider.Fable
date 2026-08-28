module SQLProvider.Fable.Tests.Js.Program

open SQLProvider.Fable
open SQLProvider.Fable.Js
open SQLProvider.Fable.SmokeTests

/// Runs the shared suites over node:sqlite. The .NET runner runs the very same
/// suites over ADO.NET, and tests/Rust runs them over sqlx.
/// Async.RunSynchronously does not exist on JavaScript -- there is no thread to
/// block -- so the whole run is one computation started immediately. node:sqlite
/// is synchronous underneath, so this completes before StartImmediate returns.
[<EntryPoint>]
let main _ =
    let dialect = DialectTests.run ()
    printfn "Dialect:"
    printfn "%s" (Harness.report dialect)

    let queries = Array.append (QueryTests.run ()) (QueryTests.runGenerated ())
    printfn "Query:"
    printfn "%s" (Harness.report queries)

    // A private in-memory database lives exactly as long as its connection, so
    // each run starts from an empty schema with no file to clean up.
    let connector = SqliteConnector(":memory:") :> ISqlConnector

    let mutable exitCode = 1

    Async.StartImmediate(
        async {
            let! smoke = Smoke.run connector Smoke.Fixture.sqlite

            printfn "Smoke (node:sqlite):"
            printfn "%s" (Harness.report smoke)

            connector.Close()

            let failed =
                Array.concat [ dialect; queries; smoke ] |> Array.filter (fun r -> not r.Passed)

            exitCode <- (if failed.Length = 0 then 0 else 1)
        }
    )

    exitCode
