namespace SQLProvider.Fable

type BinOp =
    | Eq
    | Ne
    | Gt
    | Ge
    | Lt
    | Le
    | And
    | Or
    | Add
    | Sub
    | Mul
    | Div
    | Concat
    | Like

/// The .NET functions SQLProvider translates into SQL. Named after the .NET
/// side, because that is what the caller writes; each vendor's spelling is
/// decided when the SQL is generated.
type SqlFn =
    | Upper
    | Lower
    | Trim
    | Length
    | Substring
    | Replace
    | IndexOf
    | Abs
    | Year
    | Month
    | Day
    | Hour
    | Minute
    | Second
    /// `.Date` -- the day, with the time dropped.
    | DateOnly
    // The numeric functions below avoid anything SQLite only has when compiled
    // with SQLITE_ENABLE_MATH_FUNCTIONS -- the sqlx build ships without it, so
    // CEIL and friends are spelled with CAST arithmetic there instead. That is
    // also why there is no Sqrt or Pow: they have no arithmetic spelling.
    | Ceiling
    | Floor
    | Round
    /// Round to a fixed number of decimal places. The count is part of the
    /// statement, not a parameter: PostgreSQL resolves `ROUND(numeric, int)`
    /// by literal type, and it is an F# int the caller wrote, not data.
    | RoundTo of digits: int
    /// Toward zero, like `Math.Truncate`.
    | Truncate
    /// The larger of two values, `GREATEST` -- 2-argument `MAX` on SQLite.
    | Greatest
    | Least
    /// `CAST(x AS TEXT)`, `.ToString()` done by the database.
    | CastText
    /// `CAST(x AS INTEGER)`.
    | CastInt
    /// Whole calendar days between two dates, first minus second.
    | DateDiffDays
    /// Whole seconds between two date-times, first minus second.
    | DateDiffSecs

/// The unit a date is shifted by. Constant amounts only: SQLite spells the
/// shift as a string literal and PostgreSQL as an interval literal, so neither
/// takes a bound parameter there -- which is the same limit SQLProvider
/// documents for most of these.
type DatePart =
    | Years
    | Months
    | Days
    | Hours
    | Minutes
    | Seconds

type Agg =
    | AggCount
    | AggSum
    | AggAvg
    | AggMin
    | AggMax
    /// `COUNT(DISTINCT x)`. Always carries an operand; there is no distinct
    /// count of `*`.
    | AggCountDistinct

type Order =
    | Asc
    | Desc

type JoinKind =
    | InnerJoin
    | LeftJoin

/// How two SELECTs are combined into one result.
///
/// `Union` deduplicates and `UnionAll` keeps everything, which is the
/// `.Union()` / `.Concat()` split on IQueryable. MySQL grew INTERSECT and
/// EXCEPT in 8.0.31; the older servers reject them, and so does this library's
/// SQL only at the server, not at render time.
type SetOp =
    | Union
    | UnionAll
    | Intersect
    | Except

type Source = { Table: string; Alias: string }

// Mutually recursive from here: an expression can hold a query (a subquery) and
// a query is built out of expressions.
type SqlExpr =
    | ColumnRef of table: string * column: string
    | Literal of SqlValue
    | Binary of BinOp * SqlExpr * SqlExpr
    | Not of SqlExpr
    | Call of SqlFn * SqlExpr list
    | InList of SqlExpr * SqlValue[]
    | IsNull of SqlExpr
    | IsNotNull of SqlExpr
    /// An aggregate. `None` is `COUNT(*)`, the only one with no operand.
    | Aggregate of Agg * SqlExpr option
    /// `x IN (SELECT ..)`. The subquery has to project exactly one column.
    | InQuery of SqlExpr * Query
    /// `EXISTS (SELECT ..)`. The subquery's projection is irrelevant.
    | ExistsQuery of Query
    /// A subquery used where a value is expected, `(SELECT ..)`.
    | ScalarQuery of Query
    /// `x LIKE @p ESCAPE ''`, where the pattern was built from a value whose
    /// wildcards have been escaped. Separate from `Binary(Like, ..)`, which
    /// passes a pattern through as written.
    | LikeEscaped of SqlExpr * SqlValue
    /// Shifts a date by a constant amount.
    | DateAdd of DatePart * amount: int * SqlExpr
    /// `CASE WHEN c THEN v .. ELSE e END`.
    | Case of branches: (SqlExpr * SqlExpr) list * elseValue: SqlExpr option

and Join =
    { Kind: JoinKind
      Source: Source
      On: SqlExpr }

and SelectItem =
    {
        Expr: SqlExpr
        /// The name the column comes back under, and therefore the name `Row`
        /// readers look it up by.
        Alias: string
    }

and Projection =
    /// `SELECT *`. Every column of every source, in whatever order the engine
    /// gives them.
    | All
    | Items of SelectItem[]

/// A SELECT, as data.
///
/// Deliberately a record rather than a fluent object: it can be built, stored,
/// passed around and composed before anything is executed, which is what makes
/// SQLProvider's composable queries work, and it can be rendered for a
/// different vendor than the one that built it -- and, now, nested inside
/// another query's WHERE clause.
and Query =
    {
        From: Source
        Joins: Join[]
        Where: SqlExpr option
        GroupBy: SqlExpr[]
        Having: SqlExpr option
        OrderBy: (SqlExpr * Order)[]
        Skip: int option
        Take: int option
        Distinct: bool
        Select: Projection
        /// Further SELECTs this one is combined with, in order: `UNION`,
        /// `UNION ALL`, `INTERSECT`, `EXCEPT`. When any are present, this
        /// query's OrderBy/Skip/Take apply to the combined result -- that is
        /// what the SQL standard says they mean there -- and the added arms
        /// must not carry their own.
        Compounds: (SetOp * Query)[]
    }

/// A typed reference to one column.
///
/// The type parameter is carried by `Encode`, which is what makes
/// `Customer.Country == "UK"` type-check and `Customer.Country == 3` not.
/// It also does the conversion, so a column knows how its values reach the
/// driver without the comparison operators needing to know anything.
type Column<'T> =
    { Table: string
      Name: string
      Encode: 'T -> SqlValue }

    /// The column as an expression. Short because it appears everywhere a
    /// column is used where an untyped expression is wanted.
    member c.E = ColumnRef(c.Table, c.Name)

    // Static members rather than let-bound operators so they overload:
    // `Customer.Country == "UK"` compares against a value and
    // `Customer.CustomerId == Order.CustomerId` compares two columns, spelled
    // the same way. A let-bound operator can only have one signature, which is
    // what used to force `Expr.eq (Col.ref a) (Col.ref b)` on every join.
    //
    // F#'s own `=` cannot be used: it is fixed at `'a -> 'a -> bool` and does
    // not dispatch to a type's operators, so a comparison that builds an
    // expression instead of a bool needs its own name. `==` and `!=` are free
    // in F#; the ordering ones take a trailing dot to stay clear of the real
    // `>` and `<`.
    static member (==)(c: Column<'T>, v: 'T) = Binary(Eq, c.E, Literal(c.Encode v))
    static member (==)(c: Column<'T>, other: Column<'T>) = Binary(Eq, c.E, other.E)
    static member (!=)(c: Column<'T>, v: 'T) = Binary(Ne, c.E, Literal(c.Encode v))
    static member (!=)(c: Column<'T>, other: Column<'T>) = Binary(Ne, c.E, other.E)
    static member (>.)(c: Column<'T>, v: 'T) = Binary(Gt, c.E, Literal(c.Encode v))
    static member (>.)(c: Column<'T>, other: Column<'T>) = Binary(Gt, c.E, other.E)
    static member (>=.)(c: Column<'T>, v: 'T) = Binary(Ge, c.E, Literal(c.Encode v))
    static member (>=.)(c: Column<'T>, other: Column<'T>) = Binary(Ge, c.E, other.E)
    static member (<.)(c: Column<'T>, v: 'T) = Binary(Lt, c.E, Literal(c.Encode v))
    static member (<.)(c: Column<'T>, other: Column<'T>) = Binary(Lt, c.E, other.E)
    static member (<=.)(c: Column<'T>, v: 'T) = Binary(Le, c.E, Literal(c.Encode v))
    static member (<=.)(c: Column<'T>, other: Column<'T>) = Binary(Le, c.E, other.E)

/// Building typed columns. A schema code generator emits one of these per
/// column; they are equally usable by hand.
module Col =

    let private make table name encode : Column<'T> =
        { Table = table
          Name = name
          Encode = encode }

    /// Captured before the column helpers below shadow the built-in conversion
    /// of the same name.
    let private widen (v: int) : int64 = int64 v

    let int64 table name : Column<int64> = make table name SqlInt

    let int table name : Column<int> =
        make table name (widen >> SqlInt)

    let text table name : Column<string> = make table name SqlText
    let float table name : Column<float> = make table name SqlFloat
    let bool table name : Column<bool> = make table name SqlBool
    let decimal table name : Column<decimal> = make table name SqlDecimal
    let date table name : Column<System.DateTime> = make table name SqlDate
    let guid table name : Column<System.Guid> = make table name SqlGuid
    let blob table name : Column<byte[]> = make table name SqlBlob

    /// The column as an expression, for the places that take one.
    let ref (c: Column<'T>) = ColumnRef(c.Table, c.Name)

    /// Re-points a column at a different alias, for joining a table to itself.
    let onAlias (alias: string) (c: Column<'T>) : Column<'T> = { c with Table = alias }

/// Untyped expression building, for everything the typed operators do not
/// cover: comparing two columns, calling a function, nesting arithmetic.
module Expr =

    let col (c: Column<'T>) = Col.ref c
    let value (v: SqlValue) = Literal v

    let eq a b = Binary(Eq, a, b)
    let ne a b = Binary(Ne, a, b)
    let gt a b = Binary(Gt, a, b)
    let ge a b = Binary(Ge, a, b)
    let lt a b = Binary(Lt, a, b)
    let le a b = Binary(Le, a, b)
    let add a b = Binary(Add, a, b)
    let sub a b = Binary(Sub, a, b)
    let mul a b = Binary(Mul, a, b)
    let div a b = Binary(Div, a, b)
    let concat a b = Binary(Concat, a, b)

    let andAlso a b = Binary(And, a, b)
    let orElse a b = Binary(Or, a, b)
    let not' a = Not a

    let upper a = Call(Upper, [ a ])
    let lower a = Call(Lower, [ a ])
    let trim a = Call(Trim, [ a ])
    let length a = Call(Length, [ a ])
    let abs a = Call(Abs, [ a ])
    let ceiling a = Call(Ceiling, [ a ])
    let floor a = Call(Floor, [ a ])

    /// Rounds to the nearest whole number.
    let round a = Call(Round, [ a ])

    /// Rounds to `digits` decimal places.
    let roundTo (digits: int) a = Call(RoundTo digits, [ a ])

    /// Toward zero, like `Math.Truncate`.
    let truncate a = Call(Truncate, [ a ])

    /// The larger of the two, per row -- `Math.Max` done by the database.
    let greatest a b = Call(Greatest, [ a; b ])

    let least a b = Call(Least, [ a; b ])

    /// `.ToString()` done by the database, `CAST(x AS TEXT)`.
    let castText a = Call(CastText, [ a ])

    /// `CAST(x AS INTEGER)`.
    let castInt a = Call(CastInt, [ a ])

    /// Whole calendar days from `b` to `a` (a minus b), so a date column minus
    /// an earlier one is positive.
    let dateDiffDays a b = Call(DateDiffDays, [ a; b ])

    /// Whole seconds from `b` to `a` (a minus b).
    let dateDiffSecs a b = Call(DateDiffSecs, [ a; b ])

    let isNull a = IsNull a
    let isNotNull a = IsNotNull a

    /// `COUNT(*)`.
    let count = Aggregate(AggCount, None)
    let countOf a = Aggregate(AggCount, Some a)

    /// `COUNT(DISTINCT x)`.
    let countDistinct a = Aggregate(AggCountDistinct, Some a)

    /// Substring from `start` to the end. The three-argument form is
    /// `substring`.
    let substringFrom a start = Call(Substring, [ a; start ])
    let sum a = Aggregate(AggSum, Some a)
    let avg a = Aggregate(AggAvg, Some a)
    let min a = Aggregate(AggMin, Some a)
    let max a = Aggregate(AggMax, Some a)
    let substring a start len = Call(Substring, [ a; start; len ])
    let replace a find repl = Call(Replace, [ a; find; repl ])
    let indexOf a needle = Call(IndexOf, [ a; needle ])
    let year a = Call(Year, [ a ])
    let month a = Call(Month, [ a ])
    let day a = Call(Day, [ a ])
    let hour a = Call(Hour, [ a ])
    let minute a = Call(Minute, [ a ])
    let second a = Call(Second, [ a ])

    /// Escapes the LIKE wildcards in a value, so a search for a literal `%` or
    /// `_` finds one instead of matching everything.
    ///
    /// This is why `contains` and friends exist rather than leaving callers to
    /// build patterns: user input reaching an unescaped LIKE is a quiet
    /// wrong-results bug, not a loud one.
    /// The character marking the next one as literal inside a LIKE pattern.
    ///
    /// Not a backslash, which would be the obvious pick: MySQL treats a
    /// backslash as an escape *inside string literals* too, so `ESCAPE '\'`
    /// would escape its own closing quote there. `!` is ordinary in all three
    /// engines' string literals.
    [<Literal>]
    let likeEscapeChar = '!'

    let escapeLikeValue (value: string) =
        let parts = ResizeArray<string>()

        // Indexed rather than `for ch in value`: a string has no enumerator on
        // Fable's Rust target (G27), and indexing is what the rest of this
        // library already uses to walk one.
        for ch in value do

            if ch = '%' || ch = '_' || ch = likeEscapeChar then
                parts.Add(string likeEscapeChar)

            parts.Add(string ch)

        String.concat "" (parts.ToArray())

    /// `LIKE '%value%'`, with the value's own wildcards escaped.
    let contains (e: SqlExpr) (value: string) =
        LikeEscaped(e, SqlText("%" + escapeLikeValue value + "%"))

    let startsWith (e: SqlExpr) (value: string) =
        LikeEscaped(e, SqlText(escapeLikeValue value + "%"))

    let endsWith (e: SqlExpr) (value: string) =
        LikeEscaped(e, SqlText("%" + escapeLikeValue value))

    /// `.Date` -- the day, with the time dropped.
    let dateOnly e = Call(DateOnly, [ e ])

    let addYears (e: SqlExpr) (n: int) = DateAdd(Years, n, e)
    let addMonths (e: SqlExpr) (n: int) = DateAdd(Months, n, e)
    let addDays (e: SqlExpr) (n: int) = DateAdd(Days, n, e)
    let addHours (e: SqlExpr) (n: int) = DateAdd(Hours, n, e)
    let addMinutes (e: SqlExpr) (n: int) = DateAdd(Minutes, n, e)
    let addSeconds (e: SqlExpr) (n: int) = DateAdd(Seconds, n, e)

    /// `CASE WHEN .. THEN .. ELSE .. END`, which is what SQLProvider's `if`
    /// inside a select becomes when the work is done database-side.
    let caseWhen (branches: (SqlExpr * SqlExpr) list) (elseValue: SqlExpr option) = Case(branches, elseValue)

    let ifThenElse (condition: SqlExpr) (thenValue: SqlExpr) (elseValue: SqlExpr) =
        Case([ condition, thenValue ], Some elseValue)

    /// `x IN (SELECT ..)`. The subquery must project exactly one column;
    /// `Query.select [| .. |]` on it is what does that.
    let inQuery (e: SqlExpr) (q: Query) = InQuery(e, q)

    /// `EXISTS (SELECT ..)`. Correlate it by referring to the outer table's
    /// columns inside the subquery's WHERE.
    let exists (q: Query) = ExistsQuery q

    let notExists (q: Query) = Not(ExistsQuery q)

    /// A subquery in value position, `(SELECT ..)`. It has to yield one row and
    /// one column.
    let scalarQuery (q: Query) = ScalarQuery q

    /// Folds a list of conditions into one AND, or None when there is nothing
    /// to say. Used to combine successive `where` calls.
    let all (conditions: SqlExpr list) =
        match conditions with
        | [] -> None
        | first :: rest -> Some(rest |> List.fold andAlso first)

    let any (conditions: SqlExpr list) =
        match conditions with
        | [] -> None
        | first :: rest -> Some(rest |> List.fold orElse first)

/// The comparison operators.
///
/// Doubled dots on both sides (`.=.`) so they never collide with F#'s own
/// operators or with the `.[ ]` indexer syntax. `=%` and `|=|` keep the
/// spellings SQLProvider already uses for LIKE and IN.
///
/// **`.&&.` and `.||.` need parentheses around each comparison.** F# gives a
/// custom operator its precedence from its leading characters, and every
/// operator starting with `=`, `<`, `>`, `|` or `&` lands in one
/// left-associative group -- unlike the built-in `&&`, which sits below the
/// comparisons. So this misparses:
///
///     Balance >. 100.0 .||. Balance <. 10.0     // ((a >. b) .||. c) <. d
///
/// and this is what to write:
///
///     (Balance >. 100.0) .||. (Balance <. 10.0)
///
/// Most queries never hit it, because successive `Query.where` calls are
/// already ANDed and `Expr.all`/`Expr.any` combine a list without an operator.
[<AutoOpen>]
module Operators =

    // The comparisons live on `Column<'T>` itself so they can overload; see the
    // note there. What is left here is what does not need to.

    let (.&&.) a b = Binary(And, a, b)
    let (.||.) a b = Binary(Or, a, b)

    /// LIKE, with the pattern written out: `Customer.Name =% "A%"`. Use
    /// `Expr.contains`/`startsWith`/`endsWith` for a value rather than a
    /// pattern -- they escape its wildcards.
    let (=%) (c: Column<string>) (pattern: string) =
        Binary(Like, c.E, Literal(SqlText pattern))

    /// IN, over a set of values: `Customer.Country |=| [| "UK"; "USA" |]`.
    let (|=|) (c: Column<'T>) (values: 'T[]) =
        InList(c.E, values |> Array.map c.Encode)

    /// IN, over a subquery: `Order.CustomerId |=? ukCustomerIds`. The subquery
    /// must project exactly one column.
    let (|=?) (c: Column<'T>) (subquery: Query) = InQuery(c.E, subquery)

module Query =

    /// A query with no source yet. Only the computation expression uses this,
    /// as the state that `from` replaces; rendering one is an error rather
    /// than a FROM with nothing after it.
    let blank: Query =
        { From = { Table = ""; Alias = "" }
          Joins = [||]
          Where = None
          GroupBy = [||]
          Having = None
          OrderBy = [||]
          Skip = None
          Take = None
          Distinct = false
          Select = All
          Compounds = [||] }

    /// A query over one table, selecting everything. The alias defaults to the
    /// table name, which is what an unaliased query wants.
    let from (table: string) : Query =
        { From = { Table = table; Alias = table }
          Joins = [||]
          Where = None
          GroupBy = [||]
          Having = None
          OrderBy = [||]
          Skip = None
          Take = None
          Distinct = false
          Select = All
          Compounds = [||] }

    /// The same, under an explicit alias -- needed to join a table to itself.
    let fromAs (table: string) (alias: string) : Query =
        { from table with
            From = { Table = table; Alias = alias } }

    /// Adds a condition. Successive calls are ANDed, so a query can be built up
    /// a clause at a time and still mean what it reads like.
    let where (condition: SqlExpr) (q: Query) =
        { q with
            Where =
                match q.Where with
                | None -> Some condition
                | Some existing -> Some(Binary(And, existing, condition)) }

    /// Adds every condition, ANDed. Equivalent to a chain of `where` calls, and
    /// the way to combine conditions without reaching for `.&&.` and its
    /// parentheses.
    let whereAll (conditions: SqlExpr list) (q: Query) =
        conditions |> List.fold (fun acc c -> where c acc) q

    /// Adds one condition matching any of the alternatives.
    let whereAny (conditions: SqlExpr list) (q: Query) =
        match Expr.any conditions with
        | None -> q
        | Some combined -> where combined q

    /// Adds a condition with OR instead.
    let orWhere (condition: SqlExpr) (q: Query) =
        { q with
            Where =
                match q.Where with
                | None -> Some condition
                | Some existing -> Some(Binary(Or, existing, condition)) }

    let private addJoin kind table alias on (q: Query) =
        { q with
            Joins =
                Array.append
                    q.Joins
                    [| { Kind = kind
                         Source = { Table = table; Alias = alias }
                         On = on } |] }

    let join (table: string) (on: SqlExpr) (q: Query) = addJoin InnerJoin table table on q

    let joinAs (table: string) (alias: string) (on: SqlExpr) (q: Query) = addJoin InnerJoin table alias on q

    let leftJoin (table: string) (on: SqlExpr) (q: Query) = addJoin LeftJoin table table on q

    let leftJoinAs (table: string) (alias: string) (on: SqlExpr) (q: Query) = addJoin LeftJoin table alias on q

    let private addOrder e dir (q: Query) =
        { q with
            OrderBy = Array.append q.OrderBy [| (e, dir) |] }

    let orderBy (c: Column<'T>) (q: Query) = addOrder (Col.ref c) Asc q
    let orderByDesc (c: Column<'T>) (q: Query) = addOrder (Col.ref c) Desc q

    /// Successive `orderBy` calls already chain, so these are the same thing
    /// under the name that reads better after the first one.
    let thenBy (c: Column<'T>) (q: Query) = orderBy c q
    let thenByDesc (c: Column<'T>) (q: Query) = orderByDesc c q

    let orderByExpr (e: SqlExpr) (q: Query) = addOrder e Asc q
    let orderByExprDesc (e: SqlExpr) (q: Query) = addOrder e Desc q

    /// SQLProvider's `contains`: true when the subquery yields the value.
    let containsValue (c: Column<'T>) (value: 'T) (subquery: Query) =
        InQuery(Literal(c.Encode value), subquery)

    /// SQLProvider's `all`: true when every row of `subquery` satisfies
    /// `condition`. There is no ALL in SQL, so it is asked the other way round
    /// -- no row fails the condition -- which is also how SQLProvider renders it.
    let allSatisfy (condition: SqlExpr) (subquery: Query) =
        Not(ExistsQuery(where (Not condition) subquery))

    let skip (n: int) (q: Query) = { q with Skip = Some n }
    let take (n: int) (q: Query) = { q with Take = Some n }
    let distinct (q: Query) = { q with Distinct = true }

    let private addCompound (op: SetOp) (other: Query) (q: Query) =
        { q with
            Compounds = Array.append q.Compounds [| op, other |] }

    /// Combines with another query, deduplicating -- `.Union()`. The two must
    /// project the same number of columns; the result's column names come from
    /// this query, and any `orderBy`/`skip`/`take` on this query order and page
    /// the combined result.
    ///
    /// Every *other* clause -- `where`, `groupBy`, `select` and the rest --
    /// still describes the first SELECT, whether written before or after this
    /// call: SQL has no way to filter a combined result without nesting it in
    /// a FROM, which this library does not do. Filter the arms, not the union.
    let union (other: Query) (q: Query) = addCompound Union other q

    /// `UNION ALL` -- `.Concat()`: everything from both, duplicates kept.
    let unionAll (other: Query) (q: Query) = addCompound UnionAll other q

    /// Rows present in both -- `.Intersect()`. MySQL only from 8.0.31.
    let intersect (other: Query) (q: Query) = addCompound Intersect other q

    /// Rows of this query not present in the other -- `.Except()`. MySQL only
    /// from 8.0.31.
    let except (other: Query) (q: Query) = addCompound Except other q

    let groupBy (columns: SqlExpr[]) (q: Query) = { q with GroupBy = columns }

    let having (condition: SqlExpr) (q: Query) = { q with Having = Some condition }

    /// Projects specific columns. The alias each one comes back under is its own
    /// name, so `Row.text row "Name"` keeps working.
    let select (columns: SqlExpr[]) (q: Query) =
        { q with
            Select =
                columns
                |> Array.mapi (fun i e ->
                    { Expr = e
                      // A plain column keeps its own name, so `Row.text row
                      // "Name"` still finds it. Anything computed gets a
                      // positional alias, which at least cannot collide.
                      Alias =
                        match e with
                        | ColumnRef(_, c) -> c
                        | _ -> "expr" + string i })
                |> Items }

    /// Adds one column to the projection, keeping whatever is already there.
    /// Chainable, so a projection can be built a column at a time without
    /// spelling out an array of `.E`.
    let selectCol (c: Column<'T>) (q: Query) =
        let item = { Expr = c.E; Alias = c.Name }

        { q with
            Select =
                match q.Select with
                | All -> Items [| item |]
                | Items items -> Items(Array.append items [| item |]) }

    /// Adds one named expression to the projection. The name is what `Row`
    /// readers look the value up by.
    let selectExpr (alias: string) (e: SqlExpr) (q: Query) =
        let item = { Expr = e; Alias = alias }

        { q with
            Select =
                match q.Select with
                | All -> Items [| item |]
                | Items items -> Items(Array.append items [| item |]) }

    /// Groups by one more column.
    let groupByCol (c: Column<'T>) (q: Query) =
        { q with
            GroupBy = Array.append q.GroupBy [| c.E |] }

    /// Projects named expressions, for aggregates and computed columns.
    let selectAs (items: (string * SqlExpr)[]) (q: Query) =
        { q with
            Select = items |> Array.map (fun (alias, e) -> { Expr = e; Alias = alias }) |> Items }

    /// Replaces the projection with one expression under a known alias. The
    /// aggregate helpers below are all this with a different expression, and the
    /// alias is fixed so the caller can read the value back by name.
    let selectOne (alias: string) (e: SqlExpr) (q: Query) =
        { q with
            Select = Items [| { Expr = e; Alias = alias } |] }

    /// Makes a query safe to collapse to one aggregate row.
    ///
    /// Ordering changes nothing about a single row, and some engines reject
    /// ORDER BY in an aggregate select. Paging is dropped because LIMIT and
    /// OFFSET apply *after* aggregation -- a kept Skip would page past the one
    /// result row and the aggregate would quietly come back as no rows at all.
    ///
    /// A GROUP BY is refused outright rather than adjusted: a grouped query has
    /// one aggregate value per group, and reading it as a scalar would take
    /// whichever group the engine returned first. Put the aggregate in the
    /// projection (`selectAs`) and read the rows instead.
    let private forAggregate (what: string) (q: Query) =
        if q.GroupBy.Length > 0 then
            failwith (
                what
                + ": the query has a GROUP BY, so there is one value per group rather than one value -- put the aggregate in the projection with selectAs and read the rows"
            )

        if q.Compounds.Length > 0 then
            failwith (
                what
                + ": the query is a UNION/INTERSECT/EXCEPT, and replacing its projection with an aggregate would aggregate only the first arm"
            )

        { q with
            OrderBy = [||]
            Skip = None
            Take = None }

    /// `SELECT COUNT(*)` -- or `COUNT(DISTINCT x)` for a `distinct` query that
    /// projects exactly one column, which is the only distinct count the
    /// engines can express without a nested FROM.
    let countQuery (q: Query) =
        let q = forAggregate "countQuery" q

        if q.Distinct then
            match q.Select with
            | Items items when items.Length = 1 ->
                { q with Distinct = false }
                |> selectOne "count" (Expr.countDistinct items.[0].Expr)
            | _ ->
                failwith
                    "countQuery: counting a DISTINCT query needs exactly one selected column, so it can become COUNT(DISTINCT x)"
        else
            q |> selectOne "count" Expr.count

    let sumQuery (c: Column<'T>) (q: Query) =
        forAggregate "sumQuery" q |> selectOne "sum" (Expr.sum (Col.ref c))

    let avgQuery (c: Column<'T>) (q: Query) =
        forAggregate "avgQuery" q |> selectOne "avg" (Expr.avg (Col.ref c))

    let minQuery (c: Column<'T>) (q: Query) =
        forAggregate "minQuery" q |> selectOne "min" (Expr.min (Col.ref c))

    let maxQuery (c: Column<'T>) (q: Query) =
        forAggregate "maxQuery" q |> selectOne "max" (Expr.max (Col.ref c))
