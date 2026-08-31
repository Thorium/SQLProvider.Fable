/// Backend-agnostic smoke tests.
///
/// Written once against ISqlConnector, run on every backend: under xunit on
/// .NET over AdoConnector, and compiled by Fable to Rust over SqlxConnector
/// against SQLite, PostgreSQL and MySQL/MariaDB. If a test here needs a `#if`,
/// the abstraction has sprung a leak.
///
/// Portability rules for this file: no System.Data, no reflection, no
/// System.Linq.Expressions. Plain F# and Async only.
module SQLProvider.Fable.SmokeTests.Smoke

open SQLProvider.Fable
open SQLProvider.Fable.SmokeTests.Harness

module Cols = SQLProvider.Fable.SmokeTests.QueryTests.Customer

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

// --- fixture --------------------------------------------------------------

/// The dialects disagree on nearly every type name, so the fixture DDL is
/// per-vendor while everything the tests actually assert stays shared. These are
/// the only vendor-specific strings in the test suite.
type Fixture =
    { CustomerTable: string
      InvoiceTable: string }

module Fixture =

    [<Literal>]
    let private invoiceAsText =
        "CREATE TABLE Invoice (Id TEXT NOT NULL, Total TEXT NOT NULL, Issued TEXT NOT NULL, Rebate TEXT NULL)"

    let sqlite =
        { CustomerTable =
            "CREATE TABLE Customer (
                CustomerId INTEGER NOT NULL PRIMARY KEY,
                Name       TEXT    NOT NULL,
                Country    TEXT    NULL,
                Balance    REAL    NOT NULL
             )"
          InvoiceTable = invoiceAsText }

    let postgres =
        { CustomerTable =
            "CREATE TABLE Customer (
                CustomerId INTEGER          NOT NULL PRIMARY KEY,
                Name       TEXT             NOT NULL,
                Country    TEXT             NULL,
                Balance    DOUBLE PRECISION NOT NULL
             )"
          InvoiceTable = invoiceAsText }

    /// MySQL and MariaDB. TEXT cannot carry a length-free key and the engine is
    /// stricter about lengths, so the text columns are VARCHAR.
    let mysql =
        { CustomerTable =
            "CREATE TABLE Customer (
                CustomerId INT          NOT NULL PRIMARY KEY,
                Name       VARCHAR(100) NOT NULL,
                Country    VARCHAR(100) NULL,
                Balance    DOUBLE       NOT NULL
             )"
          InvoiceTable =
            "CREATE TABLE Invoice (
                Id     VARCHAR(64) NOT NULL,
                Total  VARCHAR(64) NOT NULL,
                Issued VARCHAR(64) NOT NULL,
                Rebate VARCHAR(64) NULL
             )" }

let private seed (c: ISqlConnector) (fixture: Fixture) =
    async {
        let! _ = c.Execute(fixture.CustomerTable, Sql.noParams)

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

        let! _ = add 1 "Alfreds" (Some "Germany") 100.5
        let! _ = add 2 "Berglunds" (Some "Sweden") 250.0
        let! _ = add 3 "Cactus" None 0.0
        return ()
    }

/// COUNT(*) comes back with a different type per vendor: an int8 on PostgreSQL,
/// a DECIMAL-typed bigint on MySQL. Both are exact integers, so both are read.
let private countText (v: SqlValue) =
    match v with
    | SqlInt n -> string n
    | SqlDecimal d -> string (int64 d)
    | other -> "not an int: " + SqlValue.typeName other

// --- the tests ------------------------------------------------------------

/// Runs every test against one connector. The connector must be freshly opened
/// on an empty database; this creates and seeds its own tables.
let run (c: ISqlConnector) (fixture: Fixture) : Async<TestResult[]> =
    async {
        let results = ResizeArray<TestResult>()

        do! seed c fixture

        // 1. round-trip: query, map to a record, read every column kind
        let! rs = c.Query("SELECT CustomerId, Name, Country, Balance FROM Customer ORDER BY CustomerId", Sql.noParams)

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

        results.Add(
            check
                "non-null column maps to Some"
                "Some Germany"
                (match customers.[0].Country with
                 | None -> "None"
                 | Some v -> "Some " + v)
        )

        // 3. parameters actually bind (a literal would pass this test by accident,
        //    so filter on a value that excludes rows)
        let! filteredRs =
            c.Query(
                "SELECT CustomerId, Name, Country, Balance FROM Customer WHERE Balance > @min ORDER BY CustomerId",
                [| Sql.pFloat "min" 50.0 |]
            )

        let filtered = filteredRs |> ResultSet.map Customer.ofRow

        results.Add(check "parameter binds" "2" (string filtered.Length))

        results.Add(
            check
                "parameter filters correctly"
                "Alfreds,Berglunds"
                (filtered |> Array.map (fun x -> x.Name) |> String.concat ",")
        )

        // 3b. the same parameter used twice. Under the positional dialects this
        //     has to be sent down twice, so it is worth an end-to-end test and
        //     not only the unit test in DialectTests.
        let! twiceRs =
            c.Query(
                "SELECT Name FROM Customer WHERE Balance >= @b OR Balance <= @b ORDER BY CustomerId",
                [| Sql.pFloat "b" 100.5 |]
            )

        results.Add(check "a repeated parameter binds end to end" "3" (string (ResultSet.rowCount twiceRs)))

        // 4. scalar
        let! count = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)
        results.Add(check "scalar count" "3" (countText count))

        // 5. column names survive the round trip, case-insensitively
        let firstRow = (ResultSet.rows rs).[0]
        results.Add(check "column lookup is case-insensitive" "Alfreds" (Row.text firstRow "nAmE"))

        // Only the lookup is asserted, not rs.Columns itself: PostgreSQL folds
        // unquoted identifiers to lower case, so the exact spelling that comes
        // back is a vendor detail. That the lookup finds them is not.
        results.Add(check "every column came back" "4" (string rs.Columns.Length))

        // 6. Execute reports affected rows
        let! affected =
            c.Execute(
                "UPDATE Customer SET Balance = Balance + @bump WHERE CustomerId <= @id",
                [| Sql.pFloat "bump" 1.0; Sql.pInt "id" 2 |]
            )

        results.Add(check "execute returns affected rows" "2" (string affected))

        // 7. transaction rollback leaves nothing behind
        do! c.BeginTransaction()

        let! _ =
            c.Execute(
                "INSERT INTO Customer (CustomerId, Name, Country, Balance) VALUES (@id, @name, NULL, 0.0)",
                [| Sql.pInt "id" 99; Sql.pText "name" "Doomed" |]
            )

        do! c.Rollback()

        let! afterRollback = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)
        results.Add(check "rollback discards the insert" "3" (countText afterRollback))

        // 8. transaction commit keeps it
        do! c.BeginTransaction()

        let! _ =
            c.Execute(
                "INSERT INTO Customer (CustomerId, Name, Country, Balance) VALUES (@id, @name, NULL, 0.0)",
                [| Sql.pInt "id" 100; Sql.pText "name" "Kept" |]
            )

        do! c.Commit()

        let! afterCommit = c.Scalar("SELECT COUNT(*) FROM Customer", Sql.noParams)
        results.Add(check "commit keeps the insert" "4" (countText afterCommit))

        // 9. decimal / date / guid round-trip.
        //
        // This is the case that motivated widening SqlValue: a DECIMAL(18,4)
        // column must come back as the same decimal, not as a float that is
        // nearly right. Every backend encodes these to TEXT through Convert, so
        // identical bytes are stored whichever one wrote them, and the readers
        // parse them back invariantly -- the culture-taking overloads do not
        // exist on Rust, and the culture-less ones throw under a
        // comma-separator locale such as fi-FI.
        let! _ = c.Execute(fixture.InvoiceTable, Sql.noParams)

        let theId = System.Guid "0f8fad5b-d9cb-469f-a165-70867728950e"
        let theTotal = 1234.5678M
        let theDate = System.DateTime(2026, 8, 27, 10, 30, 0)

        let! _ =
            c.Execute(
                "INSERT INTO Invoice (Id, Total, Issued, Rebate) VALUES (@id, @total, @issued, @rebate)",
                [| Sql.p "id" (SqlGuid theId)
                   Sql.p "total" (SqlDecimal theTotal)
                   Sql.p "issued" (SqlDate theDate)
                   Sql.p "rebate" SqlNull |]
            )

        let! inv = c.Query("SELECT Id, Total, Issued, Rebate FROM Invoice", Sql.noParams)

        // Bound to a name rather than left as a statement-position `match`:
        // inside a computation expression that shape compiles to the builder's
        // Combine, which Fable's Rust async builder does not implement (G20).
        let invoiceChecks =
            match ResultSet.tryHead inv with
            | None -> [| fail "invoice row present" "no row came back" |]
            | Some row ->
                [| check "guid round-trips" (string theId) (string (Row.guid row "Id"))

                   // exact, not approximately: a float round trip would fail this
                   isTrue "decimal round-trips exactly" (Row.decimal row "Total" = theTotal)

                   isTrue "date round-trips" (Row.dateTime row "Issued" = theDate)
                   isTrue "nullable decimal is None" (Row.decimalOpt row "Rebate").IsNone

                   // the stored text must be locale-independent and identical on
                   // every backend, so assert the encoding itself, not only the
                   // round trip
                   check "decimal stored invariantly" "1234.5678" (Row.text row "Total")
                   check "date stored as ISO-8601" "2026-08-27T10:30:00.0000000" (Row.text row "Issued") |]

        results.AddRange invoiceChecks

        // 10. the query builder, against a real engine.
        //
        // QueryTests already pins down the SQL text for every vendor; this
        // proves the text the generator produces is SQL those engines actually
        // accept, which is the half a string comparison cannot check. The column
        // definitions (Cols, below) are the very ones QueryTests renders from.

        // Seeded rows, after the update and the committed insert above:
        // 1 Alfreds/Germany/101.5, 2 Berglunds/Sweden/251, 3 Cactus/-/0,
        // 100 Kept/-/0.
        let! filtered =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.Balance >. 100.0)
                 |> Query.orderByDesc Cols.Balance
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "query: where, order and project"
                "Berglunds,Alfreds"
                (filtered |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! paged =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.orderBy Cols.CustomerId
                 |> Query.skip 1
                 |> Query.take 2
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "query: skip and take"
                "Berglunds,Cactus"
                (paged |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! inList =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.Name |=| [| "Alfreds"; "Kept" |])
                 |> Query.orderBy Cols.CustomerId
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "query: IN binds one parameter per value"
                "Alfreds,Kept"
                (inList |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! liked =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.Name =% "B%")
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check "query: LIKE" "Berglunds" (liked |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! nulls =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Expr.isNull Cols.Country.E)
                 |> Query.orderBy Cols.CustomerId
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "query: IS NULL"
                "Cactus,Kept"
                (nulls |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! total = Db.count c (Query.from Cols.table |> Query.where (Cols.Balance >. 0.0))
        results.Add(check "query: count" "2" (string total))

        let! anyGerman = Db.exists c (Query.from Cols.table |> Query.where (Cols.Country == "Germany"))
        results.Add(isTrue "query: exists finds a match" anyGerman)

        let! anyMartian = Db.exists c (Query.from Cols.table |> Query.where (Cols.Country == "Mars"))
        results.Add(isTrue "query: exists rejects a miss" (not anyMartian))

        let! balanceSum = Db.sum c Cols.Balance (Query.from Cols.table)

        results.Add(
            check
                "query: sum"
                "352.5"
                (match balanceSum with
                 | Some(SqlFloat f) -> string f
                 | Some(SqlDecimal d) -> Convert.decimalToText d
                 | Some other -> "unexpected " + SqlValue.typeName other
                 | None -> "no rows")
        )

        // A self join needs both sides aliased, and exercises the JOIN .. ON
        // rendering against the engine rather than against a string.
        let! joined =
            Db.query
                c
                (Query.fromAs Cols.table "a"
                 |> Query.joinAs
                     Cols.table
                     "b"
                     (Expr.eq ((Col.onAlias "a" Cols.CustomerId)).E ((Col.onAlias "b" Cols.CustomerId)).E)
                 |> Query.where (Expr.eq ((Col.onAlias "a" Cols.Name)).E (Literal(SqlText "Alfreds")))
                 |> Query.selectAs [| "Other", (Col.onAlias "b" Cols.Name).E |])

        results.Add(
            check
                "query: self join"
                "Alfreds"
                (joined |> ResultSet.map (fun r -> Row.text r "Other") |> String.concat ",")
        )

        // Grouping with an aggregate and a HAVING clause, which is where the
        // engines are fussiest about what may appear in the select list.
        let! grouped =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.groupBy [| Cols.Balance.E |]
                 |> Query.selectAs [| "Balance", Cols.Balance.E; "n", Expr.count |]
                 |> Query.having (Expr.gt Expr.count (Literal(SqlInt 1L))))

        results.Add(check "query: group by with having" "1" (string (ResultSet.rowCount grouped)))

        // 11. writes, against a real engine.
        //
        // Rows so far: 1 Alfreds/Germany/101.5, 2 Berglunds/Sweden/251,
        // 3 Cactus/-/0, 100 Kept/-/0.
        let! inserted =
            Db.insert
                c
                (Insert.into Cols.table
                 |> Insert.set Cols.CustomerId 200
                 |> Insert.set Cols.Name "Inserted"
                 |> Insert.setOpt Cols.Country (Some "Spain")
                 |> Insert.set Cols.Balance 5.0)

        results.Add(check "insert reports one row" "1" (string inserted))

        let! afterInsert = Db.tryHead c (Query.from Cols.table |> Query.where (Cols.CustomerId == 200))

        results.Add(
            check
                "insert wrote the values"
                "Inserted/Spain"
                (match afterInsert with
                 | Some row ->
                     Row.text row "Name"
                     + "/"
                     + (Row.textOpt row "Country" |> Option.defaultValue "-")
                 | None -> "no row")
        )

        // An update written in terms of the row it updates: one statement, no
        // read-then-write.
        let! bumped =
            Db.update
                c
                (Update.table Cols.table
                 |> Update.setExpr Cols.Balance (Expr.add Cols.Balance.E (Literal(SqlFloat 2.5)))
                 |> Update.whereKey Cols.CustomerId 200)

        results.Add(check "update reports one row" "1" (string bumped))

        let! afterUpdate = Db.tryHead c (Query.from Cols.table |> Query.where (Cols.CustomerId == 200))

        results.Add(
            check
                "update computed from the current row"
                "7.5"
                (match afterUpdate with
                 | Some row -> string (Row.float row "Balance")
                 | None -> "no row")
        )

        let! setNull =
            Db.update
                c
                (Update.table Cols.table
                 |> Update.setOpt Cols.Country None
                 |> Update.whereKey Cols.CustomerId 200)

        results.Add(check "update to NULL reports one row" "1" (string setNull))

        let! afterNull = Db.tryHead c (Query.from Cols.table |> Query.where (Cols.CustomerId == 200))

        results.Add(
            isTrue
                "update wrote a NULL"
                (match afterNull with
                 | Some row -> (Row.textOpt row "Country").IsNone
                 | None -> false)
        )

        let! removed = Db.delete c (Delete.from Cols.table |> Delete.whereKey Cols.CustomerId 200)
        results.Add(check "delete reports one row" "1" (string removed))

        let! goneCount = Db.count c (Query.from Cols.table |> Query.where (Cols.CustomerId == 200))
        results.Add(check "delete removed the row" "0" (string goneCount))

        // A batch, applied in one transaction -- SQLProvider's SubmitUpdates.
        let! batched =
            Db.submit
                c
                (Batch.empty
                 |> Batch.insert (
                     Insert.into Cols.table
                     |> Insert.set Cols.CustomerId 301
                     |> Insert.set Cols.Name "Batch one"
                     |> Insert.set Cols.Balance 1.0
                 )
                 |> Batch.insert (
                     Insert.into Cols.table
                     |> Insert.set Cols.CustomerId 302
                     |> Insert.set Cols.Name "Batch two"
                     |> Insert.set Cols.Balance 2.0
                 )
                 |> Batch.update (
                     Update.table Cols.table
                     |> Update.set Cols.Name "Batch one renamed"
                     |> Update.whereKey Cols.CustomerId 301
                 ))

        results.Add(check "a batch reports every row it touched" "3" (string batched))

        let! batchNames =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.CustomerId >=. 301)
                 |> Query.orderBy Cols.CustomerId
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "the batch was applied in order"
                "Batch one renamed,Batch two"
                (batchNames |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        // A batch is all-or-nothing: the second insert collides with the key
        // written by the first, so neither may survive.
        let mutable batchFailed = false

        try
            let! _ =
                Db.submit
                    c
                    (Batch.empty
                     |> Batch.insert (
                         Insert.into Cols.table
                         |> Insert.set Cols.CustomerId 400
                         |> Insert.set Cols.Name "Doomed"
                         |> Insert.set Cols.Balance 0.0
                     )
                     |> Batch.insert (
                         Insert.into Cols.table
                         |> Insert.set Cols.CustomerId 400
                         |> Insert.set Cols.Name "Duplicate key"
                         |> Insert.set Cols.Balance 0.0
                     ))

            ()
        with _ ->
            batchFailed <- true

        results.Add(isTrue "a failing batch reports the failure" batchFailed)

        let! rolledBack = Db.count c (Query.from Cols.table |> Query.where (Cols.CustomerId == 400))
        results.Add(check "a failing batch rolls the whole thing back" "0" (string rolledBack))

        // 12. subqueries, against a real engine.
        //
        // Rows at this point: 1 Alfreds/Germany, 2 Berglunds/Sweden,
        // 3 Cactus/-, 100 Kept/-, 301 Batch one renamed/-, 302 Batch two/-.
        let germanIds =
            Query.from Cols.table
            |> Query.where (Cols.Country == "Germany")
            |> Query.select [| Cols.CustomerId.E |]

        let! inSub =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Expr.inQuery Cols.CustomerId.E germanIds)
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check "subquery: IN" "Alfreds" (inSub |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        // Correlated: the subquery refers to the outer row. Self-correlation is
        // enough to prove the SQL is accepted and the names resolve.
        let! correlated =
            Db.query
                c
                (Query.fromAs Cols.table "outer_c"
                 |> Query.where (
                     Expr.exists (
                         Query.from Cols.table
                         |> Query.where (Expr.eq Cols.CustomerId.E ((Col.onAlias "outer_c" Cols.CustomerId)).E)
                         |> Query.where (Cols.Country == "Sweden")
                     )
                 )
                 |> Query.selectAs [| "Name", (Col.onAlias "outer_c" Cols.Name).E |])

        results.Add(
            check
                "subquery: correlated EXISTS"
                "Berglunds"
                (correlated |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        // A scalar subquery in value position: everyone above the average.
        let! aboveAverage =
            Db.count
                c
                (Query.from Cols.table
                 |> Query.where (
                     Expr.gt Cols.Balance.E (Expr.scalarQuery (Query.avgQuery Cols.Balance (Query.from Cols.table)))
                 ))

        results.Add(check "subquery: scalar comparison" "2" (string aboveAverage))

        // 13. bulk insert, generated keys and the transaction wrapper.
        let! bulk =
            Db.insertMany
                c
                [ Insert.into Cols.table
                  |> Insert.set Cols.CustomerId 501
                  |> Insert.set Cols.Name "Bulk one"
                  |> Insert.set Cols.Balance 1.0
                  Insert.into Cols.table
                  |> Insert.set Cols.CustomerId 502
                  |> Insert.set Cols.Name "Bulk two"
                  |> Insert.set Cols.Balance 2.0
                  Insert.into Cols.table
                  |> Insert.set Cols.CustomerId 503
                  |> Insert.set Cols.Name "Bulk three"
                  |> Insert.set Cols.Balance 3.0 ]

        results.Add(check "insertMany writes every row in one statement" "3" (string bulk))

        let! bulkCount = Db.count c (Query.from Cols.table |> Query.where (Cols.CustomerId >=. 501))
        results.Add(check "insertMany rows are all there" "3" (string bulkCount))

        // The key the database generated. PostgreSQL answers in the statement;
        // SQLite follows up with last_insert_rowid(). Both land here.
        let! generated =
            Db.insertReturning
                c
                Cols.CustomerId
                (Insert.into Cols.table
                 |> Insert.set Cols.CustomerId 601
                 |> Insert.set Cols.Name "Keyed"
                 |> Insert.set Cols.Balance 0.0)

        results.Add(
            check
                "insertReturning hands back the key"
                "601"
                (match generated with
                 | SqlInt n -> string n
                 | SqlDecimal d -> string (int64 d)
                 | other -> "unexpected " + SqlValue.typeName other)
        )

        // A transaction that finishes commits.
        do!
            Db.inTransaction
                c
                (async {
                    let! _ =
                        Db.insert
                            c
                            (Insert.into Cols.table
                             |> Insert.set Cols.CustomerId 701
                             |> Insert.set Cols.Name "Committed"
                             |> Insert.set Cols.Balance 0.0)

                    return ()
                })

        let! committed = Db.count c (Query.from Cols.table |> Query.where (Cols.CustomerId == 701))
        results.Add(check "inTransaction commits when the body finishes" "1" (string committed))

        // A transaction whose body fails rolls back.
        let mutable txFailed = false

        try
            do!
                Db.inTransaction
                    c
                    (async {
                        let! _ =
                            Db.insert
                                c
                                (Insert.into Cols.table
                                 |> Insert.set Cols.CustomerId 702
                                 |> Insert.set Cols.Name "Rolled back"
                                 |> Insert.set Cols.Balance 0.0)

                        return failwith $"deliberate, calling run with c: {c}, fixture: {fixture}"
                    })
        with _ ->
            txFailed <- true

        results.Add(isTrue "inTransaction reports a failing body" txFailed)

        let! rolled = Db.count c (Query.from Cols.table |> Query.where (Cols.CustomerId == 702))
        results.Add(check "inTransaction rolls back a failing body" "0" (string rolled))

        // 14. LIKE from a value, against a real engine.
        //
        // A name containing a literal % is what makes the escaping observable:
        // unescaped, the pattern would match every row instead of this one.
        let! _ =
            Db.insert
                c
                (Insert.into Cols.table
                 |> Insert.set Cols.CustomerId 801
                 |> Insert.set Cols.Name "50% off"
                 |> Insert.set Cols.Balance 0.0)

        let! literalPercent =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Expr.contains Cols.Name.E "50% off")
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "contains treats % in the value as a character"
                "50% off"
                (literalPercent
                 |> ResultSet.map (fun r -> Row.text r "Name")
                 |> String.concat ",")
        )

        // The same search without the escaping would match everything, so this
        // is the assertion that the escaping is doing something.
        let! rawPercent =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Binary(Like, Cols.Name.E, Literal(SqlText "%50% off%")))
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            isTrue
                "an unescaped pattern would have matched more"
                (ResultSet.rowCount rawPercent >= ResultSet.rowCount literalPercent)
        )

        let! startsWith =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Expr.startsWith Cols.Name.E "Alf")
                 |> Query.select [| Cols.Name.E |])

        results.Add(
            check
                "startsWith anchors the front"
                "Alfreds"
                (startsWith |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        // CASE WHEN, evaluated by the engine rather than after the fact.
        let! banded =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.CustomerId <=. 3)
                 |> Query.orderBy Cols.CustomerId
                 |> Query.selectAs
                     [| "band",
                        Expr.ifThenElse (Cols.Balance >. 100.0) (Literal(SqlText "high")) (Literal(SqlText "low")) |])

        results.Add(
            check
                "CASE WHEN is evaluated by the engine"
                "high,high,low"
                (banded |> ResultSet.map (fun r -> Row.text r "band") |> String.concat ",")
        )

        // 15. numeric functions, casts and date differences, against the row
        // whose balance is known to be 101.5 here.
        let! numeric =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.CustomerId == 1)
                 |> Query.selectAs
                     [| "up", Expr.ceiling Cols.Balance.E
                        "down", Expr.floor Cols.Balance.E
                        "near", Expr.round Cols.Balance.E
                        "one", Expr.roundTo 1 Cols.Balance.E
                        "zero", Expr.truncate Cols.Balance.E
                        "big", Expr.greatest Cols.Balance.E (Literal(SqlFloat 150.0))
                        "small", Expr.least Cols.Balance.E (Literal(SqlFloat 150.0))
                        "text", Expr.castText Cols.CustomerId.E
                        "num", Expr.castInt (Literal(SqlText "42")) |])

        let numericChecks =
            match ResultSet.tryHead numeric with
            | None -> [| fail "numeric functions row present" "no row came back" |]
            | Some row ->
                [| check "ceiling" "102" (string (Row.float row "up"))
                   check "floor" "101" (string (Row.float row "down"))
                   check "round" "102" (string (Row.float row "near"))
                   check "round to decimals" "101.5" (string (Row.float row "one"))
                   check "truncate" "101" (string (Row.float row "zero"))
                   check "greatest" "150" (string (Row.float row "big"))
                   check "least" "101.5" (string (Row.float row "small"))
                   check "castText" "1" (Row.text row "text")
                   check "castInt" "42" (string (Row.int64 row "num")) |]

        results.AddRange numericChecks

        // The invoice's Issued is 2026-08-27T10:30:00, stored as text through
        // the shared encoding -- so this also proves the date-difference SQL
        // reads that encoding on every engine.
        let invIssued = Col.date "Invoice" "Issued"

        let! diffs =
            Db.query
                c
                (Query.from "Invoice"
                 |> Query.selectAs
                     [| "days",
                        Expr.dateDiffDays
                            (Literal(SqlDate(System.DateTime(2026, 8, 29, 1, 0, 0))))
                            invIssued.E
                        "secs",
                        Expr.dateDiffSecs
                            (Literal(SqlDate(System.DateTime(2026, 8, 27, 10, 31, 40))))
                            invIssued.E |])

        let diffChecks =
            match ResultSet.tryHead diffs with
            | None -> [| fail "date difference row present" "no row came back" |]
            | Some row ->
                [| check "dateDiffDays counts date boundaries" "2" (string (Row.int64 row "days"))
                   check "dateDiffSecs" "100" (string (Row.int64 row "secs")) |]

        results.AddRange diffChecks

        // 16. COUNT(DISTINCT x): two named countries among the first three
        // rows; the NULL one does not count.
        let! distinctCountries =
            Db.count
                c
                (Query.from Cols.table
                 |> Query.where (Cols.CustomerId <=. 3)
                 |> Query.distinct
                 |> Query.selectCol Cols.Country)

        results.Add(check "count of a distinct column" "2" (string distinctCountries))

        // exists must not inherit countQuery's DISTINCT strictness: whether
        // anything matches is the same question with or without deduplication.
        let! distinctAny =
            Db.exists c (Query.from Cols.table |> Query.where (Cols.CustomerId <=. 3) |> Query.distinct)

        results.Add(isTrue "exists on a DISTINCT query" distinctAny)

        // 17. set operations. Germany's names union names with a balance over
        // 100 -- Alfreds is in both, so UNION folds it and UNION ALL does not.
        let germanNames =
            Query.from Cols.table
            |> Query.where (Cols.CustomerId <=. 3)
            |> Query.where (Cols.Country == "Germany")
            |> Query.selectCol Cols.Name

        let richNames =
            Query.from Cols.table
            |> Query.where (Cols.CustomerId <=. 3)
            |> Query.where (Cols.Balance >. 100.0)
            |> Query.selectCol Cols.Name

        let! unioned = Db.query c (germanNames |> Query.union richNames |> Query.orderBy Cols.Name)

        results.Add(
            check
                "UNION deduplicates"
                "Alfreds,Berglunds"
                (unioned |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        let! concatenated =
            Db.query c (germanNames |> Query.unionAll richNames |> Query.orderBy Cols.Name)

        results.Add(
            check
                "UNION ALL keeps duplicates, and the compound ordering holds"
                "Alfreds,Alfreds,Berglunds"
                (concatenated |> ResultSet.map (fun r -> Row.text r "Name") |> String.concat ",")
        )

        // 18. a join read through qualified aliases, so the two tables' columns
        // cannot collide. The ON is a constant truth: these two tables share no
        // key, and a cross join of three customers and one invoice is exactly
        // what shows each side keeps its own values.
        let! paired =
            Db.query
                c
                (Query.from Cols.table
                 |> Query.where (Cols.CustomerId <=. 3)
                 |> Query.join "Invoice" (Expr.eq (Literal(SqlInt 1L)) (Literal(SqlInt 1L)))
                 |> Query.orderBy Cols.CustomerId
                 |> Query.selectAs
                     [| "Customer_Name", Cols.Name.E
                        "Customer_Balance", Cols.Balance.E
                        "Invoice_Id", (Col.guid "Invoice" "Id").E
                        "Invoice_Total", (Col.decimal "Invoice" "Total").E |])

        let pairedChecks =
            match ResultSet.tryHead paired with
            | None -> [| fail "qualified join row present" "no row came back" |]
            | Some row ->
                [| check "qualified join row count" "3" (string (ResultSet.rowCount paired))
                   check "qualified customer column" "Alfreds" (Row.text row "Customer_Name")
                   check
                       "qualified invoice column"
                       "0f8fad5b-d9cb-469f-a165-70867728950e"
                       (string (Row.guid row "Invoice_Id"))
                   isTrue "qualified decimal survives" (Row.decimal row "Invoice_Total" = 1234.5678M) |]

        results.AddRange pairedChecks

        // 19. the row-or-error enders.
        let! headRow = Db.head c (Query.from Cols.table |> Query.where (Cols.CustomerId == 1))
        results.Add(check "head returns the row" "Alfreds" (Row.text headRow "Name"))

        let mutable headFailure = ""

        try
            let! _ = Db.head c (Query.from Cols.table |> Query.where (Cols.CustomerId == -99))
            ()
        with e ->
            headFailure <- e.Message

        results.Add(check "head on no rows is an error" "head: the query returned no rows" headFailure)

        let! exactly = Db.exactlyOne c (Query.from Cols.table |> Query.where (Cols.CustomerId == 2))
        results.Add(check "exactlyOne returns the row" "Berglunds" (Row.text exactly "Name"))

        let! nobody = Db.tryExactlyOne c (Query.from Cols.table |> Query.where (Cols.CustomerId == -99))
        results.Add(isTrue "tryExactlyOne on no rows is None" nobody.IsNone)

        let mutable manyFailure = ""

        try
            let! _ = Db.tryExactlyOne c (Query.from Cols.table |> Query.where (Cols.CustomerId <=. 3))
            ()
        with e ->
            manyFailure <- e.Message

        results.Add(
            check
                "tryExactlyOne on many rows is an error"
                "tryExactlyOne: the query returned more than one row"
                manyFailure
        )

        return results.ToArray()
    }
