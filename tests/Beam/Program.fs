module SQLProvider.Fable.Tests.Beam.Program

open SQLProvider.Fable
open SQLProvider.Fable.SmokeTests

/// There is no Erlang connector yet, so this runs the parts of the library that
/// need no database: the placeholder rewriting, and the value encoding every
/// connector shares. Together they are most of what has to survive a target.
let private convertChecks () =
    let results = ResizeArray<Harness.TestResult>()

    let theTotal = 1234.5678M
    let theDate = System.DateTime(2026, 8, 27, 10, 30, 0)
    let theId = System.Guid "0f8fad5b-d9cb-469f-a165-70867728950e"

    // The stored text has to be byte-identical to what the other backends
    // write, so assert the encoding itself and not only the round trip.
    results.Add(Harness.check "decimal encodes invariantly" "1234.5678" (Convert.decimalToText theTotal))
    results.Add(Harness.check "date encodes as ISO-8601" "2026-08-27T10:30:00.0000000" (Convert.dateToText theDate))

    results.Add(
        Harness.check "guid encodes lower-case" "0f8fad5b-d9cb-469f-a165-70867728950e" (Convert.guidToText theId)
    )

    results.Add(Harness.isTrue "decimal round-trips" (Convert.tryParseDecimal "1234.5678" = Some theTotal))
    results.Add(Harness.isTrue "negative decimal round-trips" (Convert.tryParseDecimal "-0.25" = Some -0.25M))
    results.Add(Harness.isTrue "date round-trips" (Convert.tryParseDate "2026-08-27T10:30:00.0000000" = Some theDate))

    results.Add(
        Harness.isTrue "guid round-trips" (Convert.tryParseGuid "0f8fad5b-d9cb-469f-a165-70867728950e" = Some theId)
    )

    results.Add(Harness.isTrue "junk decimal is rejected" (Convert.tryParseDecimal "1,5" = None))
    results.Add(Harness.isTrue "junk date is rejected" (Convert.tryParseDate "not a date" = None))

    results.ToArray()

/// Reading a canned ResultSet, which is the shape any connector produces.
let private rowChecks () =
    let results = ResizeArray<Harness.TestResult>()

    let rs =
        { Columns = [| "Id"; "Name"; "Country"; "Balance" |]
          Rows =
            [| [| SqlInt 1L; SqlText "Alfreds"; SqlNull; SqlFloat 100.5 |]
               [| SqlInt 2L; SqlText "Berglunds"; SqlText "Sweden"; SqlInt 250L |] |] }

    let rows = ResultSet.rows rs

    results.Add(Harness.check "row count" "2" (string (ResultSet.rowCount rs)))
    results.Add(Harness.check "text column" "Alfreds" (Row.text rows.[0] "Name"))
    results.Add(Harness.check "int column" "2" (string (Row.int rows.[1] "Id")))
    results.Add(Harness.check "case-insensitive lookup" "Alfreds" (Row.text rows.[0] "nAmE"))
    results.Add(Harness.isTrue "null maps to None" (Row.textOpt rows.[0] "Country" = None))
    results.Add(Harness.isTrue "non-null maps to Some" (Row.textOpt rows.[1] "Country" = Some "Sweden"))
    // An integer storage class in a float column has to widen, not fail.
    results.Add(Harness.isTrue "int widens to float" (Row.float rows.[1] "Balance" = 250.0))

    results.ToArray()

/// The async computation the connector interface is built on, without a driver
/// behind it: if the builder does not work here, no connector could.
let private asyncChecks () =
    async {
        let results = ResizeArray<Harness.TestResult>()

        let! x = async { return 21 }
        results.Add(Harness.check "let! binds" "21" (string x))

        let mutable sum = 0

        for i in 1..3 do
            let! y = async { return i }
            sum <- sum + y

        results.Add(Harness.check "for in async" "6" (string sum))

        let mutable caught = "no"

        try
            let! _ = async { return () }
            failwith "boom"
        with _ ->
            caught <- "yes"

        results.Add(Harness.check "try/with in async" "yes" caught)

        return results.ToArray()
    }

let runAll () =
    let dialect = DialectTests.run ()
    let queries = Array.append (QueryTests.run ()) (QueryTests.runGenerated ())
    let convert = convertChecks ()
    let rows = rowChecks ()
    let asyncs = asyncChecks () |> Async.RunSynchronously

    printfn "Dialect:"
    printfn "%s" (Harness.report dialect)
    printfn "Query:"
    printfn "%s" (Harness.report queries)
    printfn "Convert:"
    printfn "%s" (Harness.report convert)
    printfn "Rows:"
    printfn "%s" (Harness.report rows)
    printfn "Async:"
    printfn "%s" (Harness.report asyncs)

    let failed =
        Array.concat [ dialect; queries; convert; rows; asyncs ]
        |> Array.filter (fun r -> not r.Passed)

    if failed.Length = 0 then 0 else 1

[<EntryPoint>]
let main _ = runAll ()
