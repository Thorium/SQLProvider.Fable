/// The schema reader and the code generator.
///
/// Design-time only, so these run on .NET and nowhere else. They read a real
/// schema out of a real database rather than a fixture: the point of the reader
/// is what an engine actually reports, and a hand-written fixture would only
/// test my idea of that.
module SQLProvider.Fable.Tests.Net.CodeGenTests

open Xunit
open Microsoft.Data.Sqlite
open SQLProvider.Fable
open SQLProvider.Fable.Ado
open SQLProvider.Fable.Design

let private northwindish =
    [ "CREATE TABLE Customer (
        CustomerId INTEGER NOT NULL PRIMARY KEY,
        Name       TEXT    NOT NULL,
        Country    TEXT    NULL,
        Balance    REAL    NOT NULL,
        Joined     DATETIME NULL,
        Discount   DECIMAL(18,4) NULL,
        Active     BOOLEAN NOT NULL,
        Photo      BLOB    NULL
      )"
      "CREATE TABLE Orders (
        OrderId    INTEGER NOT NULL PRIMARY KEY,
        CustomerId INTEGER NOT NULL,
        Freight    DECIMAL(18,4) NOT NULL,
        FOREIGN KEY (CustomerId) REFERENCES Customer(CustomerId)
      )" ]

let private withTables (ddls: string list) (f: ISqlConnector -> Async<unit>) =
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    use connector = new AdoConnector(connection, vendor = Sqlite)
    let c = connector :> ISqlConnector

    async {
        for ddl in ddls do
            let! _ = c.Execute(ddl, Sql.noParams)
            ()

        do! f c
    }
    |> Async.RunSynchronously

let private withSchema (f: ISqlConnector -> Async<unit>) = withTables northwindish f

[<Fact>]
let ``the reader finds the tables, their columns and their kinds`` () =
    withSchema (fun c ->
        async {
            let! db = SchemaReader.read c

            let names = db.Tables |> Array.map (fun t -> t.Name) |> Array.sort
            Assert.Equal<string[]>([| "Customer"; "Orders" |], names)

            let customer = db.Tables |> Array.find (fun t -> t.Name = "Customer")

            let kinds =
                customer.Columns
                |> Array.map (fun col -> col.Name + ":" + string col.Kind)
                |> String.concat ","

            Assert.Equal(
                "CustomerId:KInt,Name:KText,Country:KText,Balance:KFloat,Joined:KDate,Discount:KDecimal,Active:KBool,Photo:KBlob",
                kinds
            )
        })

[<Fact>]
let ``the reader reports nullability and the primary key`` () =
    withSchema (fun c ->
        async {
            let! db = SchemaReader.read c
            let customer = db.Tables |> Array.find (fun t -> t.Name = "Customer")

            let col name =
                customer.Columns |> Array.find (fun x -> x.Name = name)

            Assert.True((col "CustomerId").IsPrimaryKey, "CustomerId should be the key")
            Assert.False((col "Name").IsPrimaryKey, "Name should not be the key")
            Assert.False((col "Name").IsNullable, "Name is NOT NULL")
            Assert.True((col "Country").IsNullable, "Country is nullable")
        })

[<Fact>]
let ``the reader follows foreign keys`` () =
    withSchema (fun c ->
        async {
            let! db = SchemaReader.read c
            let orders = db.Tables |> Array.find (fun t -> t.Name = "Orders")

            Assert.Equal(1, orders.ForeignKeys.Length)
            let fk = orders.ForeignKeys.[0]
            Assert.Equal("Customer", fk.ReferencesTable)
            Assert.Equal<(string * string)[]>([| "CustomerId", "CustomerId" |], fk.Columns)
        })

[<Fact>]
let ``the generated code says what it should`` () =
    withSchema (fun c ->
        async {
            let! db = SchemaReader.read c
            let source = CodeGen.emit "Northwind" db

            // The table handle and one column of each interesting kind.
            Assert.Contains("module Customer =", source)
            Assert.Contains("let table = \"Customer\"", source)
            Assert.Contains("let CustomerId = Col.int64 table \"CustomerId\"", source)
            Assert.Contains("let Balance = Col.float table \"Balance\"", source)
            Assert.Contains("let Discount = Col.decimal table \"Discount\"", source)
            Assert.Contains("let Joined = Col.date table \"Joined\"", source)
            Assert.Contains("let Photo = Col.blob table \"Photo\"", source)

            // A nullable column becomes an option, and reads through the Opt
            // reader; a NOT NULL one does neither.
            Assert.Contains("Country: string option", source)
            Assert.Contains("Name: string", source)
            Assert.Contains("Country = Row.textOpt r \"Country\"", source)
            Assert.Contains("Name = Row.text r \"Name\"", source)

            // A foreign key becomes a ready-made join condition.
            Assert.Contains("module Relations =", source)

            Assert.Contains(
                "let toCustomer = Expr.eq (ColumnRef(table, \"CustomerId\")) (ColumnRef(\"Customer\", \"CustomerId\"))",
                source
            )
        })

[<Fact>]
let ``two keys to the same table get distinct relation names`` () =
    // `toAddress` twice would be a duplicate definition, so the generated
    // module would not compile at all -- the names carry their own columns.
    withTables
        [ "CREATE TABLE Address (Id INTEGER NOT NULL PRIMARY KEY, Street TEXT NOT NULL)"
          "CREATE TABLE Shipment (
             ShipmentId    INTEGER NOT NULL PRIMARY KEY,
             FromAddressId INTEGER NOT NULL,
             ToAddressId   INTEGER NOT NULL,
             FOREIGN KEY (FromAddressId) REFERENCES Address(Id),
             FOREIGN KEY (ToAddressId)   REFERENCES Address(Id)
           )" ]
        (fun c ->
            async {
                let! db = SchemaReader.read c
                let source = CodeGen.emit "Probe" db

                Assert.Contains("let toAddressViaFromAddressId = ", source)
                Assert.Contains("let toAddressViaToAddressId = ", source)
                Assert.DoesNotContain("let toAddress = ", source)
            })

[<Fact>]
let ``a composite key becomes one relation over every column pair`` () =
    // One row per column comes back from the engine; pairing them off as
    // separate keys would generate two half-key conditions under one name.
    withTables
        [ "CREATE TABLE Region (A INTEGER NOT NULL, B INTEGER NOT NULL, PRIMARY KEY (A, B))"
          "CREATE TABLE City (
             CityId  INTEGER NOT NULL PRIMARY KEY,
             RegionA INTEGER NOT NULL,
             RegionB INTEGER NOT NULL,
             FOREIGN KEY (RegionA, RegionB) REFERENCES Region(A, B)
           )" ]
        (fun c ->
            async {
                let! db = SchemaReader.read c
                let city = db.Tables |> Array.find (fun t -> t.Name = "City")

                Assert.Equal(1, city.ForeignKeys.Length)

                Assert.Equal<(string * string)[]>(
                    [| "RegionA", "A"; "RegionB", "B" |],
                    city.ForeignKeys.[0].Columns
                )

                let source = CodeGen.emit "Probe" db

                Assert.Contains(
                    "let toRegion = Expr.andAlso (Expr.eq (ColumnRef(table, \"RegionA\")) (ColumnRef(\"Region\", \"A\"))) (Expr.eq (ColumnRef(table, \"RegionB\")) (ColumnRef(\"Region\", \"B\")))",
                    source
                )
            })

[<Fact>]
let ``an awkward column name is escaped rather than renamed`` () =
    // The generated identifier still has to match the database, so a name that
    // collides with an F# keyword gets backticks instead of a new spelling.
    Assert.Equal("``type``", CodeGen.identifier "type")
    Assert.Equal("``Order Date``", CodeGen.identifier "Order Date")
    Assert.Equal("CustomerId", CodeGen.identifier "CustomerId")

// --- the golden file ------------------------------------------------------

/// Walks up from the test assembly to the repo root, so the golden file can be
/// found without hard-coding a path.
let private repoRoot () =
    let mutable dir = System.IO.DirectoryInfo(System.AppContext.BaseDirectory)

    while (not (isNull dir))
          && not (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SQLProvider.Fable.slnx"))) do
        dir <- dir.Parent

    if isNull dir then
        failwith "could not find the repo root from the test assembly"
    else
        dir.FullName

let private goldenPath () =
    System.IO.Path.Combine(repoRoot (), "tests", "SQLProvider.Fable.SmokeTests", "GeneratedSchema.fs")

/// Generates from the fixture and compares against the file checked in at
/// `tests/SQLProvider.Fable.SmokeTests/GeneratedSchema.fs`.
///
/// That file is compiled by the shared test project, which is the real
/// assertion: the generator's output has to be valid F# against the actual
/// runtime API, on every target the library supports. Comparing the text here
/// is what keeps the checked-in copy honest.
///
/// Set SQLPROVIDER_FABLE_REGENERATE=1 to rewrite it after a deliberate change.
[<Fact>]
let ``the generated file is up to date`` () =
    withSchema (fun c ->
        async {
            let! db = SchemaReader.read c

            let source =
                (CodeGen.emit "SQLProvider.Fable.SmokeTests.GeneratedSchema" db).Replace("\r\n", "\n")

            let path = goldenPath ()

            if System.Environment.GetEnvironmentVariable "SQLPROVIDER_FABLE_REGENERATE" = "1" then
                System.IO.File.WriteAllText(path, source)

            let existing =
                if System.IO.File.Exists path then
                    System.IO.File.ReadAllText(path).Replace("\r\n", "\n")
                else
                    ""

            Assert.Equal(existing, source)
        })
