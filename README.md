# SQLProvider.Fable

Typed SQL for F#, from one source, on .NET **and** through Fable to **Rust**,
**JavaScript NodeJs** and **Erlang/BEAM**.

NOTE: Rust needs a Fable newer than 5.15 — the fixes are merged on Fable's main branch, awaiting release.

Point the generator at your database, get a module per table with typed columns
and row mappers, and write queries against them:

```fsharp
open SQLProvider.Fable
open Northwind

let ukCustomers conn =
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

        return rows |> ResultSet.map (fun r -> Row.text r "Name", Row.textOpt r "Country")
    }
```

The same code compiles and runs on every target. The SQL it produces is adjusted
per engine — paging, string and date functions, placeholder syntax — and every
value you supply is a bound parameter, never concatenated into the statement.

---

## Contents

- [Status](#status)
- [Getting started](#getting-started)
- [Generating from your schema](#generating-from-your-schema)
- [Connecting](#connecting)
- [Querying](#querying)
- [Reading rows](#reading-rows)
- [Writing](#writing)
- [Gotchas worth knowing up front](#gotchas-worth-knowing-up-front)
- [What is not supported](#what-is-not-supported)
- [Running the tests](#running-the-tests)

---

## Status

| Target | Driver | Needs | Verified |
|---|---|---|---|
| .NET | ADO.NET (any `DbConnection`) | nothing | full suite, over `Microsoft.Data.Sqlite` |
| JavaScript | `node:sqlite` (Node 22.5+) | Fable 5.15 | full suite |
| Erlang / BEAM | *none yet* | Fable 5.15 | everything except the driver |
| Rust | sqlx + SQLite (bundled) | Fable built from `main` (see below) | full suite |
| Rust | sqlx + **PostgreSQL** | Fable built from `main` (see below) | full suite, against PostgreSQL 18 |
| Rust | sqlx + MySQL / MariaDB | Fable built from `main` (see below) | compiles and connects; **not run against a server** |

**.NET, JavaScript and BEAM work on released Fable today** — all three were run
against Fable 5.15.0 from NuGet, not a local build.

**Rust works on Fable's `main` branch; the release carrying it is pending.**
The fixes it needed — the async builder's `Combine`, the async runtime's
polling overhead, exception and type-test codegen, string enumeration — are
all merged into [fable-compiler/Fable](https://github.com/fable-compiler/Fable)
(PRs #4916–#4918, #4920, #4921), and the full suite passes against a compiler
built from unmodified `main`. Until the next Fable release ships, build Fable
from `main` and point `FABLE_LOCAL_DLL` at it; see [GAPS.md](GAPS.md).

The three Rust backends are one code path — sqlx's `Any` driver picks the engine
from the URL — so they differ only in connection string, placeholder syntax and
the DDL you write yourself.

BEAM runs the whole portable half (query building, SQL generation, value
encoding, row readers, the async layer) but has no connector yet. `epgsql` and
`mysql-otp` are pure Erlang and so the easiest ones to add.

Packages: `SQLProvider.Fable.Core`, `.Ado`, `.Rust`, `.Js`, and the generator
as the dotnet tool `SQLProvider.Fable.Generator`. CI packs them on every build
and publishes to nuget.org on a `v*` tag.

---

## Getting started

Reference `SQLProvider.Fable.Core` plus the connector for your target — only
one connector ends up in a given build:

```xml
<PackageReference Include="SQLProvider.Fable.Core" Version="0.1.1" />
<PackageReference Include="SQLProvider.Fable.Ado" Version="0.1.1" />   <!-- .NET -->
<!-- or SQLProvider.Fable.Rust / SQLProvider.Fable.Js for a Fable target -->
```

(Working from a clone instead, `ProjectReference` the same projects under
`src/`.)

The packages carry their F# sources under `fable/`, the way Fable libraries do,
so a Fable project compiles the library to its own target while .NET uses the
compiled assembly from the same package.

JavaScript and BEAM need nothing beyond released Fable. Rust needs a Fable
built from `main` until the next release ships (see [Status](#status)), and the
consuming crate's `Cargo.toml` needs the driver stack the connector calls into:

```toml
[dependencies]
sqlx = { version = "0.8", default-features = false, features = [
    "runtime-tokio", "any", "sqlite", "postgres", "mysql",
] }
tokio = { version = "1", features = ["rt-multi-thread"] }
futures = "0.3"
```

---

## Generating from your schema

Hand-writing `Col.text "Customer" "Name"` per column is the work a provider is
supposed to remove, so generate it. The generator is a dotnet tool:

```bash
dotnet tool install --global SQLProvider.Fable.Generator
```

```bash
sqlprovider-fable-gen --sqlite ./app.db --module Northwind --out ./src/Schema.fs
```

`--postgres "Host=...;Username=...;Password=...;Database=..."` reads a live
PostgreSQL schema the same way. (From a clone,
`dotnet run --project tools/SQLProvider.Fable.Generator -- ...` does the same.)

Add `Schema.fs` to your project and check it in. Regenerate when the schema
changes rather than editing it — the names in it have to keep matching the
database.

For each table and view you get:

```fsharp
module Customer =
    let table = "Customer"

    let CustomerId = Col.int64 table "CustomerId"
    let Name = Col.text table "Name"
    let Country = Col.text table "Country"

    /// One row, as a record.
    type Row =
        { CustomerId: int64
          Name: string
          Country: string option }

    let ofRow (r: SqlRow) : Row =
        { CustomerId = Row.int64 r "CustomerId"
          Name = Row.text r "Name"
          Country = Row.textOpt r "Country" }
```

A nullable column becomes an `option` and reads through the `*Opt` reader. A
name that is an F# keyword, or is not a plain identifier, is escaped with
backticks rather than renamed.

Foreign keys become ready-made join conditions:

```fsharp
Query.from Orders.table
|> Query.join Customer.table Orders.Relations.toCustomer
```

---

## Connecting

**.NET** — wrap any `DbConnection`. Tell it the engine, so the query builder
generates the right SQL:

```fsharp
open SQLProvider.Fable
open SQLProvider.Fable.Ado

use connection = new SqliteConnection("Data Source=app.db")
connection.Open()
use connector = new AdoConnector(connection, vendor = Sqlite)
let conn = connector :> ISqlConnector
```

**Rust** — one connector for all three engines; the URL picks:

```fsharp
open SQLProvider.Fable.Rust

let conn = SqlxConnector("postgres://user:pw@localhost/app") :> ISqlConnector
// "sqlite::memory:" | "sqlite://app.db" | "mysql://user:pw@localhost/app"
```

**JavaScript** — `node:sqlite`, built into Node 22.5+, so no npm dependency:

```fsharp
open SQLProvider.Fable.Js

let conn = SqliteConnector("app.db") :> ISqlConnector
```

Every member of `ISqlConnector` returns `Async`. `Close()` is explicit; the .NET
connector also implements `IDisposable`, so `use` works there.

---

## Querying

Two spellings that build the same value. The computation expression:

```fsharp
sqlQuery {
    from Customer.table
    where (Customer.Country == "UK")
    sortByDescending Customer.Balance
    select (Customer.Name, Customer.Country)
    take 10
}
```

and the pipeline, for a query assembled in pieces:

```fsharp
Query.from Customer.table
|> Query.where (Customer.Country == "UK")
|> Query.orderByDesc Customer.Balance
|> Query.selectCol Customer.Name
```

A query is a plain record. Build it, store it, pass it around, add clauses
later, render it for a different engine than the one that built it. Nothing runs
until you hand it to `Db`.

### Comparisons

| operator | meaning |
|---|---|
| `==` &nbsp;&nbsp; `!=` | equal, not equal |
| `>.` &nbsp;&nbsp; `>=.` &nbsp;&nbsp; `<.` &nbsp;&nbsp; `<=.` | ordering |
| `=%` | LIKE, with the pattern written out |

Two more that a table cannot hold: `a |=| values` is `IN` over an array, and
`a |=? subquery` is `IN` over a subquery.

Each works against a value **or** against another column, so a join condition
reads the same as a filter:

```fsharp
Query.where (Customer.Country == "UK")                            // value
Query.join Order.table (Customer.CustomerId == Order.CustomerId)  // column
```

Combine with `.&&.` and `.||.`, **parenthesising each side** (see
[Gotchas](#gotchas-worth-knowing-up-front)), or avoid them entirely — successive
`where` calls are ANDed, and `whereAll` / `whereAny` take a list:

```fsharp
|> Query.whereAll [ Customer.Country == "UK"; Customer.Balance >. 100.0 ]
```

### Clauses

| | |
|---|---|
| filter | `where`, `orWhere`, `whereAll`, `whereAny` |
| join | `join`, `leftJoin`, `joinAs`, `leftJoinAs` |
| project | `select` (a column, or a tuple of up to five), `selectCol`, `selectExpr`, `selectAs` |
| sort | `sortBy`, `sortByDescending`, `thenBy`, `thenByDescending`, `sortByExpr` |
| page | `skip`, `take`, `distinct` |
| group | `groupBy`, `groupByCol`, `having` |

Aliases, for joining a table to itself:

```fsharp
Query.fromAs Customer.table "a"
|> Query.joinAs Customer.table "b" ((Col.onAlias "a" Customer.Country) == (Col.onAlias "b" Customer.Country))
```

### Functions

Named after the .NET methods they stand in for; each engine's spelling is chosen
when the SQL is generated.

- **Strings** — `upper`, `lower`, `trim`, `length`, `substring`,
  `substringFrom`, `replace`, `indexOf`, `concat`, `castText`
- **Search** — `contains`, `startsWith`, `endsWith`. These escape the value's own
  `%` and `_`, so searching for `50% off` finds that row and not every row
- **Dates** — `dateOnly`, `year`, `month`, `day`, `hour`, `minute`, `second`;
  `addYears` / `addMonths` / `addDays` / `addHours` / `addMinutes` / `addSeconds`
  by a **constant**; and `dateDiffDays` / `dateDiffSecs` between two dates
  (first minus second, counting calendar days the way `DATEDIFF` does)
- **Maths** — `abs`, `add`, `sub`, `mul`, `div`, `ceiling`, `floor`, `round`,
  `roundTo`, `truncate`, `greatest`, `least`, `castInt`. There is no `sqrt` or
  `pow`: SQLite as commonly built has no math functions, and those two have no
  arithmetic spelling (the ones above are spelled with CAST arithmetic there)
- **Null** — `isNull`, `isNotNull`
- **Conditional** — `ifThenElse`, `caseWhen`

One cross-engine wrinkle worth knowing: `castInt` on a fractional value
truncates on SQLite and rounds on PostgreSQL — use `floor`, `truncate` or
`round` first when the fraction matters.

**Conditional aggregates** — SQLProvider's
`g.Sum(fun r -> if cond then 1 else 0)` — are an aggregate over a CASE, and
several aggregates can share one query:

```fsharp
Query.from Customer.table
|> Query.selectAs
    [| "total", Expr.sum Customer.Balance.E
       "n", Expr.count
       "unnamed", Expr.sum (Expr.ifThenElse (Expr.isNull Customer.Country.E)
                                            (Literal (SqlInt 1L))
                                            (Literal (SqlInt 0L))) |]
```

### Set operations

`union` (deduplicating, `.Union()`), `unionAll` (`.Concat()`), `intersect` and
`except` combine whole queries. Ordering and paging written on the first query
apply to the combined result, and `ORDER BY` there must name a selected column:

```fsharp
germanCustomers
|> Query.unionAll richCustomers
|> Query.orderBy Customer.Name
|> Query.take 10
```

MySQL accepts `INTERSECT` and `EXCEPT` from 8.0.31.

Only ordering and paging apply to the combined result. Every other clause —
`where`, `groupBy`, `select` — still describes the **first** SELECT, whether
written before or after the `union`: SQL cannot filter a combined result
without nesting it in a FROM, which this library does not do. Filter the arms,
not the union.

```fsharp
|> Query.where (Expr.contains Customer.Name.E searchBox)
|> Query.selectExpr "band" (Expr.ifThenElse (Customer.Balance >. 100.0) high low)
```

`c.E` is a column as an untyped expression, for the places that take one.

### Subqueries

```fsharp
let ukCustomerIds =
    Query.from Customer.table
    |> Query.where (Customer.Country == "UK")
    |> Query.selectCol Customer.CustomerId

Query.from Order.table
|> Query.where (Order.CustomerId |=? ukCustomerIds)
```

`Expr.exists` and `Expr.notExists` take a query and correlate by naming the
outer table's columns inside it. `Expr.scalarQuery` puts one in value position,
so `Balance > (SELECT AVG(Balance) ..)` is expressible. `Query.allSatisfy` is
SQLProvider's `all`.

### Running it

```fsharp
let! rows     = Db.query conn q                  // Async<ResultSet>
let! firstRow = Db.tryHead conn q                // Async<SqlRow option>
let! n        = Db.count conn q                  // Async<int64>
let! any      = Db.exists conn q                 // Async<bool>
let! total    = Db.sum conn Customer.Balance q   // Async<SqlValue option>
```

`Db.avg`, `Db.min` and `Db.max` match `Db.sum`. When an empty result is a bug
rather than an answer, `Db.head` is `tryHead` that fails on no rows, and
`Db.exactlyOne` / `Db.tryExactlyOne` additionally fail when a second row shows
the WHERE was not as selective as believed.

`Db.count` on a `distinct` query that projects one column becomes
`COUNT(DISTINCT x)`; on a grouped query it refuses rather than counting one
group — put the aggregate in the projection instead.

`Db.toSql vendor q` gives the SQL and its parameters without touching a
database — useful for logging and for tests.

---

## Reading rows

```fsharp
let! rows = Db.query conn q
let customers = rows |> ResultSet.map Customer.ofRow
```

The generated `ofRow` is the usual way. Underneath, `Row` reads a named column,
case-insensitively:

`Row.text`, `Row.int`, `Row.int64`, `Row.float`, `Row.decimal`, `Row.bool`,
`Row.dateTime`, `Row.guid`, `Row.blob` — each with an `*Opt` variant returning
`option` for a nullable column.

```fsharp
let name = Row.text row "Name"
let country = Row.textOpt row "Country"
```

**Both sides of a join** — `select (order, customer)` in SQLProvider — is what
the generated `qualified` / `ofQualifiedRow` pair is for. `SELECT *` across two
tables collides wherever they share a column name; `qualified` aliases every
column `Table_Column` so nothing does:

```fsharp
let q =
    Query.from Order.table
    |> Query.join Customer.table (Order.CustomerId == Customer.CustomerId)
    |> Query.selectAs (Array.append Order.qualified Customer.qualified)

let! rows = Db.query conn q
let pairs = rows |> ResultSet.map (fun r -> Order.ofQualifiedRow r, Customer.ofQualifiedRow r)
```

---

## Writing

```fsharp
do! Db.insert conn (
    Insert.into Customer.table
    |> Insert.set Customer.CustomerId 200
    |> Insert.set Customer.Name "Alfreds"
    |> Insert.setOpt Customer.Country (Some "Spain"))

do! Db.update conn (
    Update.table Customer.table
    |> Update.set Customer.Name "New name"
    |> Update.whereKey Customer.CustomerId 200)

do! Db.delete conn (Delete.from Customer.table |> Delete.whereKey Customer.CustomerId 200)
```

There are `sqlInsert`, `sqlUpdate` and `sqlDelete` computation expressions too.

**Compute from the current row** — one statement rather than a read and a write:

```fsharp
|> Update.setExpr Customer.Balance (Expr.add Customer.Balance.E (Literal(SqlFloat 10.0)))
```

**Many rows, one statement:**

```fsharp
do! Db.insertMany conn [ for x in items -> Insert.into Customer.table |> Insert.set Customer.Name x.Name ]
```

**The generated key**, for identity and serial primary keys:

```fsharp
let! key = Db.insertReturning conn Customer.CustomerId insert
```

**A batch, applied atomically** — this is SQLProvider's `SubmitUpdates`:

```fsharp
let! affected =
    Batch.empty
    |> Batch.insert (Insert.into Customer.table |> Insert.set Customer.Name "one")
    |> Batch.update (Update.table Customer.table |> Update.set Customer.Name "two" |> Update.whereKey Customer.CustomerId 1)
    |> Db.submit conn
```

Either every statement lands or none does; a failure rolls back and re-raises
with the engine's own message. `Db.inTransaction conn body` does the same for
logic that mixes reads and writes.

**A write with no WHERE has to say so.** `Update` and `Delete` refuse to render
without a condition unless you spell out `Update.all` or `Delete.all`:

```text
SqlGen: the delete has no WHERE and would empty the table -- say Delete.all if that is meant
```

Emptying a table is occasionally what you want and never what you want by
accident, and a forgotten `whereKey` looks exactly like the deliberate version.

---

## Gotchas worth knowing up front

**`.&&.` and `.||.` need parentheses.**

```fsharp
(Customer.Balance >. 100.0) .||. (Customer.Balance <. 10.0)   // right
Customer.Balance >. 100.0 .||. Customer.Balance <. 10.0       // misparses
```

F# takes a custom operator's precedence from its leading characters, and every
operator starting with `=`, `<`, `>`, `|` or `&` lands in one left-associative
group — unlike the built-in `&&`, which sits below the comparisons. Most queries
never meet this, because successive `where` calls already AND and
`whereAll`/`whereAny` take a list.

**Identifiers are emitted unquoted.** PostgreSQL folds an unquoted identifier to
lower case at DDL time, so quoting `"CustomerId"` on a table created as
`CustomerId` fails to find the column. If your names need quoting, quote them in
the DDL too.

**Async is concrete.** `Db.query` returns `Async<ResultSet>` and you map it
yourself rather than passing a mapper in — a generic `Async<'T>` does not compile
on the Rust target, so nothing here is generic in its result:

```fsharp
let! rs = Db.query conn q
let customers = rs |> ResultSet.map Customer.ofRow
```

**`Async.RunSynchronously` does not exist on JavaScript.** Use
`Async.StartImmediate` in a JS entry point.

**decimal, `DateTime` and `Guid` are stored as text.** No engine here has a type
for all three, so they are encoded through shared helpers and every backend
writes identical bytes. A date column you want to use `dateOnly` or `year` on
should be a real date type in the schema, not one of these text-encoded values.

---

## What is not supported

`tests/SQLProvider.Fable.SmokeTests/QueryTests.fs` is the reference: it says,
case by case, what works, and it opens with a list of what does not and why. In
short:

- `groupJoin`, and `leftOuterJoin .. into g` flattening — a plain `leftJoin` works
- projecting into a tuple or a constructed record — shaping happens in `ofRow`,
  and a join reads both sides through the generated `qualified` aliases
- date arithmetic by a column rather than a constant (SQLite cannot either)
- nested queries in a `FROM` clause (SQLProvider does not do these either)
- `Sqrt`/`Pow` and the trigonometry — SQLite as commonly built has no math
  functions to spell them with
- stored procedures

`query { ... }` is not available and cannot be: it builds a
`System.Linq.Expressions` tree, which no Fable target has. `sqlQuery` uses
SQLProvider's operation names so a ported query mostly reads the same — see
[DESIGN.md](DESIGN.md) for the full reasoning.

---

## Running the tests

```bash
dotnet test tests/SQLProvider.Fable.Tests.Net/SQLProvider.Fable.Tests.Net.fsproj
```

```bash
pwsh tests/Rust/build.ps1
```

```bash
pwsh tests/Rust/build.ps1 -Url "postgres://user:pw@localhost/testdb"
```

```bash
pwsh tests/Js/build.ps1
```

```bash
pwsh tests/Beam/build.ps1
```

A server URL must point at an **empty** database: the suite creates its own
tables and does not drop them, so recreate it between runs.

The Rust suite needs `FABLE_LOCAL_DLL` pointing at a Fable built from the
`fix/rust-async` branch; see [Status](#status). The JavaScript and BEAM suites
run on released Fable.

Every code sample in this file is compiled by
`tests/SQLProvider.Fable.Tests.Readme`, so a renamed function breaks the build
rather than leaving the documentation quietly wrong.

---

## Further reading

- [DESIGN.md](DESIGN.md) — why the library is shaped this way
- [GAPS.md](GAPS.md) — Fable compiler gaps found while building this, with repros
