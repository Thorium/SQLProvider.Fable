module SQLProvider.Fable.Tests.Net.Tests

open Xunit
open Microsoft.Data.Sqlite
open SQLProvider.Fable
open SQLProvider.Fable.Ado
open SQLProvider.Fable.SmokeTests

/// Runs the shared smoke suite over ADO.NET + Microsoft.Data.Sqlite.
/// The Rust runner (tests/Rust) runs the very same suite over rusqlite.
[<Fact>]
let ``shared smoke suite passes over ADO.NET`` () =
    // A private in-memory database lives exactly as long as its connection,
    // so each run starts from an empty schema with no file to clean up.
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()

    use connector = new AdoConnector(connection)
    let results = Smoke.run (connector :> ISqlConnector)

    let failures =
        results
        |> Array.filter (fun r -> not r.Passed)
        |> Array.map (fun r -> r.Name + ": " + r.Detail)

    Assert.True(results.Length > 0, "the smoke suite ran no tests")
    Assert.Empty(failures)
