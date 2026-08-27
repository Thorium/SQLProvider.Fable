/// Backend-agnostic smoke tests.
///
/// Written once against ISqlConnector, run twice: under xunit on .NET over
/// AdoConnector, and compiled by Fable to Rust over RusqliteConnector. If a test
/// here needs a `#if`, the abstraction has sprung a leak.
///
/// Portability rules for this file: no System.Data, no reflection, no
/// System.Linq.Expressions, no async. Plain F# only.
module SQLProvider.Fable.SmokeTests.Smoke

open SQLProvider.Fable

// --- the shape a schema code generator will emit -------------------------

type Customer =
    { CustomerId: int
      Name: string
      Country: string option
      Balance: float }

module Customer =

    /// Hand-written here; generated per table later. Explicit conversions rather
    /// than reflection, because Fable's Rust PropertyInfo carries only a name --
    /// there is no PropertyType to dispatch a generic mapper on.
    let ofRow (r: SqlRow) : Customer =
        { CustomerId = Row.int r "CustomerId"
          Name = Row.text r "Name"
          Country = Row.textOpt r "Country"
          Balance = Row.float r "Balance" }

// --- a tiny assertion harness (no xunit: this file must compile to Rust) --

type TestResult = { Name: string; Passed: bool; Detail: string }

let private pass name = { Name = name; Passed = true; Detail = "" }
let private fail name detail = { Name = name; Passed = false; Detail = detail }

let private check name expected actual =
    if expected = actual then
        pass name
    else
        fail name ("expected " + expected + ", got " + actual)

// --- fixture --------------------------------------------------------------

let private schema =
    "CREATE TABLE Customer (
        CustomerId INTEGER NOT NULL PRIMARY KEY,
        Name       TEXT    NOT NULL,
        Country    TEXT    NULL,
        Balance    REAL    NOT NULL
     )"

let private seed (c: ISqlConnector) =
    c.Execute(schema, Sql.noParams) |> ignore

    let insert =
        "INSERT INTO Customer (CustomerId, Name, Country, Balance)
         VALUES (@id, @name, @country, @balance)"

    let add id name country balance =
        c.Execute(
            insert,
            [| Sql.pInt "id" id
               Sql.pText "name" name
               Sql.pTextOpt "country" country
               Sql.pFloat "balance" balance |]
        )
        |> ignore

    add 1 "Alfreds" (Some "Germany") 100.5
    add 2 "Berglunds" (Some "Sweden") 250.0
    add 3 "Cactus" None 0.0

// --- the tests ------------------------------------------------------------

/// Runs every test against one connector. The connector must be freshly opened
/// on an empty database; this creates and seeds its own table.
let run (c: ISqlConnector) : TestResult[] =
    let results = ResizeArray<TestResult>()

    seed c

    // 1. round-trip: query, map to a record, read every column kind
    let rs =
        c.Query("SELECT CustomerId, Name, Country, Balance FROM Customer ORDER BY CustomerId", Sql.noParams)

    let customers = rs |> ResultSet.map Customer.ofRow

    results.Add(check "row count" "3" (string customers.Length))
    results.Add(check "text column" "Alfreds" customers.[0].Name)
    results.Add(check "int column" "2" (string customers.[1].CustomerId))
    results.Add(check "float column" "250" (string customers.[1].Balance))

    // 2. NULL becomes None, not a sentinel
    let nullCase =
        match customers.[2].Country with
        | None -> "None"
        | Some v -> "Some " + v

    results.Add(check "null column maps to None" "None" nullCase)

    results.Add(check "non-null column maps to Some" "Some Germany" (
        match customers.[0].Country with
        | None -> "None"
        | Some v -> "Some " + v))

    // 3. parameters actually bind (a literal would pass this test by accident,
    //    so filter on a value that excludes rows)
    let filtered =
        c.Query(
            "SELECT CustomerId, Name, Country, Balance FROM Customer WHERE Balance > @min ORDER BY CustomerId",
            [| Sql.pFloat "min" 50.0 |]
        )
        |> ResultSet.map Customer.ofRow

    results.Add(check "parameter binds" "2" (string filtered.Length))
    results.Add(check "parameter filters correctly" "Alfreds,Berglunds" (
        filtered |> Array.map (fun x -> x.Name) |> String.concat ","))

    // 4. scalar
    let count = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)

    let countText =
        match count with
        | SqlInt n -> string n
        | other -> "not an int: " + SqlValue.typeName other

    results.Add(check "scalar count" "3" countText)

    // 5. column names survive the round trip, case-insensitively
    let firstRow = (ResultSet.rows rs).[0]
    results.Add(check "column lookup is case-insensitive" "Alfreds" (Row.text firstRow "nAmE"))
    results.Add(check "column names preserved" "CustomerId,Name,Country,Balance" (String.concat "," rs.Columns))

    // 6. Execute reports affected rows
    let affected = c.Execute("UPDATE Customer SET Balance = Balance + @bump WHERE CustomerId <= @id",
                             [| Sql.pFloat "bump" 1.0; Sql.pInt "id" 2 |])

    results.Add(check "execute returns affected rows" "2" (string affected))

    // 7. transaction rollback leaves nothing behind
    let tx = c.BeginTransaction()

    c.Execute("INSERT INTO Customer (CustomerId, Name, Country, Balance) VALUES (@id, @name, NULL, 0.0)",
              [| Sql.pInt "id" 99; Sql.pText "name" "Doomed" |])
    |> ignore

    tx.Rollback()

    let afterRollback = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)

    let afterText =
        match afterRollback with
        | SqlInt n -> string n
        | other -> "not an int: " + SqlValue.typeName other

    results.Add(check "rollback discards the insert" "3" afterText)

    // 8. transaction commit keeps it
    let tx2 = c.BeginTransaction()

    c.Execute("INSERT INTO Customer (CustomerId, Name, Country, Balance) VALUES (@id, @name, NULL, 0.0)",
              [| Sql.pInt "id" 100; Sql.pText "name" "Kept" |])
    |> ignore

    tx2.Commit()

    let afterCommit = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)

    let committedText =
        match afterCommit with
        | SqlInt n -> string n
        | other -> "not an int: " + SqlValue.typeName other

    results.Add(check "commit keeps the insert" "4" committedText)

    results.ToArray()

/// Renders results as text, for the Rust runner (which has no test framework).
let report (results: TestResult[]) =
    let failed = results |> Array.filter (fun r -> not r.Passed)

    let lines =
        results
        |> Array.map (fun r ->
            if r.Passed then
                "  PASS  " + r.Name
            else
                "  FAIL  " + r.Name + " -- " + r.Detail)

    let summary =
        "  " + string (results.Length - failed.Length) + "/" + string results.Length + " passed"

    String.concat "\n" (Array.append lines [| summary |])
