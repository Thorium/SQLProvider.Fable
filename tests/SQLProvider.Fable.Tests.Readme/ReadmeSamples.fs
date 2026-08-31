/// Every code sample in README.md, compiled.
///
/// Documentation rots quietly: a renamed function leaves the README wrong and
/// nothing notices. This project is the check -- it is a library, and the
/// assertion is that it builds. When a sample here has to change, the README
/// has to change with it.
///
/// The schema modules below stand in for generated code, so this needs no
/// database and no generator run.
module SQLProvider.Fable.Tests.Readme.ReadmeSamples

open SQLProvider.Fable
open SQLProvider.Fable.Ado
open Microsoft.Data.Sqlite

// Stand-in for the generated schema.
module Customer =
    let table = "Customer"
    let CustomerId = Col.int64 table "CustomerId"
    let Name = Col.text table "Name"
    let Country = Col.text table "Country"
    let Balance = Col.float table "Balance"

    type Row =
        { CustomerId: int64
          Name: string
          Country: string option }

    let ofRow (r: SqlRow) : Row =
        { CustomerId = Row.int64 r "CustomerId"
          Name = Row.text r "Name"
          Country = Row.textOpt r "Country" }

    let qualified =
        [| "Customer_CustomerId", CustomerId.E
           "Customer_Name", Name.E
           "Customer_Country", Country.E |]

    let ofQualifiedRow (r: SqlRow) : Row =
        { CustomerId = Row.int64 r "Customer_CustomerId"
          Name = Row.text r "Customer_Name"
          Country = Row.textOpt r "Customer_Country" }

module Order =
    let table = "Orders"
    let OrderId = Col.int64 table "OrderId"
    let CustomerId = Col.int64 table "CustomerId"

    type Row = { OrderId: int64 }

    let qualified = [| "Orders_OrderId", OrderId.E |]

    let ofQualifiedRow (r: SqlRow) : Row =
        { OrderId = Row.int64 r "Orders_OrderId" }

module Orders =
    let table = "Orders"

    module Relations =
        let toCustomer =
            Expr.eq (ColumnRef(table, "CustomerId")) (ColumnRef("Customer", "CustomerId"))

let ukCustomers (conn: ISqlConnector) =
    async {
        let! rows =
            sqlQuery {
                from Customer.table
                where (Customer.Country == "UK")
                where (Customer.Balance >. 100.0)
                sortByDescending Customer.Balance
                select (Customer.Name, Customer.Country)
                take 10
            }
            |> Db.query conn

        // Read what was selected: `ofRow` is for the unprojected row -- it
        // reads every column, and this select kept only two.
        return rows |> ResultSet.map (fun r -> Row.text r "Name", Row.textOpt r "Country")
    }

let connecting () =
    use connection = new SqliteConnection("Data Source=app.db")
    use connector = new AdoConnector(connection, vendor = Sqlite)
    connector :> ISqlConnector

let pipeline =
    Query.from Customer.table
    |> Query.where (Customer.Country == "UK")
    |> Query.orderByDesc Customer.Balance
    |> Query.selectCol Customer.Name

let joins =
    Query.from Orders.table
    |> Query.join Customer.table Orders.Relations.toCustomer
    |> Query.join Order.table (Customer.CustomerId == Order.CustomerId)

let combining =
    Query.from Customer.table
    |> Query.where ((Customer.Balance >. 100.0) .||. (Customer.Balance <. 10.0))
    |> Query.whereAll [ Customer.Country == "UK"; Customer.Balance >. 100.0 ]

let selfJoin =
    Query.fromAs Customer.table "a"
    |> Query.joinAs Customer.table "b" ((Col.onAlias "a" Customer.Country) == (Col.onAlias "b" Customer.Country))

let functions searchBox high low =
    Query.from Customer.table
    |> Query.where (Expr.contains Customer.Name.E searchBox)
    |> Query.selectExpr "band" (Expr.ifThenElse (Customer.Balance >. 100.0) high low)

let ukCustomerIds =
    Query.from Customer.table
    |> Query.where (Customer.Country == "UK")
    |> Query.selectCol Customer.CustomerId

let subquery =
    Query.from Order.table |> Query.where (Order.CustomerId |=? ukCustomerIds)

let running (conn: ISqlConnector) q =
    async {
        let! rows = Db.query conn q
        let! firstRow = Db.tryHead conn q
        let! n = Db.count conn q
        let! any = Db.exists conn q
        let! total = Db.sum conn Customer.Balance q
        let! _ = Db.avg conn Customer.Balance q
        let! _ = Db.min conn Customer.Balance q
        let! _ = Db.max conn Customer.Balance q
        let sql, ps = Db.toSql Sqlite q
        let customers = rows |> ResultSet.map Customer.ofRow
        return (firstRow, n, any, total, sql, ps, customers)
    }

let reading (row: SqlRow) =
    let name = Row.text row "Name"
    let country = Row.textOpt row "Country"
    name, country

let writing (conn: ISqlConnector) =
    async {
        do!
            Db.insert
                conn
                (Insert.into Customer.table
                 |> Insert.set Customer.CustomerId 200L
                 |> Insert.set Customer.Name "Alfreds"
                 |> Insert.setOpt Customer.Country (Some "Spain"))
            |> Async.Ignore

        do!
            Db.update
                conn
                (Update.table Customer.table
                 |> Update.set Customer.Name "New name"
                 |> Update.setExpr Customer.Balance (Expr.add Customer.Balance.E (Literal(SqlFloat 10.0)))
                 |> Update.whereKey Customer.CustomerId 200L)
            |> Async.Ignore

        do!
            Db.delete conn (Delete.from Customer.table |> Delete.whereKey Customer.CustomerId 200L)
            |> Async.Ignore

        do!
            Db.insertMany conn [ for x in [ "a"; "b" ] -> Insert.into Customer.table |> Insert.set Customer.Name x ]
            |> Async.Ignore

        let! _key =
            Db.insertReturning conn Customer.CustomerId (Insert.into Customer.table |> Insert.set Customer.Name "k")

        let! _affected =
            Batch.empty
            |> Batch.insert (Insert.into Customer.table |> Insert.set Customer.Name "one")
            |> Batch.update (
                Update.table Customer.table
                |> Update.set Customer.Name "two"
                |> Update.whereKey Customer.CustomerId 1L
            )
            |> Db.submit conn

        do! Db.inTransaction conn (async { return () })

        // The deliberate unconditional forms.
        let _ = Update.table Customer.table |> Update.set Customer.Name "x" |> Update.all
        let _ = Delete.from Customer.table |> Delete.all

        // The write computation expressions.
        let _ =
            sqlInsert {
                into Customer.table
                set Customer.Name "a"
            }

        let _ =
            sqlUpdate {
                table Customer.table
                set Customer.Name "b"
                whereKey Customer.CustomerId 1L
            }

        let _ =
            sqlDelete {
                from Customer.table
                whereKey Customer.CustomerId 1L
            }

        return ()
    }

// Conditional aggregates, and several aggregates sharing one query.
let conditionalAggregates =
    Query.from Customer.table
    |> Query.selectAs
        [| "total", Expr.sum Customer.Balance.E
           "n", Expr.count
           "unnamed",
           Expr.sum (Expr.ifThenElse (Expr.isNull Customer.Country.E) (Literal(SqlInt 1L)) (Literal(SqlInt 0L))) |]

// Set operations.
let private germanCustomers =
    Query.from Customer.table
    |> Query.where (Customer.Country == "Germany")
    |> Query.selectCol Customer.Name

let private richCustomers =
    Query.from Customer.table
    |> Query.where (Customer.Balance >. 100.0)
    |> Query.selectCol Customer.Name

let setOperations =
    germanCustomers
    |> Query.unionAll richCustomers
    |> Query.orderBy Customer.Name
    |> Query.take 10

// Both sides of a join, through the generated qualified aliases.
let joinBothSides (conn: ISqlConnector) =
    async {
        let q =
            Query.from Order.table
            |> Query.join Customer.table (Order.CustomerId == Customer.CustomerId)
            |> Query.selectAs (Array.append Order.qualified Customer.qualified)

        let! rows = Db.query conn q

        return
            rows
            |> ResultSet.map (fun r -> Order.ofQualifiedRow r, Customer.ofQualifiedRow r)
    }

// The row-or-error enders.
let enders (conn: ISqlConnector) (q: Query) =
    async {
        let! _ = Db.head conn q
        let! _ = Db.exactlyOne conn q
        let! _ = Db.tryExactlyOne conn q
        return ()
    }

/// Referenced so nothing above is dead code.
let all =
    box (
        pipeline,
        joins,
        combining,
        selfJoin,
        subquery,
        ukCustomers,
        connecting,
        functions,
        running,
        reading,
        writing,
        (conditionalAggregates, setOperations, joinBothSides, enders)
    )
