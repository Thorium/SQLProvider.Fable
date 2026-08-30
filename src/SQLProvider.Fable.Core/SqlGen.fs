namespace SQLProvider.Fable

/// Renders a `Query` to SQL text plus bound parameters.
///
/// Output always uses `@name` placeholders; `Dialect.bind` rewrites those into
/// whatever the target driver wants, so this stage never has to know. Every
/// literal becomes a parameter — nothing a caller supplies is ever concatenated
/// into the SQL text.
///
/// Identifiers are emitted **unquoted**. That is deliberate rather than lazy:
/// PostgreSQL folds an unquoted identifier to lower case at DDL time, so
/// quoting `"CustomerId"` on a table created as `CustomerId` fails to find the
/// column. Quoting is only correct when the names come from the schema with
/// their real case, which is a property of generated code and not of a query
/// written by hand. Until the code generator lands, unquoted is the option that
/// works on all three engines.
module SqlGen =

    /// Accumulates the SQL text and the parameters a render produces, numbering
    /// the parameters in the order they are met.
    ///
    /// The text is a list of fragments rather than a StringBuilder because a
    /// record field whose type is an imported library class is not boxed in the
    /// reflection info Fable emits for the record, and the generated Rust then
    /// does not compile (G24 in GAPS.md).
    type private Builder =
        {
            Parts: ResizeArray<string>
            Params: ResizeArray<SqlParam>
            Vendor: Vendor
            /// Whether a column is written as `Table.Column` or bare.
            ///
            /// A SELECT qualifies, because it can have several sources. An
            /// UPDATE or DELETE has exactly one and must not: SQLite rejects a
            /// qualified name in an UPDATE's SET list outright, and leaving it
            /// off is accepted by every engine.
            Qualify: bool
        }

    let private write (b: Builder) (s: string) = b.Parts.Add s

    let private addParam (b: Builder) (v: SqlValue) =
        let name = "p" + string b.Params.Count
        b.Params.Add { Name = name; Value = v }
        write b ("@" + name)

    let private opText (op: BinOp) =
        match op with
        | Eq -> " = "
        | Ne -> " <> "
        | Gt -> " > "
        | Ge -> " >= "
        | Lt -> " < "
        | Le -> " <= "
        | And -> " AND "
        | Or -> " OR "
        | Add -> " + "
        | Sub -> " - "
        | Mul -> " * "
        | Div -> " / "
        | Like -> " LIKE "
        | Concat -> " || " // replaced for MySQL, which spells it CONCAT

    let private aggText (agg: Agg) =
        match agg with
        | AggCount -> "COUNT"
        | AggSum -> "SUM"
        | AggAvg -> "AVG"
        | AggMin -> "MIN"
        | AggMax -> "MAX"
        // The DISTINCT goes inside the parens, so this name only serves the
        // operand-less fallback; the render below spells the real thing.
        | AggCountDistinct -> "COUNT"

    /// The vendor's name for a function, and whether its arguments need
    /// reordering. Returns None where the engine has no equivalent.
    let private fnName (vendor: Vendor) (fn: SqlFn) =
        match fn, vendor with
        | Upper, _ -> ValueSome "UPPER"
        | Lower, _ -> ValueSome "LOWER"
        | Trim, _ -> ValueSome "TRIM"
        | Replace, _ -> ValueSome "REPLACE"
        | Abs, _ -> ValueSome "ABS"

        | Length, Sqlite -> ValueSome "LENGTH"
        | Length, Postgres -> ValueSome "CHAR_LENGTH"
        | Length, MySql -> ValueSome "CHAR_LENGTH"
        | Length, Generic -> ValueSome "LENGTH"

        | Substring, MySql -> ValueSome "MID"
        | Substring, Sqlite -> ValueSome "SUBSTR"
        | Substring, _ -> ValueSome "SUBSTRING"

        | IndexOf, Postgres -> ValueSome "STRPOS"
        | IndexOf, MySql -> ValueSome "LOCATE"
        | IndexOf, Sqlite -> ValueSome "INSTR"
        | IndexOf, Generic -> ValueSome "INSTR"

        // Every engine's plain-rounding function has the same name.
        | Round, _ -> ValueSome "ROUND"

        // The rest are not plain function calls on every engine, so they are
        // handled separately below and never reach this table.
        | (Year | Month | Day | Hour | Minute | Second | DateOnly), _ -> ValueNone
        | (Ceiling | Floor | RoundTo _ | Truncate | Greatest | Least), _ -> ValueNone
        | (CastText | CastInt | DateDiffDays | DateDiffSecs), _ -> ValueNone

    /// SQLite has no YEAR(); it has STRFTIME with a format string. PostgreSQL
    /// wants EXTRACT(field FROM x). MySQL has the plain function.
    let private datePartName (fn: SqlFn) =
        match fn with
        | Year -> ValueSome("YEAR", "%Y")
        | Month -> ValueSome("MONTH", "%m")
        | Day -> ValueSome("DAY", "%d")
        | Hour -> ValueSome("HOUR", "%H")
        | Minute -> ValueSome("MINUTE", "%M")
        | Second -> ValueSome("SECOND", "%S")
        | _ -> ValueNone

    let rec private renderExpr (b: Builder) (e: SqlExpr) =
        let append (s: string) = write b s

        match e with
        | ColumnRef(table, column) ->
            if b.Qualify then
                append ($"{table}.{column}")
            else
                append column

        | Literal v -> addParam b v

        // One case rather than two with a `when` guard on the first. A guard makes
        // Fable lower the whole match to a decision tree that pre-declares every
        // bound variable and zero-initialises it, and its `getZero` is
        // `mem::zeroed`, which panics for an Arc -- so binding an array, a list
        // or an option anywhere in a guarded match blows up at runtime (G25).
        | Binary(op, left, right) ->
            if op = Concat && b.Vendor = MySql then
                // MySQL's `||` is a boolean OR unless the server runs in ANSI
                // mode, so string concatenation has to go through CONCAT.
                append "CONCAT("
                renderExpr b left
                append ", "
                renderExpr b right
                append ")"
            else
                append "("
                renderExpr b left
                append (opText op)
                renderExpr b right
                append ")"

        | Not inner ->
            append "NOT ("
            renderExpr b inner
            append ")"

        | IsNull inner ->
            append "("
            renderExpr b inner
            append " IS NULL)"

        | IsNotNull inner ->
            append "("
            renderExpr b inner
            append " IS NOT NULL)"

        | InList(inner, values) ->
            if values.Length = 0 then
                // `IN ()` is a syntax error on every engine, and an empty set
                // matches nothing, so say that directly.
                append "(1 = 0)"
            else
                renderExpr b inner
                append " IN ("

                values
                |> Array.iteri (fun i v ->
                    if i > 0 then
                        append ", "

                    addParam b v)

                append ")"

        | Aggregate(agg, operand) ->
            match operand with
            | None ->
                append (aggText agg)
                append "(*)"
            | Some inner ->
                // Not a guard on the pattern: binding `inner` in a guarded
                // match trips G25 on the Rust target, same as the Binary case
                // above.
                if agg = AggCountDistinct then
                    append "COUNT(DISTINCT "
                else
                    append (aggText agg)
                    append "("

                renderExpr b inner
                append ")"

        | InQuery(inner, subquery) ->
            // A subquery on the right of IN has to yield one column; `SELECT *`
            // there is an error on every engine and a confusing one, so say it
            // here instead.
            match subquery.Select with
            | All -> failwith "SqlGen: an IN subquery must select exactly one column"
            | Items items when items.Length <> 1 ->
                failwith (
                    "SqlGen: an IN subquery must select exactly one column, not "
                    + string items.Length
                )
            | _ -> ()

            renderExpr b inner
            append " IN ("
            renderQueryInto b subquery
            append ")"

        | ExistsQuery subquery ->
            append "EXISTS ("
            renderQueryInto b subquery
            append ")"

        | ScalarQuery subquery ->
            append "("
            renderQueryInto b subquery
            append ")"

        | LikeEscaped(inner, pattern) ->
            // The escape character is part of the statement, not a parameter:
            // it says how to read the pattern, and every engine here accepts
            // the explicit clause.
            append "("
            renderExpr b inner
            append " LIKE "
            addParam b pattern
            append " ESCAPE '!')"

        | DateAdd(part, amount, inner) -> renderDateAdd b part amount inner

        | Case(branches, elseValue) ->
            if List.isEmpty branches then
                failwith "SqlGen: a CASE needs at least one WHEN"

            append "CASE"

            for condition, value in branches do
                append " WHEN "
                renderExpr b condition
                append " THEN "
                renderExpr b value

            (match elseValue with
             | None -> ()
             | Some e ->
                 append " ELSE "
                 renderExpr b e)

            append " END"

        | Call(fn, args) -> renderCall b fn args

    // The function renderers below take their arguments one small function at
    // a time rather than as one big `match fn, args with`: a dense tuple match
    // over a DU and a list mis-lowers on Fable's Rust target -- the decision
    // tree loses its bindings (the G25 family) -- and small single-scrutinee
    // matches are what it compiles correctly.

    /// The one argument of a single-argument function.
    and private oneArg (fn: SqlFn) (args: SqlExpr list) : SqlExpr =
        match args with
        | [ single ] -> single
        | _ -> failwith ("SqlGen: " + string fn + " takes exactly one argument")

    /// The two arguments of a two-argument function.
    and private twoArgs (fn: SqlFn) (args: SqlExpr list) : SqlExpr * SqlExpr =
        match args with
        | [ first; second ] -> first, second
        | _ -> failwith ("SqlGen: " + string fn + " takes exactly two arguments")

    /// `.Date`. SQLite and MySQL have DATE(); PostgreSQL's DATE() is not the
    /// same thing, so it uses the truncation SQLProvider uses too.
    and private renderDateOnly (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | Postgres ->
            append "DATE_TRUNC('day', "
            renderExpr b arg
            append ")"
        | _ ->
            append "DATE("
            renderExpr b arg
            append ")"

    // SQLite has CEIL and FLOOR only when compiled with its math functions,
    // which the sqlx build is not -- so both are spelled with CAST arithmetic
    // there. CAST truncates toward zero, and the comparison term (0 or 1) puts
    // the result on the right side of the value:
    // ceil(3.2) = 3 + (3.2 > 3), floor(-3.2) = -3 - (-3.2 < -3).
    and private renderCeiling (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | Postgres
        | MySql ->
            append "CEILING("
            renderExpr b arg
            append ")"
        | Sqlite
        | Generic ->
            append "(CAST("
            renderExpr b arg
            append " AS INTEGER) + ("
            renderExpr b arg
            append " > CAST("
            renderExpr b arg
            append " AS INTEGER)))"

    and private renderFloor (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | Postgres
        | MySql ->
            append "FLOOR("
            renderExpr b arg
            append ")"
        | Sqlite
        | Generic ->
            append "(CAST("
            renderExpr b arg
            append " AS INTEGER) - ("
            renderExpr b arg
            append " < CAST("
            renderExpr b arg
            append " AS INTEGER)))"

    /// PostgreSQL has no ROUND(double precision, int) -- the inner numeric
    /// cast makes the two-argument form resolve -- and the outer cast brings
    /// the numeric result back to a float the drivers decode (sqlx's Any
    /// driver has no numeric kind).
    and private renderRoundTo (b: Builder) (digits: int) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | Postgres ->
            append "CAST(ROUND(CAST("
            renderExpr b arg
            append (" AS numeric), " + string digits + ") AS double precision)")
        | _ ->
            append "ROUND("
            renderExpr b arg
            append (", " + string digits + ")")

    and private renderTruncate (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | Postgres ->
            append "TRUNC("
            renderExpr b arg
            append ")"
        | MySql ->
            append "TRUNCATE("
            renderExpr b arg
            append ", 0)"
        | Sqlite
        | Generic ->
            // CAST truncates toward zero, which is exactly Math.Truncate.
            append "CAST("
            renderExpr b arg
            append " AS INTEGER)"

    /// GREATEST/LEAST, or SQLite's two-argument MAX/MIN -- those are the
    /// scalar forms there, the aggregate being the one-argument spelling, so
    /// there is no ambiguity.
    and private renderPairFn (b: Builder) (name: string) (sqliteName: string) (pair: SqlExpr * SqlExpr) =
        let append (s: string) = write b s
        let first, second = pair

        match b.Vendor with
        | Sqlite
        | Generic -> append (sqliteName + "(")
        | Postgres | MySql -> append (name + "(")

        renderExpr b first
        append ", "
        renderExpr b second
        append ")"

    and private renderCastText (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        // MySQL rejects CAST(x AS TEXT); CHAR is its spelling of the same.
        match b.Vendor with
        | MySql ->
            append "CAST("
            renderExpr b arg
            append " AS CHAR)"
        | Postgres ->
            append "CAST("
            renderExpr b arg
            append " AS varchar)"
        | Sqlite
        | Generic ->
            append "CAST("
            renderExpr b arg
            append " AS TEXT)"

    and private renderCastInt (b: Builder) (arg: SqlExpr) =
        let append (s: string) = write b s

        match b.Vendor with
        | MySql ->
            append "CAST("
            renderExpr b arg
            append " AS SIGNED)"
        | _ ->
            append "CAST("
            renderExpr b arg
            append " AS INTEGER)"

    /// First minus second, in whole calendar days. The SQLite spelling
    /// truncates both sides to their day first, so it counts date boundaries
    /// the way DATEDIFF and PostgreSQL's date subtraction do, rather than
    /// 24-hour periods.
    and private renderDateDiffDays (b: Builder) (pair: SqlExpr * SqlExpr) =
        let append (s: string) = write b s
        let first, second = pair

        match b.Vendor with
        | Postgres ->
            append "(CAST("
            renderExpr b first
            append " AS date) - CAST("
            renderExpr b second
            append " AS date))"
        | MySql ->
            append "DATEDIFF("
            renderExpr b first
            append ", "
            renderExpr b second
            append ")"
        | Sqlite
        | Generic ->
            append "CAST(JULIANDAY(DATE("
            renderExpr b first
            append ")) - JULIANDAY(DATE("
            renderExpr b second
            append ")) AS INTEGER)"

    and private renderDateDiffSecs (b: Builder) (pair: SqlExpr * SqlExpr) =
        let append (s: string) = write b s
        let first, second = pair

        match b.Vendor with
        | Postgres ->
            // Cast to BIGINT rather than left as EXTRACT's numeric, which the
            // sqlx Any driver cannot decode.
            append "CAST(EXTRACT(EPOCH FROM (CAST("
            renderExpr b first
            append " AS timestamp) - CAST("
            renderExpr b second
            append " AS timestamp))) AS BIGINT)"
        | MySql ->
            // TIMESTAMPDIFF(unit, start, end) is end - start, so the
            // arguments swap to keep first-minus-second.
            append "TIMESTAMPDIFF(SECOND, "
            renderExpr b second
            append ", "
            renderExpr b first
            append ")"
        | Sqlite
        | Generic ->
            // ROUND before the cast: JULIANDAY is a float of days, and 100
            // seconds of it times 86400 lands at 99.99998 -- a bare CAST
            // truncates that to 99.
            append "CAST(ROUND((JULIANDAY("
            renderExpr b first
            append ") - JULIANDAY("
            renderExpr b second
            append ")) * 86400) AS INTEGER)"

    /// A date part (YEAR .. SECOND) or a function from the plain-name table --
    /// everything whose arguments render inside ordinary parentheses.
    and private renderPlainFn (b: Builder) (fn: SqlFn) (args: SqlExpr list) =
        let append (s: string) = write b s

        match datePartName fn with
        | ValueSome(name, strftime) ->
            let arg = oneArg fn args

            (match b.Vendor with
             | Sqlite ->
                 // STRFTIME returns text, so cast back to a number to keep
                 // comparisons behaving like the other engines.
                 append ($"CAST(STRFTIME('{strftime}', ")
                 renderExpr b arg
                 append ") AS INTEGER)"
             | Postgres ->
                 append ($"EXTRACT({name} FROM ")
                 renderExpr b arg
                 append ")"
             | MySql
             | Generic ->
                 append (name + "(")
                 renderExpr b arg
                 append ")")
        | ValueNone ->
            match fnName b.Vendor fn with
            | ValueNone -> failwith ("SqlGen: no SQL mapping for " + string fn)
            | ValueSome name ->
                // PostgreSQL's STRPOS takes (haystack, needle) like .NET's
                // IndexOf; MySQL's LOCATE takes (needle, haystack).
                let args =
                    if fn = IndexOf && b.Vendor = MySql then
                        match args with
                        | [ haystack; needle ] -> [ needle; haystack ]
                        | _ -> args
                    else
                        args

                append (name + "(")

                args
                |> List.iteri (fun i a ->
                    if i > 0 then
                        append ", "

                    renderExpr b a)

                append ")"

    and private renderCall (b: Builder) (fn: SqlFn) (args: SqlExpr list) =
        match fn with
        | DateOnly -> renderDateOnly b (oneArg fn args)
        | Ceiling -> renderCeiling b (oneArg fn args)
        | Floor -> renderFloor b (oneArg fn args)
        | RoundTo digits -> renderRoundTo b digits (oneArg fn args)
        | Truncate -> renderTruncate b (oneArg fn args)
        | Greatest -> renderPairFn b "GREATEST" "MAX" (twoArgs fn args)
        | Least -> renderPairFn b "LEAST" "MIN" (twoArgs fn args)
        | CastText -> renderCastText b (oneArg fn args)
        | CastInt -> renderCastInt b (oneArg fn args)
        | DateDiffDays -> renderDateDiffDays b (twoArgs fn args)
        | DateDiffSecs -> renderDateDiffSecs b (twoArgs fn args)
        | _ -> renderPlainFn b fn args

    /// Shifts a date by a constant amount.
    ///
    /// The amount is baked into the statement rather than bound, because both
    /// SQLite and PostgreSQL want it inside a literal -- a string and an
    /// interval respectively -- where a placeholder cannot go. It is an F# int
    /// the caller passed, not text from anywhere, so there is nothing to inject.
    and private renderDateAdd (b: Builder) (part: DatePart) (amount: int) (inner: SqlExpr) =
        let append (s: string) = write b s

        let unitName =
            match part with
            | Years -> "year"
            | Months -> "month"
            | Days -> "day"
            | Hours -> "hour"
            | Minutes -> "minute"
            | Seconds -> "second"

        match b.Vendor with
        | Postgres ->
            append "("
            renderExpr b inner
            append (" + INTERVAL '" + string amount + " " + unitName + "')")
        | MySql ->
            append "DATE_ADD("
            renderExpr b inner
            append (", INTERVAL " + string amount + " " + unitName.ToUpperInvariant() + ")")
        | Sqlite
        | Generic ->
            // SQLite wants a signed modifier string, and its own formatting for
            // a positive one includes the plus.
            let signed = if amount >= 0 then "+" + string amount else string amount

            append "DATETIME("
            renderExpr b inner
            append ($", '{signed} {unitName}s')")

    and private renderSource (b: Builder) (s: Source) =
        let append (t: string) = write b t

        if s.Alias = s.Table then
            append s.Table
        else
            append ($"{s.Table} AS {s.Alias}")

    /// Renders a query into an existing builder, so a subquery shares the outer
    /// statement's parameter list and stays numbered in the order the SQL
    /// mentions it.
    /// One SELECT through its HAVING -- everything that belongs to a single
    /// arm. ORDER BY and paging are not here: on a compound they belong to the
    /// combined result, so `renderQueryInto` writes them once at the end.
    and private renderSelectCore (b: Builder) (q: Query) =
        let append (s: string) = write b s

        if q.From.Table = "" then
            failwith "SqlGen: the query has no source table -- did the sqlQuery block forget its from?"

        append "SELECT "

        if q.Distinct then
            append "DISTINCT "

        match q.Select with
        | All -> append "*"
        | Items items ->
            if items.Length = 0 then
                append "*"
            else
                items
                |> Array.iteri (fun i item ->
                    if i > 0 then
                        append ", "

                    renderExpr b item.Expr

                    // A bare column already comes back under its own name, and
                    // aliasing it again only adds noise.
                    match item.Expr with
                    | ColumnRef(_, name) when name = item.Alias -> ()
                    | _ -> append (" AS " + item.Alias))

        append " FROM "
        renderSource b q.From

        for j in q.Joins do
            // `match j.Kind with ...` would be the natural way to write this,
            // but a fieldless DU matched with unit-valued branches mis-compiles
            // on Fable's Rust target (G23). Equality is generated correctly.
            if j.Kind = LeftJoin then
                append " LEFT JOIN "
            else
                append " INNER JOIN "

            renderSource b j.Source
            append " ON "
            renderExpr b j.On

        match q.Where with
        | None -> ()
        | Some condition ->
            append " WHERE "
            renderExpr b condition

        if q.GroupBy.Length > 0 then
            append " GROUP BY "

            q.GroupBy
            |> Array.iteri (fun i e ->
                if i > 0 then
                    append ", "

                renderExpr b e)

        match q.Having with
        | None -> ()
        | Some condition ->
            append " HAVING "
            renderExpr b condition

    /// Renders a query into an existing builder, so a subquery shares the outer
    /// statement's parameter list and stays numbered in the order the SQL
    /// mentions it.
    and private renderQueryInto (outer: Builder) (q: Query) =
        // A subquery qualifies its columns even when the statement around it does
        // not -- an UPDATE's WHERE renders bare, but a subquery there has its own
        // sources and may correlate with the outer one by name. The builder is a
        // record whose buffers are shared references, so this changes the mode
        // without splitting the output.
        let b = { outer with Qualify = true }

        let append (s: string) = write b s

        renderSelectCore b q

        // The arms carry no parentheses: SQLite rejects a parenthesised
        // compound operand, and every engine takes the bare chain.
        for op, arm in q.Compounds do
            if
                arm.OrderBy.Length > 0
                || arm.Skip.IsSome
                || arm.Take.IsSome
                || arm.Compounds.Length > 0
            then
                failwith
                    "SqlGen: a UNION/INTERSECT/EXCEPT arm cannot carry its own ordering, paging or set operations -- put them on the first query, where they apply to the combined result"

            // Equality rather than a match on the fieldless DU (G23), like the
            // join kind in renderSelectCore.
            if op = Union then append " UNION "
            elif op = UnionAll then append " UNION ALL "
            elif op = Intersect then append " INTERSECT "
            else append " EXCEPT "

            renderSelectCore b arm

        if q.OrderBy.Length > 0 then
            append " ORDER BY "

            // After a set operation the engines accept only the result's own
            // column names in ORDER BY, not Table.Column references, so the
            // compound form orders unqualified.
            let ob =
                if q.Compounds.Length > 0 then
                    { b with Qualify = false }
                else
                    b

            q.OrderBy
            |> Array.iteri (fun i (e, dir) ->
                if i > 0 then
                    append ", "

                renderExpr ob e

                // Equality rather than a match, for the same reason as the
                // join kind above.
                if dir = Desc then append " DESC" else append " ASC")

        // LIMIT/OFFSET is understood by all three engines. SQLite and MySQL
        // both refuse OFFSET without a LIMIT, so a skip with no take needs a
        // stand-in maximum -- SQLite reads a negative limit as "no limit", and
        // MySQL wants the documented largest BIGINT.
        match q.Take, q.Skip with
        | Some take, Some skip -> append (" LIMIT " + string take + " OFFSET " + string skip)
        | Some take, None -> append (" LIMIT " + string take)
        | None, Some skip ->
            match b.Vendor with
            | Postgres -> append (" OFFSET " + string skip)
            | Sqlite
            | Generic -> append (" LIMIT -1 OFFSET " + string skip)
            | MySql -> append (" LIMIT 18446744073709551615 OFFSET " + string skip)
        | None, None -> ()

    let private newBuilder vendor qualify =
        { Parts = ResizeArray<string>()
          Params = ResizeArray<SqlParam>()
          Vendor = vendor
          Qualify = qualify }

    /// Writes the `SET a = .., b = ..` list shared by INSERT and UPDATE.
    let private renderAssignments (b: Builder) (assignments: Assignment[]) =
        assignments
        |> Array.iteri (fun i a ->
            if i > 0 then
                write b ", "

            write b (a.Column + " = ")
            renderExpr b a.Value)

    let renderInsert (vendor: Vendor) (i: Insert) : string * SqlParam[] =
        if i.Assignments.Length = 0 then
            failwith "SqlGen: the insert sets no columns"

        // Columns are bare here, not qualified: an INSERT names exactly one
        // table and every engine rejects `INSERT INTO t (t.c) ..`.
        let b = newBuilder vendor false
        write b ($"INSERT INTO {i.Table} (")

        i.Assignments
        |> Array.iteri (fun n a ->
            if n > 0 then
                write b ", "

            write b a.Column)

        write b ") VALUES ("

        i.Assignments
        |> Array.iteri (fun n a ->
            if n > 0 then
                write b ", "

            renderExpr b a.Value)

        write b ")"
        String.concat "" (b.Parts.ToArray()), b.Params.ToArray()

    /// `INSERT INTO t (a, b) VALUES (..), (..)`. Every engine here takes the
    /// multi-row form.
    ///
    /// Note each value is still its own bound parameter, so a large batch can
    /// meet an engine's parameter ceiling -- SQLite's default is 999. Chunk the
    /// rows if that bites; this does not do it silently, because a silent chunk
    /// would stop the statement being atomic.
    let renderInsertMany (vendor: Vendor) (i: InsertMany) : string * SqlParam[] =
        if i.Columns.Length = 0 then
            failwith "SqlGen: the insert sets no columns"

        if i.Rows.Length = 0 then
            failwith "SqlGen: the insert has no rows"

        let b = newBuilder vendor false
        write b ($"INSERT INTO {i.Table} (")

        i.Columns
        |> Array.iteri (fun n c ->
            if n > 0 then
                write b ", "

            write b c)

        write b ") VALUES "

        i.Rows
        |> Array.iteri (fun rowIndex row ->
            if rowIndex > 0 then
                write b ", "

            write b "("

            row
            |> Array.iteri (fun n value ->
                if n > 0 then
                    write b ", "

                renderExpr b value)

            write b ")")

        String.concat "" (b.Parts.ToArray()), b.Params.ToArray()

    /// An insert that hands back a generated key.
    ///
    /// PostgreSQL says so in the statement itself; SQLite and MySQL have no
    /// RETURNING and need a second query, so this returns None for them and
    /// `Db.insertReturning` follows up.
    let renderInsertReturning (vendor: Vendor) (keyColumn: string) (i: Insert) : (string * SqlParam[]) option =
        match vendor with
        | Postgres ->
            let sql, ps = renderInsert vendor i
            Some($"{sql} RETURNING {keyColumn}", ps)
        | _ -> None

    /// The query that reports the key the last insert on this connection
    /// generated. Both are per-connection, which is what makes the follow-up
    /// safe on a connector that owns exactly one.
    let lastInsertedKeyQuery (vendor: Vendor) =
        match vendor with
        | MySql -> Some "SELECT LAST_INSERT_ID()"
        | Sqlite
        | Generic -> Some "SELECT last_insert_rowid()"
        | Postgres -> None

    let renderUpdate (vendor: Vendor) (u: Update) : string * SqlParam[] =
        if u.Assignments.Length = 0 then
            failwith "SqlGen: the update sets no columns"

        if u.Where.IsNone && not u.Unconditional then
            failwith "SqlGen: the update has no WHERE and would rewrite every row -- say Update.all if that is meant"

        let b = newBuilder vendor false
        write b ($"UPDATE {u.Table} SET ")
        renderAssignments b u.Assignments

        match u.Where with
        | None -> ()
        | Some condition ->
            write b " WHERE "
            renderExpr b condition

        String.concat "" (b.Parts.ToArray()), b.Params.ToArray()

    let renderDelete (vendor: Vendor) (d: Delete) : string * SqlParam[] =
        if d.Where.IsNone && not d.Unconditional then
            failwith "SqlGen: the delete has no WHERE and would empty the table -- say Delete.all if that is meant"

        let b = newBuilder vendor false
        write b ("DELETE FROM " + d.Table)

        match d.Where with
        | None -> ()
        | Some condition ->
            write b " WHERE "
            renderExpr b condition

        String.concat "" (b.Parts.ToArray()), b.Params.ToArray()

    let renderStatement (vendor: Vendor) (s: Statement) : string * SqlParam[] =
        match s with
        | InsertStmt i -> renderInsert vendor i
        | UpdateStmt u -> renderUpdate vendor u
        | DeleteStmt d -> renderDelete vendor d

    /// Renders the query. Returns the SQL and the parameters it binds, in the
    /// order the SQL mentions them.
    let render (vendor: Vendor) (q: Query) : string * SqlParam[] =
        let b = newBuilder vendor true
        renderQueryInto b q
        String.concat "" (b.Parts.ToArray()), b.Params.ToArray()
