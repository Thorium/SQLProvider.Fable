# SQLProvider.Fable

A light SQL data-access runtime for F# that compiles to **Rust** through Fable, and
to .NET through ADO.NET, from one source.

The shared smoke suite in `tests/SQLProvider.Fable.SmokeTests/Smoke.fs` is written
once against `ISqlConnector` and runs on both backends unchanged — no `#if` in it.

## Status

| Backend | Driver | Verified |
|---|---|---|
| .NET | ADO.NET (`Microsoft.Data.Sqlite`) | 14/14 smoke tests pass |
| Rust | `rusqlite` (bundled SQLite) | 14/14 smoke tests pass |

Not built yet: the schema code generator, the quotation-to-SQL translator, and
backends other than SQLite. See [Not done yet](#not-done-yet).

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
3. **A mutable `.Parameters` collection.** rusqlite and sqlx want parameters
   supplied at execute time, not accumulated on a command object.
4. **`Open`/`Close`/`State`.** rusqlite opens on construct; sqlx has a pool.
   There is no ADO connection-state machine to model.
5. **`System.Transactions.TransactionScope`** — SQLProvider's ambient-scope write
   model has no Rust counterpart. Transactions here are explicit.

So the native surface is one interface with five members
(`src/SQLProvider.Fable.Core/Connector.fs`), and everything else is portable F#
on top of it.

## Layout

```
src/SQLProvider.Fable.Core     portable F#: SqlValue, ISqlConnector, Row readers
src/SQLProvider.Fable.Ado      .NET backend over ADO.NET
src/SQLProvider.Fable.Rust     Fable/Rust backend: F# bindings + sqlite_native.rs
tests/SQLProvider.Fable.SmokeTests   the suite, written once, backend-agnostic
tests/SQLProvider.Fable.Tests.Net    runs it under xunit over ADO.NET
tests/Rust                           runs it as a Rust binary over rusqlite
```

`Core` and `SmokeTests` must stay free of `System.Data`, reflection,
`System.Linq.Expressions` and async — anything there has to survive Fable's Rust
backend.

## Running the tests

.NET:

```bash
dotnet test tests/SQLProvider.Fable.Tests.Net/SQLProvider.Fable.Tests.Net.fsproj
```

Rust (needs `rustup` and the `fable` CLI on PATH):

```bash
pwsh tests/Rust/build.ps1
```

That script compiles F# to Rust, stages `sqlite_native.rs` and `Cargo.toml` into
`build/rust`, then runs the binary. It exits non-zero if any test fails.

## Design notes

### Result sets are materialised eagerly

Not laziness: a rusqlite `Rows` iterator borrows the `Statement` that produced
it, so a reader kept alive across F# calls would need a self-referential struct.
SQLProvider's own `Sql.dataReaderToArray` already reads the whole reader into an
array before projecting, so nothing is lost.

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

`sqlite_native.rs` never sees a Fable-generated `SqlValue`. Values cross as an
integer tag plus a typed accessor call, so a change in how Fable lays out unions
cannot silently break the shim.

### Parameters

SQL text uses `@name` on every backend (rusqlite accepts `@`, `:` and `$`;
Microsoft.Data.Sqlite accepts `@`). `SqlParam.Name` is the bare name; each
connector adds the marker its driver wants.

## Fable Rust codegen gaps found while building this

Three, all worked around here, all worth filing upstream:

1. **Object expression calling back into its enclosing object.** Generates a
   struct field literally named `_`, a reserved identifier in Rust, and calls the
   enclosing object's method on the object-expression struct. Worked around with
   a concrete class (`RusqliteTransaction`). Matches the cases already commented
   out in Fable's own `MiscTests.fs`.
2. **`this :> ISomeInterface` inside the type's own interface implementation.**
   `interface_cast!` expands to a plain `as`, which Rust rejects as a
   non-primitive cast. Worked around by moving the logic to free functions.
3. **Doubly-nested closure over a captured value.** `Array.init n (fun r ->
   Array.init m (fun c -> ...raw...))` clones `raw` into the outer `Fn` closure
   but *moves* it into the inner one, so it does not borrow-check. Worked around
   with explicit loops and a top-level helper.

Also worth knowing: Fable emits the `#[path = "..."] mod ...` declaration for a
`.rs` pulled in by `importAll`, but does not copy the file — the build script
stages it.

## Not done yet

- **Schema code generator.** Reads SQLProvider's own offline schema JSON
  (`SchemaCache.Save`, the documented `ContextSchemaPath` format) and emits
  records plus `ofRow` mappers. File-format coupling only: no assembly
  reference to SQLProvider, no internals, no version lockstep.
- **Quotation-to-SQL translator.** `<@ fun c -> c.Country = "UK" @>` to a WHERE
  clause, walking the `FSharpExpr` AST that Fable's Rust target now supports.
- **Blob parameters on Rust** are bound but untested; blob *results* are covered
  by the shim, not by a test.
- **Other backends.** Postgres/MySQL via sqlx or tokio-postgres will need the
  async story worked out — Fable's Rust async is its own machinery and bridging
  it to tokio is not free. rusqlite is synchronous, which is why it came first.
- **Dates and decimals** currently travel as text/float. A real backend wants
  native cases on `SqlValue`.
