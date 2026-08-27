module SQLProvider.Fable.Tests.Rust.Program

open SQLProvider.Fable
open SQLProvider.Fable.Rust
open SQLProvider.Fable.SmokeTests

/// Runs the shared smoke suite over rusqlite. The .NET runner
/// (tests/SQLProvider.Fable.Tests.Net) runs the very same suite over ADO.NET.
[<EntryPoint>]
let main _ =
    // A private in-memory database lives exactly as long as its connection, so
    // each run starts from an empty schema with no file to clean up.
    let connector = RusqliteConnector(":memory:") :> ISqlConnector

    let results = Smoke.run connector
    printfn "%s" (Smoke.report results)

    connector.Close()

    let failed = results |> Array.filter (fun r -> not r.Passed)

    if failed.Length = 0 then 0 else 1
