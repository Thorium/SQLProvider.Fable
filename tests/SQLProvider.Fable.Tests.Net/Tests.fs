module SQLProvider.Fable.Tests.Net.Tests

open Xunit
open Microsoft.Data.Sqlite
open SQLProvider.Fable
open SQLProvider.Fable.Ado
open SQLProvider.Fable.SmokeTests

let private assertAllPassed (results: Harness.TestResult[]) =
    let failures =
        results
        |> Array.filter (fun r -> not r.Passed)
        |> Array.map (fun r -> $"{r.Name}: {r.Detail}")

    Assert.True(results.Length > 0, "the suite ran no tests")
    Assert.Empty(failures)

/// The query builder and its SQL generation. Pure logic, so the vendor's
/// spelling is pinned down here rather than only where a server can be reached.
[<Fact>]
let ``query builder suite passes`` () = assertAllPassed (QueryTests.run ())

/// Placeholder rewriting. Pure logic, so this is the one piece of the
/// multi-vendor story that is fully verifiable without a server -- and since
/// every backend routes its SQL through it, a bug here is a bug in all of them.
[<Fact>]
let ``dialect rewriting suite passes`` () = assertAllPassed (DialectTests.run ())

/// Runs the shared smoke suite over ADO.NET + Microsoft.Data.Sqlite.
/// The Rust runner (tests/Rust) runs the very same suite over sqlx.
[<Fact>]
let ``shared smoke suite passes over ADO.NET`` () =
    // A private in-memory database lives exactly as long as its connection,
    // so each run starts from an empty schema with no file to clean up.
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()

    use connector = new AdoConnector(connection, vendor = Sqlite)

    Smoke.run (connector :> ISqlConnector) Smoke.Fixture.sqlite
    |> Async.RunSynchronously
    |> assertAllPassed

/// The same suite again with the SQL rewritten to numbered markers -- the shape
/// PostgreSQL wants. SQLite happens to accept `$1` as a parameter name too, so
/// the rewriting gets exercised end-to-end against a real engine here and not
/// only in the unit tests. (The positional `?` shape cannot be tested this way:
/// Microsoft.Data.Sqlite will not bind an anonymous marker by position.)
[<Fact>]
let ``shared smoke suite passes with numbered placeholders`` () =
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()

    use connector =
        new AdoConnector(connection, placeholder = Numbered, vendor = Sqlite)

    Smoke.run (connector :> ISqlConnector) Smoke.Fixture.sqlite
    |> Async.RunSynchronously
    |> assertAllPassed

/// The encoding must not depend on the developer's locale. fi-FI uses a comma
/// decimal separator, which is exactly what breaks `Decimal.Parse s` and
/// `decimal.ToString()`; if anything in the stack reached for a culture-sensitive
/// conversion, this fails and the en-GB run above would not have noticed.
[<Fact>]
let ``shared smoke suite passes under a comma-separator locale`` () =
    let original = System.Globalization.CultureInfo.CurrentCulture

    try
        System.Globalization.CultureInfo.CurrentCulture <- System.Globalization.CultureInfo "fi-FI"

        use connection = new SqliteConnection("Data Source=:memory:")
        connection.Open()

        use connector = new AdoConnector(connection, vendor = Sqlite)

        Smoke.run (connector :> ISqlConnector) Smoke.Fixture.sqlite
        |> Async.RunSynchronously
        |> assertAllPassed
    finally
        System.Globalization.CultureInfo.CurrentCulture <- original

/// Column lookup must not depend on the locale either. Turkish lowers 'I' to a
/// dotless 'ı', so a culture-sensitive fold stops "CustomerId" matching the
/// "customerid" PostgreSQL hands back for an unquoted column -- which is the
/// everyday case, not an exotic one, for anyone running under tr-TR.
[<Fact>]
let ``column lookup is case-insensitive under the Turkish locale`` () =
    let original = System.Globalization.CultureInfo.CurrentCulture

    try
        System.Globalization.CultureInfo.CurrentCulture <- System.Globalization.CultureInfo "tr-TR"

        let rs =
            { Columns = [| "customerid" |]
              Rows = [| [| SqlInt 42L |] |] }

        Assert.Equal(0, Row.tryOrdinal rs "CustomerId")

        match ResultSet.tryHead rs with
        | None -> failwith "the result set has a row"
        | Some row -> Assert.Equal(42L, Row.int64 row "CustomerId")
    finally
        System.Globalization.CultureInfo.CurrentCulture <- original
