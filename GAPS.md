# Fable Rust gap catalogue

Everything SQLProvider.Fable needs from Fable's Rust backend, probed
systematically so the fixes can go upstream as one planned set rather than a
dribble of "that wasn't enough" PRs.

## Method

1. **Write the probe suite for the real workload** — a scratch project of seven
   modules covering quotations, active patterns, the column types a schema
   actually produces, collections, string building, interfaces, and
   exceptions/generics/recursion/comparison. (The probe projects were removed
   once the hunt concluded; see git history for `tests/RustProbe/`.)
2. **Run it on .NET first.** The baseline caught four bugs in the *probes*
   before any Rust result was trusted. It is green at 109/109.
3. **Run the same code through Fable to Rust**, and treat every difference as a
   candidate.
4. **Minimise each candidate** to a standalone repro, then re-verify it in
   isolation. Two candidates dissolved under this step (see *Retracted*).
5. **Check each survivor against Fable's own test suite** to say whether it is
   already known.

### Read this before reproducing anything

**The released Fable CLI (5.15.0) does not contain the quotation work.**
PR #4790 is merged on `main` but unreleased, so a stock `dotnet tool install -g
fable` reports every `Patterns.*` case as "not supported by Fable" and hides
every real finding behind that. All results below come from Fable built from
`main` at `899f99c98`.

To reproduce, build the CLI from the Fable checkout. Note `Fable/global.json`
pins SDK `10.0.100` with `rollForward: latestPatch`, which rejects a 10.0.3xx
SDK; relax it temporarily rather than committing a change.

## Summary

| # | Gap | Impact here | Status |
|---|---|---|---|
| G1 | An active pattern in the first clause of a non-exhaustive match crashes the compiler | **Blocking** | **FIXED** |
| G2 | Constructing an F# `exception` value crashes the compiler (same root cause as G1) | High | **FIXED** |
| G3 | `DerivedPatterns` (`AndAlso`/`OrElse`/`SpecificCall`) unmapped | **Blocking** | **FIXED** |
| G4 | `:? T as x` on `obj` yields `&T` where `T` is expected | **Blocking** | **FIXED** |
| G5 | `sprintf "%O"` on a boxed `obj` does not compile | Low | New |
| G6 | `string` applied to a tuple does not compile | Low | **FIXED** |
| G7 | Object expression calling into its enclosing object emits a field named `_` | High | Known (disabled tests) |
| G8 | `this :> IFace` inside the type's own interface impl emits a bad `as` | High | New |
| G9 | Doubly-nested closure over a captured value does not borrow-check | High | New |
| G10 | `let mutable` written inside `finally` is silently lost | **Blocking** | **FIXED** |
| G11 | `use` does not call `Dispose` | High | Known (`MiscTests.fs:1186`) |
| G12 | A quotation's captured local is a `Var`, not a `Value` | **Blocking** | **FIXED** |
| G13 | `string`/`ToString` on unions and records prints the Rust type name | Medium | Known (`TypeTests.fs:293`) |
| G14 | `sprintf "%A"` prints the Rust type name | Low | Known (`StringTests.fs:462`) |
| G15 | Decimal's culture-taking overloads (`ToString(IFormatProvider)`, `Decimal.Parse(s, NumberStyles, CultureInfo)`) are not modelled | **Blocking** | **FIXED** |
| G16 | `raise` discards the exception value; typed `with` handlers never match and fall through | High | New |
| G17 | Awaiting a task slept 100ms per poll: `Async.StartAsTask` took 2718ms for 65ms of work | **Blocking** for async vendors | **FIXED** |
| G18 | `Async.Parallel` is not implemented on Rust | High | **FIXED** |
| G19 | ~~Every `let!` costs ~11ms~~ — **retracted**, see below; the sleeps were elsewhere and are now gone | — | **RETRACTED** |
| G20 | The Rust async builder has no `Combine`: any `async` block whose body has a computation in statement position emits `singleton.Combine(..)`, and neither `singleton` nor the method exists | High | **FIXED** |
| G21 | `Async<'T>` does not accept an interface or a generic parameter as `'T`: `Async` requires `T: Send + Sync`, and neither a trait object nor a generic parameter carries those bounds | High | New (attempted, see below) |
| G22 | `DateTime.AddTicks` is not mapped on the BEAM target, though `DateTimeOffset.AddTicks` is and the runtime already stores ticks | Low | New |
| G23 | A fieldless DU matched with unit-valued branches emits an integer tag pattern against the value, which does not compile | High | **FIXED** |
| G24 | A record field typed as an imported library class (e.g. `StringBuilder`) is not boxed in the record's reflection info, so the crate does not compile | High | New (fix attempted, see below) |
| G25 | A guarded nested pattern makes the decision-tree lowering `mem::zeroed` every bound array, list and option — a runtime panic | **Blocking** | **FIXED** |
| G26 | A member taking `unit` is declared with the parameter but called without it, so any computation-expression builder fails to compile | Medium | New (fix attempted, see below) |
| G27 | `for ch in someString` does not compile on Rust: a string has no `GetEnumerator` | Low | New |

"Blocking" means SQLProvider.Fable cannot ship the feature without it.


### Fix progress

**Every blocking gap is fixed.** Three branches on the Fable checkout, all
local and unpushed, split so the Rust-only work can go up without waiting on
sign-off for the shared transforms.

| Branch | Gaps | Scope |
|---|---|---|
| `fix/rust-exception-and-typetest-codegen` | G1, G2, G4 | Rust only |
| `fix/rust-runtime-semantics` | G10, G15 | Rust only |
| `fix/rust-formatting` | G6 | Rust only |
| `fix/rust-async` | G17, G18 | Rust only |
| `feat/quotation-derived-patterns` | G3, G12 | shared transforms, all targets |

Every fix carries a regression test whose expected values were taken from a
.NET run first. All four targets with a quotation runtime are verified:
**JavaScript 3085, Rust 2659, Dart 1338, BEAM 2673**. The Rust branches are also
run through the full CI matrix on Linux — default, `lrc_ptr`, `threaded` and
`no_std` — since `no_std` cannot be built on Windows at all.

Still open, none of them blocking. Triaged for cheap wins, and only G6 was one:

- **G5** (`%O` on a boxed obj) needs a runtime ToString for `dyn Any`, which means
  going through the value registry. Not cheap.
- **G7, G8** both need a design call rather than a repair: G8's `this :> IFace`
  has no `Rc` to coerce from inside a member, and wrapping one changes identity.
- **G9** is diagnosed to root cause below, and needs the same kind of call.
- **G13, G14** are exact F# formatting for unions and records — a real feature,
  and Fable already tracks it as a TODO. A Debug-derived approximation would
  print `Lit(1)` where .NET prints `Lit 1`: close enough to look right and
  quietly differ, which is worse than the current obvious wrongness.
- **G16** (raise discards the exception) is substantial.
- **G20** (no `Combine` in the async builder) and **G21** (`Async<interface>`)
  came out of the sqlx work and are the two that most deserve a fix: G20 forbids
  `if .. then do! ..` in any async block, which a code generator cannot promise.
  It looks contained -- one struct with `Combine`/`While`/`For`/`TryWith`/
  `TryFinally`/`Using` plus a `singleton` value, since the replacement already
  emits instance calls for all of them.

A note for anyone reproducing on a machine with Application Control: it blocks
`rustfmt.exe` outright, which breaks `build.sh fable-library --rust` at its
`cargo fmt` step, and it intermittently blocks freshly built binaries including
Fable's own `Rust.AST.dll`. The library can be built by invoking a
previously-allowed published `Fable.dll` directly with the same arguments
`BuildFableLibrary` uses, then copying `*.rs`, `Cargo.toml` and
`vendored/` into `temp/fable-library-rust` by hand.

## What already works

Worth stating plainly, because it settles the design question that prompted this
audit — whether a schema-driven code generator can emit real column types:

- **`decimal`, `DateTime`, `Guid`, `int64`, `byte[]`, and `option` fields all
  work on Rust** (P03: 23/23), including decimal arithmetic and comparison,
  `DateTime` round-tripping through `"o"`, `Guid.Parse`, record
  copy-and-update, and structural equality.
- Collections: `Map`, `Dictionary`, `Set`, `ResizeArray`, `groupBy`, `fold`,
  nested `List.map`/`Seq.map` (P04: 22/22).
- String building including `StringBuilder`, `sprintf` with `%s/%d/%f/%b`, and
  interpolation (P05: 27/28).
- Active patterns in every form — partial, parameterised, multi-case, over
  strings — provided G1's ordering constraint is respected (P02: 4/4).
- Class-to-interface upcasts, object expressions capturing locals, two
  interfaces on one class, an interface member returning another interface
  (P06: 9/10).

So the schema-driven design is sound. The blockers are all in the *translator*
and *lifetime* layers, not the data layer.

## Retracted during minimisation

Two candidates looked like bugs and were not. Recording them so nobody re-files
them:

- **"`try/finally` does not run the `finally` block."** False. `finally` runs.
  The observable failure was G10 — the mutable flag written inside it was lost.
  A `ref` cell in the same position works.
- **"`List.sort` on a union is wrong."** False. Sorting is correct; only the
  rendering of the result was wrong (G13).

---

## Details

### G1 + G2 — one root cause: `makeRecord` ignores the synthetic base field

Both crashes report `error EXCEPTION: The lists had different lengths` with no
source location. They are the same bug, traced to:

```
List.zip  ->  Fable2Rust.Util.makeRecord           (Fable2Rust.fs:2111)
          <-  Fable2Rust.Util.transformValue       (2324)
          <-  Fable2Rust.Util.transformThrow       (3451)
```

`makeRecord` zips `getEntityFieldsAsIdents ent` against the constructor's
`values`. `getEntityFieldsAsIdents` **prepends a synthetic `__base__` ident**
whenever the entity has a valid base type (Fable2Rust.fs:4209-4214), but a
`NewRecord` value list never carries a base value — so idents is exactly one
longer, which is what the error says (`list2 is 1 element shorter than list1`).

`ignoredBaseTypes` is only `{ System.Object; System.ValueType }`
(Fable2Rust.fs:5037), so **`System.Exception` is a "valid" base type** and every
F# `exception` declaration trips this.

The object-expression path shows the intended convention: at Fable2Rust.fs:2711
the base *value* field is prepended only when there is a `baseCall`, alongside
the base *ident*. `makeRecord` prepends the ident without ever supplying the
value.

**G2 is the direct form.** Constructing any F# exception is a `NewRecord` on an
entity whose base is `System.Exception`:

```fsharp
exception TranslationError            // zero fields is enough
let make () : exn = TranslationError :> exn
```

**G1 is the indirect form.** F# emits `raise (MatchFailureException(...))` for a
match it cannot prove exhaustive. `FSharp2Fable.fs:1521-1546` normally rewrites
that into a plain `throw Error(msg)` — but **only** when the decision
expression's outermost node is `IfThenElse(UnionCaseTest ...)`. When the first
clause is an active pattern, the outermost node is the active pattern's own
call instead, so the rewrite falls into the `| _ ->` branch already marked
`// TODO: rewrite other MatchFailureException to failwith "The match cases were
incomplete"` (line 1544). The raw `MatchFailureException` construction then
survives into `makeRecord`.

So the trigger is precisely: **an active pattern in the first clause of a match
that F# cannot prove exhaustive.**

```fsharp
let (|IsConst|_|) e = match e with Const c -> Some c | _ -> None
let (|IsColumn|_|) e = match e with Column(t, n) -> Some(t, n) | _ -> None

let render e =
    match e with
    | IsConst c -> c
    | IsColumn(t, n) -> t + "." + n     // crashes: no union-case pattern needed
```

The model accounts for every observation:

| Shape | Result | Why |
|---|---|---|
| Active pattern first, no catch-all | **crash** | rewrite skipped, MatchFailure survives |
| Same, with a trailing `\| _ ->` | compiles | match is exhaustive, no MatchFailure emitted |
| Union case first, active pattern after | compiles | outermost node is `UnionCaseTest`, rewrite applies |
| Incomplete match, no active pattern | compiles | same — rewrite applies |

**Correction to an earlier version of this document:** this was first written up
as "a union-case pattern appearing *after* an active-pattern case". That
description matched every example tried at the time but named the wrong cause —
the union case is incidental, and what matters is the active pattern being
*first*. The workaround comments in `P02_ActivePatterns.fs` still hold (adding a
catch-all is the simplest fix) but describe the trigger the same wrong way.

**Status: partly fixed.** Implementing it turned up four defects layered behind
these two symptoms, not the two predicted. Branch `fix/rust-makerecord-base-field`
on the Fable checkout (committed locally, not pushed) carries the first two.

| # | Defect | State |
|---|---|---|
| D1 | `makeRecord` never supplies the synthetic base value → the crash | **Fixed** |
| D2 | Generated `get_Message` formats `__base__`, and `{:?}` on it walks a null `innerException` → stack overflow / segfault at runtime | **Fixed** |
| D3 | The `MatchFailureException` rewrite only fires for `IfThenElse(UnionCaseTest ...)`, so G1 emits an import of a type the Rust library does not have | Open |
| D4 | `raise` discards the exception value → typed handlers never match (see G16) | Open |

D2 was invisible until D1 was fixed: the program could not be compiled at all
before, so nothing had ever run. It is the same root cause — a call site of
`getEntityFieldsAsIdents` that does not want the synthetic base — which makes
three such sites in total, counting the class constructor that handles it
correctly.

With D1 and D2 fixed, **G2 compiles and runs**: constructing and destructuring
an F# exception value both work. **G1 still fails**, now at
`unresolved import fable_library_rust::Microsoft::FSharp::Core::MatchFailureException`
— which is D3, exactly the second defect predicted, and confirms the crash and
the missing rewrite were independent.

Verified against Fable's own Rust suite: **2657 passed, 0 failed**, including a
new regression test. The fix is Rust-only (`Fable2Rust.fs`), which matters
because Fable's `AGENTS.md` warns against touching the shared transforms.

D3 has two possible shapes and the choice is a maintainer's call:

- Generalise the rewrite at `FSharp2Fable.fs:1544` as its own TODO suggests.
  Smallest change, but it is a shared transform: JS/Python/Dart currently
  construct a real `MatchFailureException` (`Replacements.fs:717` imports one
  from their library), so this would change the observable exception type on
  every target.
- Give the Rust backend its own mapping for `Types.matchFail`, the way the
  other targets have one. Larger, but contained to Rust and consistent with
  `AGENTS.md`'s instruction to look at how other targets solve the same problem.

### G3 — `DerivedPatterns` are not mapped

`AndAlso`, `OrElse` and `SpecificCall` all report "is not supported by Fable".

This is **target-independent**: `Replacements.Util.tryQuotationCall` dispatches
`Types.patternsModule` but has no case for `DerivedPatternsModule`, so every
Fable target is affected, not just Rust.

Impact is larger than it looks. `&&` and `||` desugar to `IfThenElse`, so
without `AndAlso`/`OrElse` a translator has to re-derive them structurally, and
without `SpecificCall` operators must be matched by compiled name
(`"op_Equality"` and friends). `P01_Quotations.fs` does exactly that and works —
so this is survivable, but it is the difference between a clean translator and a
brittle one. `P01_Quotations.fs.orig` is the version that uses the derived
patterns.

### G4 — `:? T as x` on `obj` yields a reference

```fsharp
let unboxBool (o: obj) =
    match o with
    | :? bool as b -> b = true      // error: can't compare `&bool` with `bool`
    | _ -> false

let unboxString (o: obj) : string =
    match o with
    | :? string as s -> s           // error: expected `LrcStr`, found `&LrcStr`
    | _ -> ""
```

Blocking because a quotation's `Value` node carries `obj`, and discriminating
that value by type is the only portable way to turn a literal into SQL. (Rust's
quotation runtime does carry a type-name string in the `Value` node, but it is
bound as a wildcard because .NET models that slot as `System.Type`, so it cannot
be used portably.)

Sidestepped in `P01_Quotations.fs` by parameterising every constant instead of
inlining it — which a real translator should do anyway.

### G5 — `sprintf "%O"` on a boxed value

```fsharp
let formatObj (o: obj) = sprintf "%O" o
// error: `dyn Any` doesn't implement `std::fmt::Display`
```

### G6 — `string` applied to a tuple

```fsharp
let formatTuple (t: int * int) = string t
// error: `(i32, i32)` doesn't implement `std::fmt::Display`
```

### G7 — object expression calling into its enclosing object

Found building `RusqliteConnector.BeginTransaction`. The generated struct gets a
field literally named `_` — a reserved identifier in Rust — and the enclosing
object's method is called on the object-expression struct rather than on the
captured object:

```rust
struct ObjectExpr {
    _: LrcPtr<RusqliteConnector>,          // reserved identifier
}
impl ISqlTransaction for ObjectExpr {
    fn Commit(&self) { __self__.exec_Z721C83C5(string("COMMIT")) }   // wrong receiver
}
```

Known: `MiscTests.fs:232`/`237` have the equivalent cases commented out.
Worked around with a concrete class.

### G8 — `this :> IFace` inside the type's own interface implementation

`interface_cast!` expands to a plain `as`, which Rust rejects:

```
error[E0605]: non-primitive cast: `RusqliteConnector` as `Rc<dyn ISqlConnector>`
```

Note that ordinary upcasts are fine — `(Quoter() :> IRenderer)` works, including
directly on a constructor result (P06 passes those). It is specifically the
self-cast from inside the type's own interface impl. Worked around by moving the
shared logic to free functions.

### G9 — doubly-nested closure over a captured value

```fsharp
Array.init rows (fun r -> Array.init cols (fun c -> raw.kind (r, c)))
```

The outer `Fn` closure clones `raw`, the inner one **moves** it, so it does not
borrow-check. Worked around with explicit loops and a top-level helper.

Narrowed further while triaging the remaining gaps: **it needs the captured type
to be an `[<Erase; Emit>]` one.** The same doubly-nested shape over ordinary F#
types compiles and runs correctly, which is why P04's nested
`List.map`/`Seq.map` cases pass.

Root cause, in `Fable2Rust.fs`:

- `transformLambda` strips its captured idents from `ScopedSymbols` for the body
  context (so inner uses do not consume outer usage counts).
- A nested lambda then asks `isClosedOverIdent`, whose remaining signals are
  `ident.IsMutable` and `shouldBeCloned`.
- `shouldBeCloned` is `isWrappedType || shouldBeRefCountWrapped`, and an erased
  type is neither as far as Fable can tell — it emits whatever the `Emit` string
  says, so nothing marks it ref-counted.

So the inner lambda does not classify the ident as captured, emits no clone, and
moves it out of the outer `Fn` closure.

Not a small fix, which is why it was left. Either `shouldBeCloned` treats an
erased declared type as needing a clone — safe by default, but it would add
clones elsewhere — or captured idents stay visible to nested lambdas for capture
analysis while still being excluded from usage counting. That is a design call
for the backend's owners rather than a mechanical repair.

### G10 — `let mutable` written inside `finally` is lost

```fsharp
let mutableInFinally () =
    let mutable ran = false
    let r =
        try
            try failwith "inner"
            finally ran <- true
        with ex -> ex.Message
    (ran, r)          // Rust: (false, "inner")   .NET: (true, "inner")
```

Silent wrong answer, no compile error. Verified narrowly:

- The `finally` block **does** execute (a `ref` cell set in the same position
  comes back `true`).
- The same mutable written inside a `with` handler works.
- The same mutable written inside a plain nested block works.

So it is specific to mutable-local writes inside `finally` — precisely where
cleanup bookkeeping lives, which is why it is rated blocking.

### G11 — `use` does not call `Dispose`

```fsharp
let log = ResizeArray<string>()
(fun () ->
    use r = new Resource(log)      // Resource.Dispose adds "disposed"
    log.Add "body") ()
// Rust: "body"        .NET: "body,disposed"
```

Known: `MiscTests.fs:1186` — ``// let ``use calls Dispose at the end of the
scope`` `` — is commented out.

Matters here because connection and transaction lifetime is exactly what `use`
is for. `ISqlConnector` deliberately exposes an explicit `Close` instead of
inheriting `IDisposable` for this reason.

### G12 — a quotation's captured local is a `Var`, not a `Value`

```fsharp
let wanted = "SE"
<@ fun (c: Customer) -> c.Country = wanted @>
```

.NET produces a `Value` node; Rust produces `Var "wanted"`. Observed directly in
the probe: the translator emitted `(Country = wanted)` on Rust against
`(Country = @p0)` on .NET.

Blocking, because captured locals are the main thing a query translator must
turn into parameters — a `Var` has no value to bind. (A standalone repro is in
the history; it could not be executed here because Application Control blocked
that particular binary, so the confirmation above comes from the probe run.)

### G13 / G14 — `ToString` and `%A` print Rust type names

```fsharp
string (Lit 1)        // "iso::module_9f7078f3::Iso::Node"   .NET: "Lit 1"
sprintf "%A" [ "x" ]  // "...List_::List<...LrcStr>"          .NET: "[\"x\"]"
```

Both known: `TypeTests.fs:293` (`// TODO: .ToString() with records and unions`)
and `StringTests.fs:462`. Low impact here beyond error-message quality.

### G15 — culture-taking overloads are not modelled

```fsharp
open System.Globalization

d.ToString CultureInfo.InvariantCulture
// error[E0308]: expected `decimal`, found `Rc<dyn IFormatProvider>`

Decimal.Parse("1234.5678", NumberStyles.Number, CultureInfo.InvariantCulture)
// error[E0061]: this function takes 1 argument but 3 arguments were supplied

// DateTime.Parse(s, CultureInfo) looks like it belongs here and does not:
// it reaches ignoreFormatProvider through makeMemberCall and already worked.
// Checked against a build without the fix rather than assumed -- the original
// probe reported an error on that line, but it was a cascade from the two above.
```

`CultureInfo.InvariantCulture` itself lowers to `()`, so any call taking a
format provider fails to compile.

Rated blocking because the alternative is worse than unavailable. The
culture-*less* `Decimal.Parse s` compiles on both platforms and **throws** under
a comma-separator locale:

```
culture: fi-FI
string decimal:   1234.5678      <- F#'s `string` operator is invariant
d.ToString():     1234,5678      <- culture-sensitive
Decimal.Parse:    THREW: FormatException
```

So a library that stores a `decimal` as text has no supported way to read it
back. Without noticing this, SQLProvider.Fable would have shipped money columns
that silently corrupt on any machine outside the en-* / invariant locales.

Two things this establishes, both used by `Convert` in
`src/SQLProvider.Fable.Core/SqlValue.fs`:

- **F#'s `string` operator is invariant on both platforms** and is therefore
  usable as the encoder. Verified under fi-FI above.
- **Decoding must be hand-rolled** from culture-free primitives. `Int32.Parse`
  over a digit-only substring is safe (no separators are involved), so an
  ISO-8601 date and a fixed-point decimal can both be parsed by hand.

`tests/SQLProvider.Fable.Tests.Net` runs the shared smoke suite twice, once
under the ambient locale and once forced to fi-FI. The fi-FI run was
mutation-tested: reverting `Convert.decimalToText` to `d.ToString()` fails that
test and only that test.

---

### G16 — `raise` discards the exception value, so typed handlers never match

```fsharp
exception TranslationError of expr: string * reason: string

let v2 () =
    try
        raise (TranslationError("a", "b"))
    with
    | TranslationError(x, y) -> x + "/" + y
    | _ -> "other"

// .NET: "a/b"      Rust: "other"
```

Compiles cleanly and returns the wrong branch. The generated Rust shows why —
`raise` panics with the exception's *message string*, not the exception:

```rust
panic!("{}", LrcPtr::new(TranslationError { .. }).get_Message())
```

`Exception_::try_catch` then rebuilds a fresh `LrcPtr<Exception>` from that
string, so the handler's `try_downcast::<_, LrcPtr<TranslationError>>` can never
succeed and every typed handler falls through to the wildcard.

Only visible once G1/G2's D1 and D2 are fixed, because before that the program
could not be compiled at all.

Rated high rather than blocking for SQLProvider.Fable only because the
connector reports failures through `failwith` rather than typed exceptions. For
anyone porting code that discriminates exceptions by type — which is most
error-handling code — it is silently wrong, and a silent wrong branch is worse
than the crash that hid it.


### G20 — the Rust async builder has no `Combine`

Any `async` block with a computation in statement position — the ordinary
`if cond then do! x` shape, a `for`, a `while`, or a `match` whose branches are
unit and which is followed by more of the block — makes F# call the builder's
`Combine`. Fable's Rust replacement routes every unhandled builder method to an
instance call on `singleton`:

```fsharp
| meth, Some callee, _ -> makeInstanceCall r t i callee meth args
```

and `"DefaultAsyncBuilder"` resolves to `makeImportLib com t "singleton" "AsyncBuilder"`.
`AsyncBuilder_` in `fable-library-rust` defines only `delay`, `bind`, `r_return`,
`return_from` and `zero`, and has no `singleton` value at all, so the generated
code does not compile:

```text
error[E0432]: unresolved import `fable_library_rust::AsyncBuilder_::singleton`
   --> Smoke.rs:24:21
```

Minimal repro:

```fsharp
async {
    if System.DateTime.Now.Year > 2000 then
        do! Async.Sleep 1

    return 1
}
```

The same shape is what `Task` uses, via `TaskBuilder_`, which is missing the same
family. A fix wants one struct with `Combine`/`While`/`For`/`TryWith`/
`TryFinally`/`Using` plus the `singleton` value, since the replacement already
generates instance calls for all of them.

Worked around here by binding the statement-position `match` in `Smoke.fs` to a
name, which is a `let` rather than a `Combine`. That is fine for hand-written
code and not fine as a general constraint: a generated data-access layer cannot
be forbidden from writing `if`.

### G21 — `Async<'T>` over an interface or a generic parameter does not compile

`Async<'T>` requires `'T: Send + Sync` (`Async.rs:17`), and Fable emits an
interface-typed `'T` as `LrcPtr<dyn IFace>`, a trait object with no auto-trait
bounds. So:

```fsharp
type ISqlTransaction =
    abstract Commit: unit -> Async<unit>

type ISqlConnector =
    abstract BeginTransaction: unit -> Async<ISqlTransaction>
```

produces

```text
error[E0277]: `dyn ISqlTransaction` cannot be sent between threads safely
    = note: required for `Arc<dyn ISqlTransaction>` to implement `Send`
note: required by a bound in `fable_library_rust::Async_::Async`
```

A returned *record* or *union* is fine — only interfaces are. This is the
`threaded` feature's bound, and `Async` exists only under `threaded`, so there is
no configuration in which it does not bite.

Worked around here by flattening: `BeginTransaction`/`Commit`/`Rollback` are
three members on `ISqlConnector` rather than a returned transaction object. That
turned out to suit both backends better anyway — ADO already tracked the open
`DbTransaction` on the connection, and the sqlx side issues plain SQL — but it
was not a free choice.

**A generic parameter has the same problem**, and it is the more common one. A
helper as ordinary as

```fsharp
let runIt (name: string) (c: Async<'a>) = printfn "%s" (string (Async.RunSynchronously c))
```

fails with `` `a` cannot be sent between threads safely / required by a bound in
`Async` ``. So the constraint is not "avoid interfaces in asyncs" but "an async
must be concrete in `'T`". Nothing in this library needs a generic one — the
connector's members are all concrete, and `ResultSet.map` is synchronous and
applied after the await — but a generic async data-access helper cannot be
written against the Rust target today.

**Attempted, and reverted.** The fix has to make an interface object and a
generic parameter carry `Send + Sync` under `threaded`, which cannot be written
literally: Fable emits the same declaration whatever features the crate is later
built with, and demanding them without `threaded` (where `Lrc` is `Rc`) would
reject nearly every type. A feature-switched marker trait routed through
`Native_` is the right shape, and Fable had already sketched exactly that —
`makeDefaultTypeBounds` and an `IObject` marker, both commented out in
`Fable2Rust.fs`. Finishing it and measuring:

- **Marker as an interface supertrait only:** 6 errors in Fable's own Rust suite,
  all of them a *generic class* implementing an interface, whose type parameter
  cannot prove the bound.
- **Marker on generic parameters as well:** 752 errors, inside
  `fable-library-rust` itself. Dominated by `dyn Any` — a boxed `obj` can never
  be `Send + Sync` — plus the hand-written `.rs` generics that declare their own
  parameters.

So the real blocker is `obj`: Fable would have to replace `core::any::Any` with
its own trait carrying the auto traits, across the whole runtime. That is a
runtime redesign rather than a bug fix, which is presumably why the sketch was
left commented out.

### G22 — `DateTime.AddTicks` is not mapped on BEAM

`DateTimeOffset.AddTicks` routes to `fable_date_offset:add_ticks`, but the
`DateTime` equivalent is missing from the BEAM replacements even though the
runtime already represents a `DateTime` as `{Ticks, Kind}` and has
`add_milliseconds` next door. So:

```fsharp
DateTime(2026, 8, 27).AddTicks 1234567L
```

fails at compile time with `System.DateTime.AddTicks is not supported by Fable`.

Worked around here by constructing from a tick count instead —
`DateTime(d.Ticks + n)` compiles and runs correctly on every target — so this is
a small, self-contained fix rather than a blocker: one case in the BEAM
`Replacements.fs` and one function in `fable_date.erl`.

### G23 — a fieldless DU matched with unit branches emits an integer pattern

```fsharp
type Kind = A | B
type Holder = { Kind: Kind }

match h.Kind with
| A -> seen <- "a"
| B -> seen <- "b"
```

generates

```rust
match &h.Kind {
    1_i32 => seen.set(string("b")),
    _ => seen.set(string("a")),
};
```

which does not compile: `expected Arc<Kind>, found i32`. Fable produced the
tag-comparison shape it uses for a decision-tree `matchResult` integer, but
against the DU value itself, without projecting the tag from it.

A *value-returning* match over the same DU is fine — `SqlGen.opText` matches a
fourteen-case `BinOp` and returns strings without trouble — and so is equality,
which is why `Dialect.bind`'s `style = Named` always worked. It is the
unit-valued branches that go wrong.

Worked around by asking with equality instead: `if j.Kind = LeftJoin then ..`.

**Fixed** on `fix/rust-exception-and-typetest-codegen`. The `OptionTest` case
directly above the `UnionCaseTest` one already carried the guard and a comment
predicting this exact failure; the union case simply did not have it. Guarding it
leaves such matches to the if/else path, which tests with `if let`.

### G24 — a record field typed as a library class is not boxed in reflection info

```fsharp
type WithSb = { Sb: System.Text.StringBuilder; N: int }
```

Fable emits `recordType(...)` metadata for the record, and the per-field getters
box each field so they can be handed back as `obj`:

```rust
Func1::new(move |o| box_((unbox_lrc::<WithSb>(o)).N.clone())),      // int: boxed
Func1::new(move |o| (unbox_lrc::<WithSb>(o)).Sb.clone()),           // StringBuilder: NOT boxed
```

so the getter's type is `Func1<Arc<dyn Any>, Arc<StringBuilder>>` where
`Func1<Arc<dyn Any>, Arc<dyn Any>>` is required, and the constructor lambda is
missing the matching `unbox_lrc`. The choice of wrapper comes from
`shouldBeRefCountWrapped`, which returns `None` for a type carrying an emit
attribute — correct for the field's own storage, wrong for the boxing that the
reflection info needs.

Any record with a `StringBuilder`, and presumably any other imported library
class, fails to compile. Worked around by keeping the SQL fragments in a
`ResizeArray<string>`, which Fable boxes correctly.

**Fix attempted and backed out.** The obvious change is to widen the
`DeclaredType -> Any` boxing case from `isReferenceRecordOrUnion` to include
`isReferenceClass` (a helper that already exists for this shape). It compiles,
and then breaks the suite in two places, because the erasure is load-bearing:

- `EventTests` produces `box_lrc(box_lrc(..))` — something already boxed gets
  boxed again, and `dyn Any` is not `Sized`.
- `MiscTests` produces `box_lrc(c.clone()).Dispose()` — a cast to `obj` followed
  by a member call, which only resolved because the cast was a no-op.

So a class-to-`obj` cast is deliberately erased in places, and making it box
needs those call sites revisited. Note the `// casts to System.Object` case in
the same match is commented out, which points the same way. This is a
maintainer's design call, not a one-line repair.

### G25 — a guarded nested pattern zero-initialises heap values

```fsharp
type Node = Lit of int | Bin of Op * Node * Node | Many of int[]

match n with
| Lit v -> ...
| Bin(Plus, _, _) when flag -> ...      // nested constructor + guard
| Bin(op, _, _) -> ...
| Many xs -> ...                        // binds an array
```

compiles, then panics the moment it runs:

```text
attempted to zero-initialize type `Arc<MutArray<i32>>`, which is invalid
```

The guard forces Fable to lower the match to its decision-tree form, which
pre-declares a mutable binding for *every* pattern variable in the match and
initialises each one before testing anything. `LrcPtr<T>` variables get
`null::<T>()`, which is safe, but arrays, lists and options get
`Native_::getZero`, whose own comment says what happens:

```rust
pub fn getZero<T>() -> T {
    unsafe { core::mem::zeroed() } // will panic on Rc/Arc/Box
}
```

Both halves are needed. The same match without the guard is fine, and a guard on
a *flat* pattern (`One n when flag`) is fine too — it is a guard on a pattern
that nests a constructor inside another, in a match where some other case binds
a heap value. That is an ordinary shape: the case that found it was
`Binary(Concat, left, right) when b.Vendor = MySql` next to
`InList(inner, values)`.

Worked around by moving the guard's test inside the branch, so the match has no
guard at all.

**Fixed** on the same branch, by giving `makeInit` a valid empty value for the
heap-backed types (`""`, `None`, an empty array, an empty list) instead of
letting them fall through to `getZero`. The function's own comment said these
"don't reach this path in practice"; they do.

### G26 — a `unit` member parameter is dropped at the call but not the declaration

```fsharp
type Builder() =
    member _.Yield(_: unit) = 1
```

generates the member with its parameter and the call without it:

```rust
pub fn Yield(&self, _arg: ()) -> i32 { ... }   // declared with the argument
builder.Yield()                                 // called without it
```

`FSharp2Fable.Util.dropUnitCallArg` runs on the call side (`transformCall`) but
the member declaration keeps the parameter, so the two disagree and the crate
does not compile: *this method takes 1 argument but 0 arguments were supplied*.

It bites any computation expression, because F# calls `builder.Yield(())` for a
block made only of custom operations. Worked around by making the parameter
generic (`member _.Yield(_value: 'T)`), which is not dropped.

**Fix attempted and backed out.** The two sides disagree on three axes, not one.
`dropUnitCallArg` keeps the argument only when *all* of: the argument is a unit
constant, `argTypes` is exactly `[Fable.Unit]`, and the member reports a single
*named* parameter. The declaration side (`makeMemberAssocItem`) has the
parameters but not `argTypes`, and for `member _.M(_: unit)` the parameter turns
out to be *named* while `argTypes` is not `[Unit]` — so mirroring the parameter
rule alone still leaves them disagreeing, which a build confirmed.

Aligning them properly means deciding which side is authoritative, and the
tempting change is in `FSharp2Fable.Util`, which every target shares. Also a
maintainer's call.

### G27 — a string cannot be iterated with `for .. in`

```fsharp
for ch in value do
    ...
```

fails with *no method named `GetEnumerator` found for enum `LrcStr`*. Every
other sequence works; a string is the exception, presumably because `LrcStr` is
not the `Seq` the enumerator machinery expects.

Worked around by indexing (`for i in 0 .. value.Length - 1 do let ch = value.[i]`),
which is what the rest of this library already does to walk a string -- the
decimal and date parsers were written that way before this was known, so it had
gone unnoticed until a new loop was written the natural way.

## Cross-target portability notes (JavaScript and BEAM)

Neither target needed a Fable fix beyond G22. Three divergences did show up while
getting the suites green on them, all in this library rather than in Fable:

1. **`string true`** is `"True"` on .NET and Rust but `"true"` on JavaScript. The
   test harness gained `isTrue` so an assertion never stringifies a boolean.
2. **`DateTime.ToString "o"`** emits seven fractional digits on .NET and Rust and
   three on JavaScript, which goes through `Date.toISOString`. Two backends
   writing different text for the same value defeats the whole point of encoding
   dates as text, so `Convert.dateToText` now formats digit by digit. The
   "date stored as ISO-8601" assertion caught this on the first Node run.
3. **`Async.RunSynchronously` does not exist on JavaScript** — there is no thread
   to block. Only the per-target runners use it, so the shared suites are
   unaffected, but a portable library must not call it.

## Async: can Fable's Rust async drive a tokio database driver?

The largest unknown, since rusqlite is synchronous and every other serious Rust
driver (sqlx, tokio-postgres) is not. Probed with a scratch suite (removed
after the hunt; see git history for `tests/AsyncProbe/`) that stands in for a
query with a 20ms tokio timer sleep so elapsed time separates real concurrency
from serialised awaits.

**Answer: yes, through a channel bridge — but Fable's async layer is expensive
and its Task bridge is unusable.**

Three ways of connecting the two were tried:

| Approach | Result |
|---|---|
| Block on tokio inside the shim, F# stays synchronous | Works. Costs a parked thread per call, no concurrency from F#. |
| Hand a tokio future straight to Fable's `Async<T>` | **Fails** — `there is no reactor running, must be called from the context of a Tokio 1.x runtime`. Fable polls with `futures::executor`, which has no tokio reactor. |
| `tokio::spawn` the work, bridge completion over a `futures::channel::oneshot`, wrap the receiver in `Async<T>` | **Works.** tokio drives the work on its own threads; Fable only ever polls a receiver, which needs no reactor. |

The third is the one to build on. `let! rows = query ...` inside an ordinary F#
`async { }` works, and the tokio side really is concurrent.

### Measurements

Each op is 20ms of work. Stable across runs except where noted.

```
one await:          31ms      (20ms of work + ~11ms overhead)
three sequential:   94ms      (3 x 31ms, as expected)
three pre-spawned:  23ms      <- all three overlap: real concurrency
three via StartAsTask: 403ms / 3014ms / 3614ms   <- unusable, nondeterministic
```

Three findings fall out:

- **G19 was wrong and is retracted.** A micro-benchmark with no tokio in it puts
  ten sequential `let!` at **0ms** on Rust against 2ms on .NET — the binder uses
  a proper async lock and never sleeps. The ~11ms came from the oneshot bridge
  in the probe itself, not from Fable. The sleeps that did exist were in
  `Task::poll` (100ms), `Task::get_result` (10ms) and `Async::poll`'s
  contention path (10ms); all three are gone.
- **G17 — `Async.StartAsTask` was unusable, and is fixed.** Awaiting a task
  found it `Running` and slept 100ms before looking again. Tasks now keep the
  wakers of whoever awaits them. Three 50ms sleeps: **2718ms before, 65ms after**,
  against .NET's 65ms.
- **G18 — `Async.Parallel` is implemented.** Replacements already mapped it;
  only the runtime function was missing. Three 50ms sleeps in 66ms, results in
  order. `Async.StartImmediateAsTask` was in the same position and is added too.

### What this means for the design

Concurrency has to come from the **native side**, not from F#. Spawning three
operations before awaiting any of them completed all three in 23ms; expressing
the same thing through `Async.StartAsTask` cost two orders of magnitude more.

So the connector should keep F# awaits few and coarse — batch at the native
boundary and let Rust spawn — rather than exposing a fine-grained async API that
charges ~11ms per await. An async Postgres backend is viable on that shape. It
is not viable if every row-level operation is its own `let!`.

## Suggested PR grouping

Five PRs, ordered so nothing depends on a later one:

1. **Exception handling** — G1, G2, G16. Started as "two compiler crashes" and
   turned out to be four layered defects sharing one root cause. D1 and D2 are
   done and verified (2657 tests green) on branch
   `fix/rust-makerecord-base-field`; D3 and G16 remain. Highest value: a crash
   with no source location is the worst failure mode to leave in, and G16 — the
   silently-wrong branch it was hiding — is arguably worse.
2. **Quotation completeness** — G3, G12, plus G4 if the fix lands in the same
   area. This is the one that decides whether a query DSL is possible at all.
   G3 is target-independent, so it needs sign-off beyond the Rust backend.
3. **Codegen correctness** — G7, G8, G9. All three are "the emitted Rust does
   not compile", all three were hit by a single ~200-line connector, and all
   three have workarounds, which makes them good candidates to fix together
   with regression tests derived from that connector.
4. **Runtime semantics** — G10, G11, G15. Silent wrong behaviour or no way to
   avoid it. G15 is arguably the most user-visible of the set: without it a
   library cannot read back its own locale-independent output. G10 is a small
   targeted fix; G11 is a known design gap that may be a larger conversation.
5. **Async runtime** — G17, G18, G19. Only worth doing if an async vendor
   (Postgres, MySQL) matters; SQLite needs none of it. G19 is the root of G17
   and is acknowledged in Fable's own source, so it is the one to raise first.
   Until it lands, keep F# awaits coarse and let Rust own the concurrency.

G5, G6, G13, G14 are formatting polish — worth filing, not worth blocking on.

## Reproducing

The scratch projects the hunt was run with — `tests/RustProbe/` (the probe
suite, 109/109 on .NET), `tests/AsyncProbe/` (the tokio question),
`tests/AsyncBench/` (async timing) and `tests/Iso/` (single-snippet
minimisation) — were removed once the gaps were catalogued; recover any of
them from git history. Each gap's entry above carries its own minimal repro,
and the regression tests that matter live in the Fable branches themselves.

## Pre-PR review notes

A read-through of the four branches before they go anywhere, with everything
checked against a build rather than against the commit messages.

- **G15 was overstated.** The commit claimed `DateTime.Parse(s, provider)` was
  among the broken overloads. It was not: compiled against a build without the
  fix, it works, because it reaches `ignoreFormatProvider` through
  `makeMemberCall`. The original probe did report an error on that line, but it
  was a cascade from the two decimal errors above it. Corrected; the fix is
  decimal-only.
- **`dropCultureArgs` leaves `SignatureArgTypes` at its original length.** Not a
  correctness bug — `transformCallArgs` guards with
  `if argTypes.Length = args.Length`, so a mismatch loses per-argument type
  hints rather than crashing — but it is worth tidying. A version that filters
  the signature types alongside the arguments was written and then reverted
  unverified: Application Control began blocking every freshly built
  `Fable.AST.dll`, so the compiler could no longer be rebuilt to test it. The
  branch carries the version that has actually been run.
- The `makeRecord` base-field guard was re-checked against zero-field,
  single-field and n-field exceptions, and against the case where a value list
  already carries a base: the length test falls through correctly in each.
- **`global.json` had been committed** on the quotation branch by an earlier
  `git add -A`, changing the SDK pin for everyone. Caught by diffing the file
  list per branch, and amended out.

## CI lesson: the no_std matrix leg

The first branch pushed as a PR failed CI on `build-rust (no_std)` — the one
matrix leg that cannot be built on Windows at all (`no_std` fails to link
against MSVC with `LNK1181: cannot open input file 'c.lib'`), so nothing local
had exercised it.

The cause was in the added test, not the fix. Under `no_std` the Rust library's
`try_catch` **does not catch**:

```rust
#[cfg(feature = "no_std")]
pub fn try_catch<F, G, R>(try_f: F, catch_f: G) -> R { try_f() }
```

so a test that raises and expects a `with` handler to run just panics. Fable's
own suite handles this by compiling the exception helpers in
`tests/Rust/tests/common/Util.fs` down to no-ops under `NO_STD_NO_EXCEPTIONS`,
and by guarding whole tests with `#if !NO_STD_NO_EXCEPTIONS` where they depend
on catching.

Fixed by splitting the test: the exception-free `try/finally` case still
exercises the mutable-capture mechanism and runs on every leg, and the two
variants that need a working `with` sit behind the guard.

**Running the matrix locally** needs Linux for `no_std`. With the Rust toolchain
installed in WSL, compile the tests with `--define NO_STD_NO_EXCEPTIONS` and then
from WSL run `cargo test --features no_std -- --test-threads=1`. All four
branches were re-checked this way afterwards; the branch in the PR was also run
through `lrc_ptr`, `threaded` and the default leg on Linux.

Note CI runs the suite in **debug**, which is why it never sees the release-mode
flake in InteropTests' "Bindings with Emit on interfaces works".
