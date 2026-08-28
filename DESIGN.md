# Design notes

Why SQLProvider.Fable is shaped the way it is. The user-facing documentation is
in [README.md](README.md); the catalogue of Fable compiler gaps found while
building this, with minimal repros, is in [GAPS.md](GAPS.md).

## Why not just port SQLProvider's runtime?

Almost none of `System.Data` survives the trip to Rust, and almost none of it
needs to. Tracing `executeQuery` in SQLProvider's `SqlRuntime.Linq.fs`, one query
touches roughly twenty ADO members:

| Type | Members used |
|---|---|
| `IDbConnection` | `ConnectionString`, `Open`, `Close`, `State`, `Dispose` |
| `IDbCommand` | `CommandText`, `CommandTimeout`, `Parameters.Add`, `ExecuteReader`, `ExecuteScalar`, `ExecuteNonQuery` |
| `IDbDataParameter` | `ParameterName`, `Value`, `DbType`, `Direction` |
| `IDataReader` | `Read`, `FieldCount`, `GetName`, `GetValue` |
| `ConnectionState`, `DBNull`, `IDbTransaction`, `IsolationLevel` | — |

Everything else in SQLProvider's `ISqlProvider` — `GetTables`, `GetColumns`,
`GetRelationships`, `GetSprocs`, `CreateTypeMappings`, the `DataTable` and SSDT
machinery — is schema introspection. That runs on .NET at code-generation time
and never reaches a Fable target.

But those twenty members should not be ported either, because each collides with
Rust:

1. **`obj` values.** `reader.GetValue i : obj` becomes `Lrc<dyn Any>` in Rust, so
   every column read is a downcast. Replaced by the `SqlValue` union.
2. **`DBNull`** has no Rust analogue and shouldn't get one. It's `option`.
3. **A mutable `.Parameters` collection.** sqlx wants parameters supplied at
   execute time, not accumulated on a command object.
4. **`Open`/`Close`/`State`.** sqlx connects on construct and closes on drop.
   There is no ADO connection-state machine to model.
5. **`System.Transactions.TransactionScope`** — SQLProvider's ambient-scope write
   model has no Rust counterpart. Transactions here are explicit.

So the native surface is one interface with eight members
(`src/SQLProvider.Fable.Core/Connector.fs`), and everything else is portable F#
on top of it.

## How it works

### The interface is async-only

PostgreSQL and MySQL are async-native, and a synchronous interface would park a
thread per query. SQLite has no async to offer, so its connector completes
immediately — the cost there is writing `let!` for work that never yields.

On Rust the query runs on a tokio runtime and the result is handed back over a
oneshot channel, which Fable's `Async` awaits. The sqlx future never leaves
tokio; only the receiver crosses into Fable's executor. That keeps the caller's
thread free while the database is busy, which is the whole point, and it keeps
`Async`'s `Send + Sync` bound off sqlx's futures.

### One SQL text, three placeholder syntaxes

sqlx does *not* normalise placeholders: PostgreSQL wants `$1`, MySQL and SQLite
want a bare `?`, ADO.NET providers want `@name`. Query code here is always written
with `@name`, and `Dialect.bind` rewrites it on the way down — reordering the
values to match, duplicating an entry for a name used twice (neither positional
form can refer back), and leaving alone any `@` that is not a parameter, such as
an address inside a literal or a MySQL `@@version`.

This is pure logic with no database in it, which makes it the one part of the
multi-vendor story that is fully testable anywhere: `DialectTests.fs` covers all
three styles, and the .NET suite additionally runs the whole smoke suite a second
time in `Numbered` mode against real SQLite, which accepts `$1` as a parameter
name.

### Result sets are materialised eagerly

Not laziness: a sqlx row stream borrows the connection that produced it, so a
reader kept alive across F# calls would need a self-referential struct.
SQLProvider's own `Sql.dataReaderToArray` already reads the whole reader into an
array before projecting, so nothing is lost.

### One connection, not a pool

A pool would hand successive statements to different connections, which breaks
`BEGIN`/`COMMIT` — they are plain statements here — and, for `sqlite::memory:`,
would give each connection its own empty database.

### Mapping is generated, not reflected

Fable's Rust `FSharpPropertyInfo` carries only a name (see fable-library-rust
`QuotationTypes.fs`), so `PropertyInfo.PropertyType` is unavailable and a generic
"convert each column to whatever the record field wants" mapper has nothing to
dispatch on. Mappers state conversions explicitly instead:

```fsharp
let ofRow (r: SqlRow) : Customer =
    { CustomerId = Row.int r "CustomerId"
      Name = Row.text r "Name"
      Country = Row.textOpt r "Country"
      Balance = Row.float r "Balance" }
```

This is what the schema code generator will emit per table. It is also faster and
clearer than reflection on .NET.

### The native boundary carries primitives only

`sqlx_native.rs` never sees a Fable-generated `SqlValue`. Values cross as an
integer tag plus a typed accessor call, so a change in how Fable lays out unions
cannot silently break the shim.

### decimal, DateTime and Guid travel as text

sqlx's `Any` driver has no decimal, date or uuid kind, and SQLite has no such
storage class either. Every backend encodes these three through the same
`Convert` helpers, so identical bytes are stored whichever one wrote them, and
the readers parse them back invariantly. The suite asserts the stored text, not
only the round trip, because a locale-sensitive conversion sneaking in would
still round-trip on the machine that wrote it.

Verified across engines rather than asserted: after a PostgreSQL run, `psql`
shows `1234.5678` and `2026-08-27T10:30:00.0000000` in the table — the same bytes
the .NET, Rust/SQLite and Node runs write.

`Convert` is hand-rolled, and every piece of it is there because a target
disagreed:

- `Decimal`'s culture-taking overloads do not exist on Rust, and the culture-less
  ones throw under a comma-separator locale such as fi-FI.
- `DateTime.ToString "o"` emits seven fractional digits on .NET and Rust but
  three on JavaScript, which goes through `Date.toISOString`. The format is
  therefore spelled out digit by digit rather than borrowed — caught by the
  "date stored as ISO-8601" assertion the first time the suite ran on Node.
- `DateTime.AddTicks` is not mapped on BEAM (`DateTimeOffset.AddTicks` is), so
  the parser reconstructs from a tick count instead.

## Where this differs from SQLProvider

### What the query syntax cannot be

It is not `query { }`. SQLProvider writes
`join order in ctx.Main.Orders on (customer.CustomerId = order.CustomerId)`;
the closest here is
`join Order.table (Customer.CustomerId == Order.CustomerId)` -- the table is
named rather than bound to a variable, and the comparison needs its own
operator. `select (Customer.Name, Customer.Country)` does read like
SQLProvider's.

The gap is the price of having no expression trees: nothing can inspect a
lambda, so every part of a query has to be a value the code builds rather than
code the compiler hands over. Tuple projections into a typed *result* are the
clearest thing still missing -- `select` takes typed columns, but the shaping
into a record happens in the generated `ofRow` rather than in the query.

### On the objection this drew in SQLProvider itself

SQLProvider PR [#457](https://github.com/fsprojects/SQLProvider/pull/457) tried
the same idea there -- a `sqlQuery` CE exposing only the operators that actually
work -- and was closed on the maintainer's view that *"we should implement the
whole LINQ support rather than limiting ourselves to some custom developer
interface which is not familiar to anyone"*.

That objection is right where it was made and does not transfer here. In
SQLProvider, `query { }` works and a narrower facade is a step back from
finishing it. On a Fable target `System.Linq.Expressions` does not exist at all,
so there is no LINQ support to complete and nothing to be narrower than.

The familiarity half of the objection does transfer, and it is why the
operations are named after SQLProvider's rather than invented: a query ported
from there mostly reads the same. Where this goes further than LINQ can --
`fromAs`/`joinAs` for aliases, `whereAny`, `selectAs`, `sortByExpr` -- it is
because the AST underneath is SQL, not because the CE is doing something clever.

That PR also ran into the async terminators (`countAsync` and friends) not
fitting inside the CE. They are outside it here on purpose: the CE builds a
`Query` and `Db.query`/`Db.count`/`Db.tryHead` run it, so nothing about
execution has to be expressible as a custom operation.

### Why not `query { ... }`

SQLProvider's `query { }` builds a `System.Linq.Expressions` tree, which no
Fable target has. F# *quotations* do survive to Rust, JavaScript and BEAM, so a
`where <@ fun c -> c.Country = "UK" @>` front-end is the natural next step — but
not yet, for two measured reasons:

- `DerivedPatterns.AndAlso`/`OrElse` and captured locals arriving as `Value`
  rather than `Var` both live on an **unmerged** Fable branch
  (`feat/quotation-derived-patterns`). On stock Fable, `&&` inside a quotation
  does not compile at all.
- `PropertyGet` hands back a `PropertyInfo` on Rust but a bare string on the
  other targets, so reading a column name out of a quotation needs a per-target
  shim.

The AST underneath is what a quotation front-end would translate *into*, so
nothing here is wasted when that lands.

## How the generated code is verified

`tests/SQLProvider.Fable.SmokeTests/GeneratedSchema.fs` is a checked-in
generated file, compiled by the shared test project -- so it is compiled and
exercised on .NET, Rust, JavaScript and BEAM, which is the only assertion that
really matters about a generator. A test regenerates it from a live SQLite
schema and compares, so the checked-in copy cannot drift
(`SQLPROVIDER_FABLE_REGENERATE=1` rewrites it after a deliberate change).

## Fable Rust codegen gaps found while building this

Nine, all worked around here, all catalogued with repros in `GAPS.md`
(G7, G8, G9, G20, G21, G23, G24, G25, G26). The last four came out of the query
builder — the first code in this project with a non-trivial amount of pattern
matching in it, and the first computation expression:

1. **Object expression calling back into its enclosing object.** Generates a
   struct field literally named `_`, a reserved identifier in Rust. Matches the
   cases already commented out in Fable's own `MiscTests.fs`.
2. **`this :> ISomeInterface` inside the type's own interface implementation.**
   `interface_cast!` expands to a plain `as`, which Rust rejects as a
   non-primitive cast. Worked around by moving the logic to free functions.
3. **Doubly-nested closure over a captured value.** `Array.init n (fun r ->
   Array.init m (fun c -> ...raw...))` clones `raw` into the outer `Fn` closure
   but *moves* it into the inner one, so it does not borrow-check. Worked around
   with explicit loops and a top-level helper.
4. **The async builder has no `Combine`.** Any `async` block with a computation
   in statement position — `if cond then do! x`, a `for`, a `while` — emits
   `singleton.Combine(..)`, and neither `singleton` nor the method exists in
   `fable-library-rust`. Worked around by binding the offending `match` to a
   name. This one is a real constraint on generated code, not just on ours.
5. **`Async<'T>` where `'T` is an interface** emits a trait object with no
   `Send + Sync` bound, which `Async` requires. Worked around by flattening
   `BeginTransaction`/`Commit`/`Rollback` onto `ISqlConnector`.
6. **A fieldless DU matched with unit-valued branches** emits an integer tag
   pattern against the value itself. Worked around with equality.
7. **A record field typed as an imported library class** (`StringBuilder`) is
   not boxed in the record's reflection info. Worked around with a
   `ResizeArray<string>`.
8. **A guarded nested pattern** makes the decision-tree lowering `mem::zeroed`
   every bound array, list and option — which panics on an `Arc` at runtime,
   not at compile time. Worked around by moving the guard inside the branch.
   This is the one to fix upstream first: it compiles cleanly and then dies.
9. **A member taking `unit`** is declared with the parameter but called without
   it, which breaks any computation-expression builder. Worked around by making
   the parameter generic.

Also worth knowing: Fable emits the `#[path = "..."] mod ...` declaration for a
`.rs` pulled in by `importAll`, but does not copy the file — the build script
stages it.

