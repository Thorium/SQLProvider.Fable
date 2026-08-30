/// The reference for what this library can express, and what it cannot.
///
/// Modelled on SQLProvider's own `tests/SqlProvider.Tests/QueryTests.fs`, which
/// serves the same purpose there: one file that says, case by case, which
/// queries work. Everything here is pure logic with no database, so it runs on
/// every target the library does, and it is where the vendor differences get
/// pinned down -- a rendering that changes silently is what makes a "portable"
/// query layer stop being portable.
///
/// Covered, in rough order of how often SQLProvider's own tests use it:
///
///   select, where, join, groupBy/having, sortBy/sortByDescending/thenBy,
///   take, skip, distinct, exists, count, sum/avg/min/max, LIKE (`=%`),
///   IN (`|=|`), IS NULL, leftJoin, self joins via aliases, the canonical
///   string and date functions (upper, lower, trim, length, substring,
///   replace, indexOf, abs, year, month, day, hour, minute, second), and
///   subqueries: `IN (SELECT ..)`, correlated `EXISTS`, `NOT EXISTS`, a scalar
///   subquery in value position, and SQLProvider's `contains` and `all`.
///
/// Also, driven by what production code actually calls: `.Contains`,
/// `.StartsWith` and `.EndsWith` as escaped LIKE patterns; `.Date`;
/// `.AddYears`/`.AddMonths`/`.AddDays`/`.AddHours`/`.AddMinutes`/`.AddSeconds`
/// by a constant; `if/then/else` as CASE WHEN; aggregates over a CASE
/// (`g.Sum(fun r -> if .. then 1 else 0)`); several aggregates in one
/// projection; COUNT(DISTINCT x); `ceiling`/`floor`/`round`/`roundTo`/
/// `truncate`/`greatest`/`least` (spelled with CAST arithmetic on SQLite,
/// whose common builds lack the math functions); `castText`/`castInt`;
/// `dateDiffDays`/`dateDiffSecs`; and `.Union()`/`.Concat()`/`.Intersect()`/
/// `.Except()` as UNION / UNION ALL / INTERSECT / EXCEPT.
///
/// Writes: insert, update (including `Balance = Balance + n`), delete,
/// multi-row insert, and a batch in one transaction.
///
/// Not covered yet, with the reason:
///
///   - `groupJoin` and `leftOuterJoin .. into g` -- the flattening SQLProvider
///     does for these has no equivalent here yet; a plain `leftJoin` does.
///   - `select` of a tuple or a constructed record -- projections here are a
///     list of columns, and the shaping happens in the `ofRow` mapper; a join
///     reads both entities through the generated `qualified` aliases.
///   - Nested queries in a FROM clause. SQLProvider does not do these either.
///   - `skipWhile`/`takeWhile`/`last` -- unsupported in SQLProvider too.
///   - `Sqrt`/`Pow` and trigonometry -- SQLite as commonly built has no math
///     functions to spell them with.
///   - Date arithmetic by a column rather than a constant. SQLite takes only
///     a literal modifier, which is the same limit SQLProvider documents.
module SQLProvider.Fable.SmokeTests.QueryTests

open SQLProvider.Fable
open SQLProvider.Fable.SmokeTests.Harness

// --- the shape a schema code generator will emit --------------------------

module Customer =
    let table = "Customer"
    let CustomerId = Col.int table "CustomerId"
    let Name = Col.text table "Name"
    let Country = Col.text table "Country"
    let Balance = Col.float table "Balance"
    let Joined = Col.date table "Joined"

module Order =
    let table = "Orders"
    let OrderId = Col.int table "OrderId"
    let CustomerId = Col.int table "CustomerId"
    let Freight = Col.decimal table "Freight"

/// Renders a query and folds the parameters into the text, so one assertion
/// covers both the SQL and what got bound to it.
let private render (vendor: Vendor) (q: Query) =
    let sql, ps = Db.toSql vendor q

    let values =
        ps
        |> Array.map (fun p ->
            match p.Value with
            | SqlInt i -> string i
            | SqlFloat f -> string f
            | SqlText s -> s
            | SqlNull -> "null"
            | SqlBool b -> (if b then "true" else "false")
            | other -> SqlValue.typeName other)
        |> String.concat ","

    if values = "" then sql else $"{sql} | {values}"

/// The two spellings, which build the same `Query`. The computation expression
/// is the one to read first:
///
///     sqlQuery {
///         from Customer.table
///         where (Customer.Country == "UK")
///         sortByDescending Customer.Balance
///         select (Customer.Name, Customer.Country)
///         take 10
///     }
///
/// and the pipeline is the same thing when a query is assembled in pieces:
///
///     Query.from Customer.table
///     |> Query.where (Customer.Country == "UK")
///     |> Query.orderByDesc Customer.Balance
///     |> Query.selectCol Customer.Name
///
/// Most cases below use the pipeline because they assert one clause at a time;
/// the equivalence cases at the end check the two agree.
let run () : TestResult[] =
    let results = ResizeArray<TestResult>()

    // --- the basics -------------------------------------------------------

    results.Add(check "select all" "SELECT * FROM Customer" (render Sqlite (Query.from Customer.table)))

    results.Add(
        check
            "where binds a parameter rather than inlining it"
            "SELECT * FROM Customer WHERE (Customer.Country = @p0) | UK"
            (render Sqlite (Query.from Customer.table |> Query.where (Customer.Country == "UK")))
    )

    // Successive where calls are ANDed, so a query can be assembled a clause at
    // a time -- which is what makes it composable.
    results.Add(
        check
            "successive wheres are ANDed"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) AND (Customer.Balance > @p1)) | UK,100"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Country == "UK")
                 |> Query.where (Customer.Balance >. 100.0)))
    )

    results.Add(
        check
            "orWhere widens instead"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) OR (Customer.Country = @p1)) | UK,USA"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Country == "UK")
                 |> Query.orWhere (Customer.Country == "USA")))
    )

    results.Add(
        check
            "combined conditions nest as written"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) AND ((Customer.Balance > @p1) OR (Customer.Balance < @p2))) | UK,100,10"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (
                     (Customer.Country == "UK")
                     .&&. ((Customer.Balance >. 100.0) .||. (Customer.Balance <. 10.0))
                 )))
    )

    results.Add(
        check
            "whereAll ANDs without needing the operator"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) AND (Customer.Balance > @p1)) | UK,100"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.whereAll [ Customer.Country == "UK"; Customer.Balance >. 100.0 ]))
    )

    results.Add(
        check
            "whereAny ORs the alternatives into one clause"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) OR (Customer.Country = @p1)) | UK,USA"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.whereAny [ Customer.Country == "UK"; Customer.Country == "USA" ]))
    )

    // --- projection, ordering, paging -------------------------------------

    results.Add(
        check
            "select names the columns and does not alias a plain one"
            "SELECT Customer.CustomerId, Customer.Name FROM Customer"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.select [| Customer.CustomerId.E; Customer.Name.E |]))
    )

    results.Add(
        check
            "computed columns get an alias"
            "SELECT UPPER(Customer.Name) AS expr0 FROM Customer"
            (render Sqlite (Query.from Customer.table |> Query.select [| Expr.upper Customer.Name.E |]))
    )

    results.Add(
        check
            "selectAs uses the name given"
            "SELECT UPPER(Customer.Name) AS shout FROM Customer"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs [| "shout", Expr.upper Customer.Name.E |]))
    )

    results.Add(
        check
            "ordering chains"
            "SELECT * FROM Customer ORDER BY Customer.Country ASC, Customer.Balance DESC"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.orderBy Customer.Country
                 |> Query.thenByDesc Customer.Balance))
    )

    results.Add(
        check
            "distinct"
            "SELECT DISTINCT Customer.Country FROM Customer"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.select [| Customer.Country.E |]
                 |> Query.distinct))
    )

    results.Add(
        check "take alone" "SELECT * FROM Customer LIMIT 5" (render Sqlite (Query.from Customer.table |> Query.take 5))
    )

    results.Add(
        check
            "skip and take"
            "SELECT * FROM Customer LIMIT 5 OFFSET 10"
            (render Sqlite (Query.from Customer.table |> Query.skip 10 |> Query.take 5))
    )

    // OFFSET without LIMIT is a syntax error on SQLite and MySQL, so each needs
    // its own stand-in maximum. PostgreSQL takes it bare.
    results.Add(
        check
            "skip alone, SQLite"
            "SELECT * FROM Customer LIMIT -1 OFFSET 10"
            (render Sqlite (Query.from Customer.table |> Query.skip 10))
    )

    results.Add(
        check
            "skip alone, PostgreSQL"
            "SELECT * FROM Customer OFFSET 10"
            (render Postgres (Query.from Customer.table |> Query.skip 10))
    )

    results.Add(
        check
            "skip alone, MySQL"
            "SELECT * FROM Customer LIMIT 18446744073709551615 OFFSET 10"
            (render MySql (Query.from Customer.table |> Query.skip 10))
    )

    // --- joins ------------------------------------------------------------

    results.Add(
        check
            "inner join"
            "SELECT * FROM Customer INNER JOIN Orders ON (Customer.CustomerId = Orders.CustomerId)"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.join Order.table (Customer.CustomerId == Order.CustomerId)))
    )

    results.Add(
        check
            "left join under an alias"
            "SELECT * FROM Customer LEFT JOIN Orders AS o ON (Customer.CustomerId = o.CustomerId)"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.leftJoinAs
                     Order.table
                     "o"
                     (Expr.eq Customer.CustomerId.E ((Col.onAlias "o" Order.CustomerId)).E)))
    )

    // A table joined to itself needs both sides aliased, which is the whole
    // reason Col.onAlias exists.
    results.Add(
        check
            "self join"
            "SELECT * FROM Customer AS a INNER JOIN Customer AS b ON (a.Country = b.Country)"
            (render
                Sqlite
                (Query.fromAs Customer.table "a"
                 |> Query.joinAs
                     Customer.table
                     "b"
                     (Expr.eq ((Col.onAlias "a" Customer.Country)).E ((Col.onAlias "b" Customer.Country)).E)))
    )

    // --- LIKE, IN, NULL ---------------------------------------------------

    results.Add(
        check
            "like"
            "SELECT * FROM Customer WHERE (Customer.Name LIKE @p0) | A%"
            (render Sqlite (Query.from Customer.table |> Query.where (Customer.Name =% "A%")))
    )

    results.Add(
        check
            "in, one parameter per value"
            "SELECT * FROM Customer WHERE Customer.Country IN (@p0, @p1) | UK,USA"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Country |=| [| "UK"; "USA" |])))
    )

    // An empty IN-list is a syntax error everywhere, and it matches nothing, so
    // say that instead of generating it.
    results.Add(
        check
            "in over nothing matches nothing"
            "SELECT * FROM Customer WHERE (1 = 0)"
            (render Sqlite (Query.from Customer.table |> Query.where (Customer.Country |=| [||])))
    )

    results.Add(
        check
            "is null"
            "SELECT * FROM Customer WHERE (Customer.Country IS NULL)"
            (render Sqlite (Query.from Customer.table |> Query.where (Expr.isNull Customer.Country.E)))
    )

    results.Add(
        check
            "not"
            "SELECT * FROM Customer WHERE NOT ((Customer.Country = @p0)) | UK"
            (render Sqlite (Query.from Customer.table |> Query.where (Expr.not' (Customer.Country == "UK"))))
    )

    // --- aggregates and grouping ------------------------------------------

    results.Add(
        check
            "count drops ordering and paging"
            "SELECT COUNT(*) AS count FROM Customer WHERE (Customer.Country = @p0) | UK"
            (render
                Sqlite
                (Query.countQuery (
                    Query.from Customer.table
                    |> Query.where (Customer.Country == "UK")
                    |> Query.orderBy Customer.Name
                    |> Query.take 5
                )))
    )

    results.Add(
        check
            "sum"
            "SELECT SUM(Customer.Balance) AS sum FROM Customer"
            (render Sqlite (Query.sumQuery Customer.Balance (Query.from Customer.table)))
    )

    // Same rule as count: LIMIT/OFFSET apply after aggregation, so a kept Skip
    // would page past the single result row and the sum would come back as no
    // rows at all.
    results.Add(
        check
            "aggregates drop ordering and paging like count"
            "SELECT SUM(Customer.Balance) AS sum FROM Customer WHERE (Customer.Country = @p0) | UK"
            (render
                Sqlite
                (Query.sumQuery
                    Customer.Balance
                    (Query.from Customer.table
                     |> Query.where (Customer.Country == "UK")
                     |> Query.orderBy Customer.Name
                     |> Query.skip 2
                     |> Query.take 5)))
    )

    // A grouped query has one aggregate value per group; reading it as a scalar
    // would take whichever group came first, so it is refused rather than
    // quietly wrong.
    results.Add(
        (let name = "an aggregate over a grouped query is refused"

         try
             Query.countQuery (Query.from Customer.table |> Query.groupByCol Customer.Country)
             |> ignore

             fail name "expected countQuery to refuse the GROUP BY"
         with _ ->
             pass name)
    )

    results.Add(
        check
            "group by with having"
            "SELECT Customer.Country, COUNT(*) AS n FROM Customer GROUP BY Customer.Country HAVING (COUNT(*) > @p0) | 2"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.groupByCol Customer.Country
                 |> Query.selectCol Customer.Country
                 |> Query.selectExpr "n" Expr.count
                 |> Query.having (Expr.gt Expr.count (Literal(SqlInt 2L)))))
    )

    // --- canonical functions, where the vendors disagree ------------------

    let upperQuery =
        Query.from Customer.table
        |> Query.where (Expr.eq (Expr.upper Customer.Name.E) (Literal(SqlText "BOB")))

    results.Add(
        check
            "upper is the same everywhere"
            "SELECT * FROM Customer WHERE (UPPER(Customer.Name) = @p0) | BOB"
            (render Postgres upperQuery)
    )

    let lengthQuery =
        Query.from Customer.table
        |> Query.where (Expr.gt (Expr.length Customer.Name.E) (Literal(SqlInt 3L)))

    results.Add(
        check
            "length, SQLite"
            "SELECT * FROM Customer WHERE (LENGTH(Customer.Name) > @p0) | 3"
            (render Sqlite lengthQuery)
    )

    results.Add(
        check
            "length, PostgreSQL"
            "SELECT * FROM Customer WHERE (CHAR_LENGTH(Customer.Name) > @p0) | 3"
            (render Postgres lengthQuery)
    )

    let yearQuery =
        Query.from Customer.table
        |> Query.where (Expr.eq (Expr.year Customer.Joined.E) (Literal(SqlInt 2026L)))

    results.Add(
        check
            "year, SQLite goes through STRFTIME"
            "SELECT * FROM Customer WHERE (CAST(STRFTIME('%Y', Customer.Joined) AS INTEGER) = @p0) | 2026"
            (render Sqlite yearQuery)
    )

    results.Add(
        check
            "year, PostgreSQL uses EXTRACT"
            "SELECT * FROM Customer WHERE (EXTRACT(YEAR FROM Customer.Joined) = @p0) | 2026"
            (render Postgres yearQuery)
    )

    results.Add(
        check
            "year, MySQL has the plain function"
            "SELECT * FROM Customer WHERE (YEAR(Customer.Joined) = @p0) | 2026"
            (render MySql yearQuery)
    )

    let concatQuery =
        Query.from Customer.table
        |> Query.selectAs [| "label", Expr.concat Customer.Name.E (Literal(SqlText "!")) |]

    results.Add(
        check
            "concat, SQLite and PostgreSQL use ||"
            "SELECT (Customer.Name || @p0) AS label FROM Customer | !"
            (render Postgres concatQuery)
    )

    // MySQL reads `||` as a boolean OR unless the server is in ANSI mode.
    results.Add(
        check
            "concat, MySQL needs CONCAT"
            "SELECT CONCAT(Customer.Name, @p0) AS label FROM Customer | !"
            (render MySql concatQuery)
    )

    let indexQuery =
        Query.from Customer.table
        |> Query.selectAs [| "at", Expr.indexOf Customer.Name.E (Literal(SqlText "a")) |]

    results.Add(
        check
            "indexOf, PostgreSQL takes haystack first"
            "SELECT STRPOS(Customer.Name, @p0) AS at FROM Customer | a"
            (render Postgres indexQuery)
    )

    // LOCATE takes the needle first, so the arguments have to be swapped.
    results.Add(
        check
            "indexOf, MySQL takes the needle first"
            "SELECT LOCATE(@p0, Customer.Name) AS at FROM Customer | a"
            (render MySql indexQuery)
    )

    // --- parameter numbering ----------------------------------------------

    // Every literal becomes its own parameter, numbered in the order the SQL
    // mentions it, so nothing a caller supplies is ever concatenated in.
    results.Add(
        check
            "parameters are numbered in SQL order"
            "SELECT * FROM Customer WHERE ((Customer.Country = @p0) AND (Customer.Name = @p1)) LIMIT 1 | UK,Bob"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Country == "UK")
                 |> Query.where (Customer.Name == "Bob")
                 |> Query.take 1))
    )

    // A date literal is encoded through the same Convert the connectors use, so
    // a query and an insert write the same bytes.
    results.Add(
        check
            "a date literal is encoded as invariant text"
            "SELECT * FROM Customer WHERE (Customer.Joined > @p0) | date"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Joined >. System.DateTime(2026, 8, 27, 10, 30, 0))))
    )

    // --- LIKE from a value, with escaping ---------------------------------

    results.Add(
        check
            "contains wraps the value in wildcards"
            "SELECT * FROM Customer WHERE (Customer.Name LIKE @p0 ESCAPE '!') | %bob%"
            (render Sqlite (Query.from Customer.table |> Query.where (Expr.contains Customer.Name.E "bob")))
    )

    results.Add(
        check
            "startsWith anchors the front"
            "SELECT * FROM Customer WHERE (Customer.Name LIKE @p0 ESCAPE '!') | bob%"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Expr.startsWith Customer.Name.E "bob")))
    )

    results.Add(
        check
            "endsWith anchors the back"
            "SELECT * FROM Customer WHERE (Customer.Name LIKE @p0 ESCAPE '!') | %bob"
            (render Sqlite (Query.from Customer.table |> Query.where (Expr.endsWith Customer.Name.E "bob")))
    )

    // The point of the escaping: a search for a literal % finds one, instead of
    // matching every row. This is a wrong-results bug when it is missing, not a
    // loud one, which is why the helpers exist at all.
    results.Add(
        check
            "a wildcard in the value is escaped"
            "%50!% off%"
            (match Expr.contains Customer.Name.E "50% off" with
             | LikeEscaped(_, SqlText pattern) -> pattern
             | _ -> "not a pattern")
    )

    results.Add(
        check
            "an underscore and the escape character are escaped too"
            "%a!_b!!c%"
            (match Expr.contains Customer.Name.E "a_b!c" with
             | LikeEscaped(_, SqlText pattern) -> pattern
             | _ -> "not a pattern")
    )

    // --- date functions ---------------------------------------------------

    let dateOnlyQuery =
        Query.from Customer.table
        |> Query.selectAs [| "d", Expr.dateOnly Customer.Joined.E |]

    results.Add(
        check "date truncation, SQLite" "SELECT DATE(Customer.Joined) AS d FROM Customer" (render Sqlite dateOnlyQuery)
    )

    results.Add(
        check
            "date truncation, PostgreSQL"
            "SELECT DATE_TRUNC('day', Customer.Joined) AS d FROM Customer"
            (render Postgres dateOnlyQuery)
    )

    let addDaysQuery =
        Query.from Customer.table
        |> Query.selectAs [| "d", Expr.addDays Customer.Joined.E 7 |]

    results.Add(
        check
            "addDays, SQLite"
            "SELECT DATETIME(Customer.Joined, '+7 days') AS d FROM Customer"
            (render Sqlite addDaysQuery)
    )

    results.Add(
        check
            "addDays, PostgreSQL"
            "SELECT (Customer.Joined + INTERVAL '7 day') AS d FROM Customer"
            (render Postgres addDaysQuery)
    )

    results.Add(
        check
            "addDays, MySQL"
            "SELECT DATE_ADD(Customer.Joined, INTERVAL 7 DAY) AS d FROM Customer"
            (render MySql addDaysQuery)
    )

    // A negative shift is the same operation, and SQLite needs the sign in the
    // modifier string rather than a separate minus.
    results.Add(
        check
            "a negative shift, SQLite"
            "SELECT DATETIME(Customer.Joined, '-3 months') AS d FROM Customer"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs [| "d", Expr.addMonths Customer.Joined.E -3 |]))
    )

    results.Add(
        check
            "a negative shift, PostgreSQL"
            "SELECT (Customer.Joined + INTERVAL '-3 month') AS d FROM Customer"
            (render
                Postgres
                (Query.from Customer.table
                 |> Query.selectAs [| "d", Expr.addMonths Customer.Joined.E -3 |]))
    )

    // --- CASE WHEN --------------------------------------------------------

    results.Add(
        check
            "if/then/else becomes CASE"
            "SELECT CASE WHEN (Customer.Country = @p0) THEN Customer.Name ELSE @p1 END AS label FROM Customer | UK,other"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "label", Expr.ifThenElse (Customer.Country == "UK") (Customer.Name.E) (Literal(SqlText "other")) |]))
    )

    results.Add(
        check
            "several branches, and no else"
            "SELECT CASE WHEN (Customer.Balance > @p0) THEN @p1 WHEN (Customer.Balance > @p2) THEN @p3 END AS band FROM Customer | 100,high,10,mid"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "band",
                        Expr.caseWhen
                            [ Customer.Balance >. 100.0, Literal(SqlText "high")
                              Customer.Balance >. 10.0, Literal(SqlText "mid") ]
                            None |]))
    )

    // --- subqueries -------------------------------------------------------

    // The subquery shares the outer statement's parameter list, so numbering
    // stays in the order the SQL mentions them across the nesting.
    let ukCustomerIds =
        Query.from Customer.table
        |> Query.where (Customer.Country == "UK")
        |> Query.select [| Customer.CustomerId.E |]

    results.Add(
        check
            "IN over a subquery"
            "SELECT * FROM Orders WHERE Orders.CustomerId IN (SELECT Customer.CustomerId FROM Customer WHERE (Customer.Country = @p0)) | UK"
            (render Sqlite (Query.from Order.table |> Query.where (Order.CustomerId |=? ukCustomerIds)))
    )

    results.Add(
        check
            "parameters stay in SQL order across the nesting"
            "SELECT * FROM Orders WHERE ((Orders.OrderId > @p0) AND Orders.CustomerId IN (SELECT Customer.CustomerId FROM Customer WHERE (Customer.Country = @p1))) | 5,UK"
            (render
                Sqlite
                (Query.from Order.table
                 |> Query.where (Order.OrderId >. 5)
                 |> Query.where (Order.CustomerId |=? ukCustomerIds)))
    )

    // A correlated EXISTS: the subquery refers to the outer table by name.
    results.Add(
        check
            "correlated EXISTS"
            "SELECT * FROM Customer WHERE EXISTS (SELECT * FROM Orders WHERE (Orders.CustomerId = Customer.CustomerId))"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (
                     Expr.exists (Query.from Order.table |> Query.where (Order.CustomerId == Customer.CustomerId))
                 )))
    )

    results.Add(
        check
            "NOT EXISTS"
            "SELECT * FROM Customer WHERE NOT (EXISTS (SELECT * FROM Orders WHERE (Orders.CustomerId = Customer.CustomerId)))"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (
                     Expr.notExists (Query.from Order.table |> Query.where (Order.CustomerId == Customer.CustomerId))
                 )))
    )

    // SQLProvider's `all`: there is no ALL in SQL, so it is asked the other way
    // round -- no row fails the condition.
    results.Add(
        check
            "all becomes NOT EXISTS over the rows that fail"
            "SELECT * FROM Customer WHERE NOT (EXISTS (SELECT * FROM Orders WHERE NOT ((Orders.Freight > @p0)))) | decimal"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Query.allSatisfy (Order.Freight >. 0m) (Query.from Order.table))))
    )

    // A subquery in value position.
    results.Add(
        check
            "a scalar subquery compares against an aggregate"
            "SELECT * FROM Customer WHERE (Customer.Balance > (SELECT AVG(Customer.Balance) AS avg FROM Customer))"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (
                     Expr.gt
                         Customer.Balance.E
                         (Expr.scalarQuery (Query.avgQuery Customer.Balance (Query.from Customer.table)))
                 )))
    )

    // An IN subquery has to project exactly one column, and says so rather than
    // handing the engine something it will reject less clearly.
    results.Add(
        check
            "an IN subquery selecting everything is rejected"
            "rejected"
            (try
                render
                    Sqlite
                    (Query.from Order.table
                     |> Query.where (Expr.inQuery Order.CustomerId.E (Query.from Customer.table)))
                |> ignore

                "rendered anyway"
             with _ ->
                 "rejected")
    )

    // --- writes -----------------------------------------------------------

    let renderStmt (vendor: Vendor) (st: Statement) =
        let sql, ps = SqlGen.renderStatement vendor st

        let values =
            ps
            |> Array.map (fun p ->
                match p.Value with
                | SqlInt i -> string i
                | SqlFloat f -> string f
                | SqlText t -> t
                | SqlNull -> "null"
                | other -> SqlValue.typeName other)
            |> String.concat ","

        if values = "" then sql else $"{sql} | {values}"

    results.Add(
        check
            "insert names its columns unqualified"
            "INSERT INTO Customer (CustomerId, Name) VALUES (@p0, @p1) | 1,Alfreds"
            (renderStmt
                Sqlite
                (InsertStmt(
                    Insert.into Customer.table
                    |> Insert.set Customer.CustomerId 1
                    |> Insert.set Customer.Name "Alfreds"
                )))
    )

    results.Add(
        check
            "insert maps None to NULL"
            "INSERT INTO Customer (Country) VALUES (@p0) | null"
            (renderStmt Sqlite (InsertStmt(Insert.into Customer.table |> Insert.setOpt Customer.Country None)))
    )

    results.Add(
        check
            "update sets and restricts by key"
            "UPDATE Customer SET Name = @p0 WHERE (CustomerId = @p1) | Bob,7"
            (renderStmt
                Sqlite
                (UpdateStmt(
                    Update.table Customer.table
                    |> Update.set Customer.Name "Bob"
                    |> Update.whereKey Customer.CustomerId 7
                )))
    )

    // An update can be written in terms of the row it updates, so a read and a
    // write collapse into one statement.
    results.Add(
        check
            "update can compute from the current row"
            "UPDATE Customer SET Balance = (Balance + @p0) WHERE (CustomerId = @p1) | 10,7"
            (renderStmt
                Sqlite
                (UpdateStmt(
                    Update.table Customer.table
                    |> Update.setExpr Customer.Balance (Expr.add Customer.Balance.E (Literal(SqlFloat 10.0)))
                    |> Update.whereKey Customer.CustomerId 7
                )))
    )

    results.Add(
        check
            "delete restricts by key"
            "DELETE FROM Customer WHERE (CustomerId = @p0) | 7"
            (renderStmt Sqlite (DeleteStmt(Delete.from Customer.table |> Delete.whereKey Customer.CustomerId 7)))
    )

    // A write with no WHERE hits every row. That is occasionally wanted and
    // never wanted by accident, so it has to be asked for by name.
    let rejected (render: unit -> string) =
        try
            render () |> ignore
            "rendered anyway"
        with _ ->
            "rejected"

    results.Add(
        check
            "an update with no WHERE is rejected"
            "rejected"
            (rejected (fun () ->
                renderStmt Sqlite (UpdateStmt(Update.table Customer.table |> Update.set Customer.Name "x"))))
    )

    results.Add(
        check
            "an update with no WHERE is allowed when asked for"
            "UPDATE Customer SET Name = @p0 | x"
            (renderStmt Sqlite (UpdateStmt(Update.table Customer.table |> Update.set Customer.Name "x" |> Update.all)))
    )

    results.Add(
        check
            "a delete with no WHERE is rejected"
            "rejected"
            (rejected (fun () -> renderStmt Sqlite (DeleteStmt(Delete.from Customer.table))))
    )

    results.Add(
        check
            "a delete with no WHERE is allowed when asked for"
            "DELETE FROM Customer"
            (renderStmt Sqlite (DeleteStmt(Delete.from Customer.table |> Delete.all)))
    )

    results.Add(
        check
            "an insert that sets nothing is rejected"
            "rejected"
            (rejected (fun () -> renderStmt Sqlite (InsertStmt(Insert.into Customer.table))))
    )

    results.Add(
        check
            "a batch keeps its order"
            "3"
            (string (
                Batch.empty
                |> Batch.insert (Insert.into Customer.table |> Insert.set Customer.Name "a")
                |> Batch.update (Update.table Customer.table |> Update.set Customer.Name "b" |> Update.all)
                |> Batch.delete (Delete.from Customer.table |> Delete.all)
                |> Batch.count
            ))
    )

    // --- multi-row insert -------------------------------------------------

    results.Add(
        check
            "many rows become one statement"
            "INSERT INTO Customer (CustomerId, Name) VALUES (@p0, @p1), (@p2, @p3) | 1,a,2,b"
            (let rows =
                [ Insert.into Customer.table
                  |> Insert.set Customer.CustomerId 1
                  |> Insert.set Customer.Name "a"
                  Insert.into Customer.table
                  |> Insert.set Customer.CustomerId 2
                  |> Insert.set Customer.Name "b" ]

             let sql, ps = SqlGen.renderInsertMany Sqlite (Insert.combine rows)

             let values =
                 ps
                 |> Array.map (fun p ->
                     match p.Value with
                     | SqlInt i -> string i
                     | SqlText t -> t
                     | other -> SqlValue.typeName other)
                 |> String.concat ","

             $"{sql} | {values}")
    )

    // Rows that disagree would otherwise take the previous row's value for a
    // missing column, so disagreement is an error.
    let combineRejected (rows: Insert list) =
        try
            Insert.combine rows |> ignore
            "combined anyway"
        with _ ->
            "rejected"

    results.Add(
        check
            "rows setting different columns are rejected"
            "rejected"
            (combineRejected
                [ Insert.into Customer.table |> Insert.set Customer.CustomerId 1
                  Insert.into Customer.table |> Insert.set Customer.Name "b" ])
    )

    results.Add(
        check
            "rows targeting different tables are rejected"
            "rejected"
            (combineRejected
                [ Insert.into Customer.table |> Insert.set Customer.CustomerId 1
                  Insert.into Order.table |> Insert.set Order.OrderId 1 ])
    )

    // --- generated keys ---------------------------------------------------

    // PostgreSQL says RETURNING in the statement; the others have no such
    // thing and need the follow-up query below.
    results.Add(
        check
            "PostgreSQL asks for the key in the statement"
            "INSERT INTO Customer (Name) VALUES (@p0) RETURNING CustomerId"
            (match
                SqlGen.renderInsertReturning
                    Postgres
                    Customer.CustomerId.Name
                    (Insert.into Customer.table |> Insert.set Customer.Name "a")
             with
             | Some(sql, _) -> sql
             | None -> "no RETURNING")
    )

    results.Add(
        isTrue
            "SQLite has no RETURNING clause to use"
            (SqlGen.renderInsertReturning
                Sqlite
                Customer.CustomerId.Name
                (Insert.into Customer.table |> Insert.set Customer.Name "a")
             |> Option.isNone)
    )

    results.Add(
        check
            "SQLite follows up per connection"
            "SELECT last_insert_rowid()"
            (SqlGen.lastInsertedKeyQuery Sqlite |> Option.defaultValue "none")
    )

    results.Add(
        check
            "MySQL follows up per connection"
            "SELECT LAST_INSERT_ID()"
            (SqlGen.lastInsertedKeyQuery MySql |> Option.defaultValue "none")
    )

    // --- the computation expression ---------------------------------------

    // The CE and the pipeline form are two spellings of one thing, so each
    // case asserts they build the identical query rather than re-asserting
    // the SQL: if they ever diverge, that is the bug.
    let sameAsPipeline name (ce: Query) (piped: Query) =
        check name (render Sqlite piped) (render Sqlite ce)

    results.Add(
        sameAsPipeline
            "CE: from and where"
            (sqlQuery {
                from Customer.table
                where (Customer.Country == "UK")
            })
            (Query.from Customer.table |> Query.where (Customer.Country == "UK"))
    )

    // A CE case written the way the schema is meant to be used: the tuple
    // overloads of `select` take typed columns, which is the whole point of
    // having generated them.
    results.Add(
        sameAsPipeline
            "CE: select a column by name"
            (sqlQuery {
                from Customer.table
                where (Customer.Country == "UK")
                select Customer.Name
            })
            (Query.from Customer.table
             |> Query.where (Customer.Country == "UK")
             |> Query.selectCol Customer.Name)
    )

    results.Add(
        sameAsPipeline
            "CE: select several columns by name"
            (sqlQuery {
                from Customer.table
                select (Customer.CustomerId, Customer.Name, Customer.Country)
            })
            (Query.from Customer.table
             |> Query.selectCol Customer.CustomerId
             |> Query.selectCol Customer.Name
             |> Query.selectCol Customer.Country)
    )

    results.Add(
        sameAsPipeline
            "CE: groupBy a column by name"
            (sqlQuery {
                from Customer.table
                groupBy Customer.Country
                selectCol Customer.Country
            })
            (Query.from Customer.table
             |> Query.groupByCol Customer.Country
             |> Query.selectCol Customer.Country)
    )

    results.Add(
        sameAsPipeline
            "CE: successive wheres still AND"
            (sqlQuery {
                from Customer.table
                where (Customer.Country == "UK")
                where (Customer.Balance >. 100.0)
            })
            (Query.from Customer.table
             |> Query.where (Customer.Country == "UK")
             |> Query.where (Customer.Balance >. 100.0))
    )

    results.Add(
        sameAsPipeline
            "CE: sorting, paging and projection"
            (sqlQuery {
                from Customer.table
                where (Customer.Balance >. 0.0)
                sortByDescending Customer.Balance
                thenBy Customer.Name
                skip 5
                take 10
                select Customer.Name
            })
            (Query.from Customer.table
             |> Query.where (Customer.Balance >. 0.0)
             |> Query.orderByDesc Customer.Balance
             |> Query.thenBy Customer.Name
             |> Query.skip 5
             |> Query.take 10
             |> Query.select [| Customer.Name.E |])
    )

    results.Add(
        sameAsPipeline
            "CE: join"
            (sqlQuery {
                from Customer.table
                join Order.table (Expr.eq Customer.CustomerId.E (Order.CustomerId.E))
                where (Customer.Country == "UK")
            })
            (Query.from Customer.table
             |> Query.join Order.table (Expr.eq Customer.CustomerId.E (Order.CustomerId.E))
             |> Query.where (Customer.Country == "UK"))
    )

    results.Add(
        sameAsPipeline
            "CE: distinct, groupBy and having"
            (sqlQuery {
                from Customer.table
                groupByCol Customer.Country
                having (Expr.gt Expr.count (Literal(SqlInt 2L)))
                selectCol Customer.Country
                selectExpr "n" Expr.count
                distinct
            })
            (Query.from Customer.table
             |> Query.groupByCol Customer.Country
             |> Query.having (Expr.gt Expr.count (Literal(SqlInt 2L)))
             |> Query.selectCol Customer.Country
             |> Query.selectExpr "n" Expr.count
             |> Query.distinct)
    )

    results.Add(
        sameAsPipeline
            "CE: whereAny"
            (sqlQuery {
                from Customer.table
                whereAny [ Customer.Country == "UK"; Customer.Country == "USA" ]
            })
            (Query.from Customer.table
             |> Query.whereAny [ Customer.Country == "UK"; Customer.Country == "USA" ])
    )

    // A block with no `from` has no table to select from, and says so instead
    // of rendering `FROM ` with nothing after it.
    let sourceless =
        try
            Db.toSql Sqlite Query.blank |> ignore
            "rendered anyway"
        with _ ->
            "rejected"

    results.Add(check "a query with no source is rejected" "rejected" sourceless)

    results.Add(
        sameAsPipeline
            "CE: a subquery in where"
            (sqlQuery {
                from Order.table
                where (Expr.inQuery Order.CustomerId.E ukCustomerIds)
            })
            (Query.from Order.table
             |> Query.where (Expr.inQuery Order.CustomerId.E ukCustomerIds))
    )

    results.Add(
        sameAsPipeline
            "CE: a correlated EXISTS in where"
            (sqlQuery {
                from Customer.table

                where (
                    Expr.exists (
                        Query.from Order.table
                        |> Query.where (Expr.eq Order.CustomerId.E (Customer.CustomerId.E))
                    )
                )
            })
            (Query.from Customer.table
             |> Query.where (
                 Expr.exists (
                     Query.from Order.table
                     |> Query.where (Expr.eq Order.CustomerId.E (Customer.CustomerId.E))
                 )
             ))
    )

    // The write CEs, asserted the same way: they must build the identical
    // statement as the pipeline form.
    results.Add(
        check
            "CE: insert"
            (renderStmt
                Sqlite
                (InsertStmt(
                    Insert.into Customer.table
                    |> Insert.set Customer.Name "a"
                    |> Insert.setOpt Customer.Country None
                )))
            (renderStmt
                Sqlite
                (InsertStmt(
                    sqlInsert {
                        into Customer.table
                        set Customer.Name "a"
                        setOpt Customer.Country None
                    }
                )))
    )

    results.Add(
        check
            "CE: update"
            (renderStmt
                Sqlite
                (UpdateStmt(
                    Update.table Customer.table
                    |> Update.set Customer.Name "b"
                    |> Update.whereKey Customer.CustomerId 7
                )))
            (renderStmt
                Sqlite
                (UpdateStmt(
                    sqlUpdate {
                        table Customer.table
                        set Customer.Name "b"
                        whereKey Customer.CustomerId 7
                    }
                )))
    )

    results.Add(
        check
            "CE: delete"
            (renderStmt Sqlite (DeleteStmt(Delete.from Customer.table |> Delete.whereKey Customer.CustomerId 7)))
            (renderStmt
                Sqlite
                (DeleteStmt(
                    sqlDelete {
                        from Customer.table
                        whereKey Customer.CustomerId 7
                    }
                )))
    )

    // --- conditional and combined aggregates ------------------------------
    //
    // SQLProvider's `g.Sum(fun r -> if cond then 1 else 0)` and
    // `groupBy 1 into g; select (g.Sum .., g.Count())` shapes, spelled with
    // what already exists: an aggregate over a CASE, and several aggregates in
    // one projection.

    results.Add(
        check
            "SUM over CASE WHEN counts matches"
            "SELECT SUM(CASE WHEN (Customer.Country IS NULL) THEN @p0 ELSE @p1 END) AS anonymous FROM Customer | 1,0"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "anonymous",
                        Expr.sum (
                            Expr.ifThenElse (Expr.isNull Customer.Country.E) (Literal(SqlInt 1L)) (Literal(SqlInt 0L))
                        ) |]))
    )

    results.Add(
        check
            "several aggregates in one row"
            "SELECT SUM(Customer.Balance) AS total, COUNT(*) AS n, MAX(Customer.Balance) AS biggest FROM Customer"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "total", Expr.sum Customer.Balance.E
                        "n", Expr.count
                        "biggest", Expr.max Customer.Balance.E |]))
    )

    // --- numeric functions, casts and date differences --------------------

    results.Add(
        check
            "ceiling, SQLite spelled with CAST arithmetic"
            "SELECT (CAST(Customer.Balance AS INTEGER) + (Customer.Balance > CAST(Customer.Balance AS INTEGER))) AS c FROM Customer"
            (render Sqlite (Query.from Customer.table |> Query.selectAs [| "c", Expr.ceiling Customer.Balance.E |]))
    )

    results.Add(
        check
            "ceiling, PostgreSQL"
            "SELECT CEILING(Customer.Balance) AS c FROM Customer"
            (render Postgres (Query.from Customer.table |> Query.selectAs [| "c", Expr.ceiling Customer.Balance.E |]))
    )

    results.Add(
        check
            "floor, SQLite spelled with CAST arithmetic"
            "SELECT (CAST(Customer.Balance AS INTEGER) - (Customer.Balance < CAST(Customer.Balance AS INTEGER))) AS f FROM Customer"
            (render Sqlite (Query.from Customer.table |> Query.selectAs [| "f", Expr.floor Customer.Balance.E |]))
    )

    results.Add(
        check
            "round, and round to decimals -- PostgreSQL needs the numeric cast"
            "SELECT ROUND(Customer.Balance) AS r, CAST(ROUND(CAST(Customer.Balance AS numeric), 2) AS double precision) AS r2 FROM Customer"
            (render
                Postgres
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "r", Expr.round Customer.Balance.E
                        "r2", Expr.roundTo 2 Customer.Balance.E |]))
    )

    results.Add(
        check
            "truncate per vendor"
            ("SELECT CAST(Customer.Balance AS INTEGER) AS t FROM Customer"
             + " / "
             + "SELECT TRUNCATE(Customer.Balance, 0) AS t FROM Customer")
            ((render Sqlite (Query.from Customer.table |> Query.selectAs [| "t", Expr.truncate Customer.Balance.E |]))
             + " / "
             + (render MySql (Query.from Customer.table |> Query.selectAs [| "t", Expr.truncate Customer.Balance.E |])))
    )

    results.Add(
        check
            "greatest and least, SQLite's two-argument MAX and MIN"
            "SELECT MAX(Customer.Balance, @p0) AS g, MIN(Customer.Balance, @p1) AS l FROM Customer | 100,100"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "g", Expr.greatest Customer.Balance.E (Literal(SqlFloat 100.0))
                        "l", Expr.least Customer.Balance.E (Literal(SqlFloat 100.0)) |]))
    )

    results.Add(
        check
            "greatest, PostgreSQL"
            "SELECT GREATEST(Customer.Balance, @p0) AS g FROM Customer | 100"
            (render
                Postgres
                (Query.from Customer.table
                 |> Query.selectAs [| "g", Expr.greatest Customer.Balance.E (Literal(SqlFloat 100.0)) |]))
    )

    results.Add(
        check
            "casts per vendor"
            ("SELECT CAST(Customer.Balance AS TEXT) AS t, CAST(Customer.Name AS INTEGER) AS i FROM Customer"
             + " / "
             + "SELECT CAST(Customer.Balance AS CHAR) AS t, CAST(Customer.Name AS SIGNED) AS i FROM Customer")
            ((render
                Sqlite
                (Query.from Customer.table
                 |> Query.selectAs
                     [| "t", Expr.castText Customer.Balance.E
                        "i", Expr.castInt Customer.Name.E |]))
             + " / "
             + (render
                 MySql
                 (Query.from Customer.table
                  |> Query.selectAs
                      [| "t", Expr.castText Customer.Balance.E
                         "i", Expr.castInt Customer.Name.E |])))
    )

    let joinedDiff = Expr.dateDiffDays Customer.Joined.E (Literal(SqlText "2026-01-01"))

    results.Add(
        check
            "dateDiffDays, SQLite counts date boundaries via JULIANDAY(DATE(..))"
            "SELECT CAST(JULIANDAY(DATE(Customer.Joined)) - JULIANDAY(DATE(@p0)) AS INTEGER) AS d FROM Customer | 2026-01-01"
            (render Sqlite (Query.from Customer.table |> Query.selectAs [| "d", joinedDiff |]))
    )

    results.Add(
        check
            "dateDiffDays, PostgreSQL and MySQL"
            ("SELECT (CAST(Customer.Joined AS date) - CAST(@p0 AS date)) AS d FROM Customer | 2026-01-01"
             + " / "
             + "SELECT DATEDIFF(Customer.Joined, @p0) AS d FROM Customer | 2026-01-01")
            ((render Postgres (Query.from Customer.table |> Query.selectAs [| "d", joinedDiff |]))
             + " / "
             + (render MySql (Query.from Customer.table |> Query.selectAs [| "d", joinedDiff |])))
    )

    results.Add(
        check
            "dateDiffSecs swaps its arguments for MySQL's TIMESTAMPDIFF"
            "SELECT TIMESTAMPDIFF(SECOND, @p0, Customer.Joined) AS s FROM Customer | 2026-01-01"
            (render
                MySql
                (Query.from Customer.table
                 |> Query.selectAs [| "s", Expr.dateDiffSecs Customer.Joined.E (Literal(SqlText "2026-01-01")) |]))
    )

    // --- COUNT(DISTINCT) --------------------------------------------------

    results.Add(
        check
            "countDistinct renders inside the parens"
            "SELECT COUNT(DISTINCT Customer.Country) AS n FROM Customer"
            (render Sqlite (Query.from Customer.table |> Query.selectAs [| "n", Expr.countDistinct Customer.Country.E |]))
    )

    results.Add(
        check
            "counting a one-column DISTINCT query becomes COUNT(DISTINCT x)"
            "SELECT COUNT(DISTINCT Customer.Country) AS count FROM Customer"
            (render
                Sqlite
                (Query.countQuery (Query.from Customer.table |> Query.distinct |> Query.selectCol Customer.Country)))
    )

    results.Add(
        (let name = "counting a DISTINCT query without one column is refused"

         try
             Query.countQuery (Query.from Customer.table |> Query.distinct) |> ignore

             fail name "expected countQuery to refuse DISTINCT over SELECT *"
         with _ ->
             pass name)
    )

    // --- set operations: UNION, UNION ALL, INTERSECT, EXCEPT --------------

    let ukNames =
        Query.from Customer.table
        |> Query.where (Customer.Country == "UK")
        |> Query.selectCol Customer.Name

    let usNames =
        Query.from Customer.table
        |> Query.where (Customer.Country == "USA")
        |> Query.selectCol Customer.Name

    results.Add(
        check
            "union deduplicates; unionAll keeps everything"
            ("SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0)"
             + " UNION SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1) | UK,USA"
             + " / "
             + "SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0)"
             + " UNION ALL SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1) | UK,USA")
            ((render Sqlite (ukNames |> Query.union usNames))
             + " / "
             + (render Sqlite (ukNames |> Query.unionAll usNames)))
    )

    results.Add(
        check
            "intersect and except"
            ("SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0)"
             + " INTERSECT SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1) | UK,USA"
             + " / "
             + "SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0)"
             + " EXCEPT SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1) | UK,USA")
            ((render Sqlite (ukNames |> Query.intersect usNames))
             + " / "
             + (render Sqlite (ukNames |> Query.except usNames)))
    )

    results.Add(
        check
            "ordering and paging on a compound apply to the whole result, unqualified"
            "SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0) UNION SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1) ORDER BY Name ASC LIMIT 5 | UK,USA"
            (render
                Sqlite
                (ukNames
                 |> Query.union usNames
                 |> Query.orderBy Customer.Name
                 |> Query.take 5))
    )

    results.Add(
        (let name = "a union arm with its own ordering is refused"

         try
             render Sqlite (ukNames |> Query.union (usNames |> Query.orderBy Customer.Name))
             |> ignore

             fail name "expected the arm's ORDER BY to be refused"
         with _ ->
             pass name)
    )

    results.Add(
        check
            "a union can sit inside IN, sharing the parameter list"
            "SELECT * FROM Customer WHERE Customer.Name IN (SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p0) UNION SELECT Customer.Name FROM Customer WHERE (Customer.Country = @p1)) | UK,USA"
            (render
                Sqlite
                (Query.from Customer.table
                 |> Query.where (Customer.Name |=? (ukNames |> Query.union usNames))))
    )

    results.Add(
        check
            "CE: union"
            (render Sqlite (ukNames |> Query.unionAll usNames |> Query.take 3))
            (render
                Sqlite
                (sqlQuery {
                    from Customer.table
                    where (Customer.Country == "UK")
                    selectCol Customer.Name
                    unionAll usNames
                    take 3
                }))
    )

    results.ToArray()

/// Uses the generated schema, so the generator's output is exercised rather
/// than only inspected: this file is compiled by every target the library
/// supports, and these assertions run there.
let runGenerated () : TestResult[] =
    let results = ResizeArray<TestResult>()

    results.Add(check "generated: the table handle" "Customer" GeneratedSchema.Customer.table)

    results.Add(
        check
            "generated: columns build a query"
            "SELECT * FROM Customer WHERE (Customer.Country = @p0) | UK"
            (render
                Sqlite
                (Query.from GeneratedSchema.Customer.table
                 |> Query.where (GeneratedSchema.Customer.Country == "UK")))
    )

    // A nullable column came through as an option, which is what decides
    // whether the mapper reads with the Opt variant.
    results.Add(
        check
            "generated: a decimal column keeps its type"
            "SELECT * FROM Orders WHERE (Orders.Freight > @p0) | decimal"
            (render
                Sqlite
                (Query.from GeneratedSchema.Orders.table
                 |> Query.where (GeneratedSchema.Orders.Freight >. 1m)))
    )

    // A foreign key became a ready-made join condition.
    results.Add(
        check
            "generated: a foreign key is a join condition"
            "SELECT * FROM Orders INNER JOIN Customer ON (Orders.CustomerId = Customer.CustomerId)"
            (render
                Sqlite
                (Query.from GeneratedSchema.Orders.table
                 |> Query.join GeneratedSchema.Customer.table GeneratedSchema.Orders.Relations.toCustomer))
    )

    // The mapper reads a row into the generated record.
    let rs =
        { Columns =
            [| "CustomerId"
               "Name"
               "Country"
               "Balance"
               "Joined"
               "Discount"
               "Active"
               "Photo" |]
          Rows =
            [| [| SqlInt 1L
                  SqlText "Alfreds"
                  SqlNull
                  SqlFloat 10.5
                  SqlNull
                  SqlNull
                  SqlBool true
                  SqlNull |] |] }

    match ResultSet.tryHead rs with
    | None -> results.Add(fail "generated: ofRow" "no row")
    | Some row ->
        let c = GeneratedSchema.Customer.ofRow row
        results.Add(check "generated: ofRow reads a value" "Alfreds" c.Name)
        results.Add(isTrue "generated: ofRow reads NULL as None" c.Country.IsNone)
        results.Add(isTrue "generated: ofRow reads a bool" c.Active)

    results.ToArray()
