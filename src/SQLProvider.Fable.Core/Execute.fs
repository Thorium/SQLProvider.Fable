namespace SQLProvider.Fable

/// Running a `Query` against a connector.
///
/// Everything here returns a concrete type. There is no
/// `exec: ... -> Async<'T[]>` taking a mapper, because `Async<'T>` with a
/// generic `'T` does not compile on Fable's Rust target -- `Async` requires
/// `T: Send + Sync` and a bare generic parameter carries neither (G21 in
/// GAPS.md). Callers await a `ResultSet` and map it synchronously:
///
///     let! rs = Db.query conn q
///     let customers = rs |> ResultSet.map Customer.ofRow
///
/// which is also what the hand-written suite already does.
[<RequireQualifiedAccess>]
module Db =

    /// The SQL a query would run, for logging or for a test that wants to
    /// assert on the text rather than the rows. This is the equivalent of
    /// SQLProvider's `query.ToString()`.
    let toSql (vendor: Vendor) (q: Query) = SqlGen.render vendor q

    /// Runs a query and returns every row.
    let query (conn: ISqlConnector) (q: Query) : Async<ResultSet> =
        let sql, ps = SqlGen.render conn.Vendor q
        conn.Query(sql, ps)

    /// Runs a query and returns the first row, if there is one.
    let tryHead (conn: ISqlConnector) (q: Query) : Async<SqlRow option> =
        async {
            // One row is all that gets asked for, whatever the caller wrote.
            let! rs = query conn { q with Take = Some 1 }
            return ResultSet.tryHead rs
        }

    /// The first row, and an error when there is none -- LINQ's `head`. Use
    /// `tryHead` when an empty result is an answer rather than a bug.
    let head (conn: ISqlConnector) (q: Query) : Async<SqlRow> =
        async {

            match! tryHead conn q with
            | Some r -> return r
            | None -> return failwith "head: the query returned no rows"
        }

    /// The row, when the query yields exactly one; None on none at all --
    /// LINQ's `exactlyOneOrDefault`. Two rows are still an error: a second
    /// one means the WHERE was not as selective as the caller believed.
    let tryExactlyOne (conn: ISqlConnector) (q: Query) : Async<SqlRow option> =
        async {
            // Two rows are enough to tell one from many, however many matched.
            let! rs = query conn { q with Take = Some 2 }

            if rs.Rows.Length > 1 then
                return failwith "tryExactlyOne: the query returned more than one row"
            else
                return ResultSet.tryHead rs
        }

    /// The row, when the query yields exactly one; an error on none or many --
    /// LINQ's `exactlyOne`.
    let exactlyOne (conn: ISqlConnector) (q: Query) : Async<SqlRow> =
        async {

            match! tryExactlyOne conn q with
            | Some r -> return r
            | None -> return failwith "exactlyOne: the query returned no rows"
        }

    /// `SELECT COUNT(*)`, with the caller's ordering and paging dropped.
    let count (conn: ISqlConnector) (q: Query) : Async<int64> =
        async {
            let sql, ps = SqlGen.render conn.Vendor (Query.countQuery q)
            let! v = conn.Scalar(sql, ps)

            return
                match v with
                | SqlInt n -> n
                // MySQL hands COUNT back as a DECIMAL-typed bigint, and an
                // engine that has been through a float path can return it as
                // one too. Both are exact here.
                | SqlDecimal d -> int64 d
                | SqlFloat f -> int64 f
                | SqlNull -> 0L
                | other -> failwith ("count: expected a number but got " + SqlValue.typeName other)
        }

    /// Whether the query matches anything, without dragging the rows back.
    let exists (conn: ISqlConnector) (q: Query) : Async<bool> =
        async {
            // Deduplication cannot change whether anything exists, so DISTINCT
            // is dropped here rather than refused the way a bare count of it is.
            let! n = count conn { q with Distinct = false }
            return n > 0L
        }

    /// A single aggregate value. `None` when the query matched no rows, which
    /// is what SUM/MIN/MAX return over an empty set.
    let private aggregate (conn: ISqlConnector) (q: Query) : Async<SqlValue option> =
        async {
            let sql, ps = SqlGen.render conn.Vendor q
            let! v = conn.Scalar(sql, ps)

            return
                match v with
                | SqlNull -> None
                | other -> Some other
        }

    let sum (conn: ISqlConnector) (c: Column<'T>) (q: Query) = aggregate conn (Query.sumQuery c q)
    let avg (conn: ISqlConnector) (c: Column<'T>) (q: Query) = aggregate conn (Query.avgQuery c q)
    let min (conn: ISqlConnector) (c: Column<'T>) (q: Query) = aggregate conn (Query.minQuery c q)
    let max (conn: ISqlConnector) (c: Column<'T>) (q: Query) = aggregate conn (Query.maxQuery c q)

    // --- writes -----------------------------------------------------------

    let insert (conn: ISqlConnector) (i: Insert) : Async<int> =
        let sql, ps = SqlGen.renderInsert conn.Vendor i
        conn.Execute(sql, ps)

    let update (conn: ISqlConnector) (u: Update) : Async<int> =
        let sql, ps = SqlGen.renderUpdate conn.Vendor u
        conn.Execute(sql, ps)

    let delete (conn: ISqlConnector) (d: Delete) : Async<int> =
        let sql, ps = SqlGen.renderDelete conn.Vendor d
        conn.Execute(sql, ps)

    let execute (conn: ISqlConnector) (s: Statement) : Async<int> =
        let sql, ps = SqlGen.renderStatement conn.Vendor s
        conn.Execute(sql, ps)

    /// Applies a batch of writes in one transaction and returns the total rows
    /// affected. This is SQLProvider's `SubmitUpdates`: either every statement
    /// lands or none does.
    ///
    /// A failure rolls back and then re-raises with the original message. The
    /// message rather than the exception itself, because `raise` does not carry
    /// the value through on Fable's Rust target (G16 in GAPS.md) -- so what
    /// survives everywhere is the text.
    let submit (conn: ISqlConnector) (batch: Statement[]) : Async<int> =
        async {
            if batch.Length = 0 then
                return 0
            else

                do! conn.BeginTransaction()
                let mutable affected = 0
                let mutable failed = false
                let mutable failure = ""

                try
                    for s in batch do
                        let sql, ps = SqlGen.renderStatement conn.Vendor s
                        let! n = conn.Execute(sql, ps)
                        affected <- affected + n

                    do! conn.Commit()
                with e ->
                    // Recorded rather than re-raised here: the rollback has to
                    // happen first, and raising from inside the handler would skip
                    // it. A flag, not `failure <> ""`, so an exception with an
                    // empty message still counts as one.
                    failed <- true
                    failure <- e.Message

                if failed then
                    // Best-effort: when it was COMMIT itself that failed, the
                    // transaction is already finished or aborted and the rollback
                    // may protest -- and its protest must not replace the message
                    // that says what actually went wrong.
                    try
                        do! conn.Rollback()
                    with _ ->
                        ()

                    return failwith ("submit failed and was rolled back: " + failure)
                else
                    return affected
        }

    /// Writes several rows with one statement. Ordinary `Insert` values that
    /// agree on their table and columns; disagreement is an error rather than
    /// something quietly reshaped.
    let insertMany (conn: ISqlConnector) (inserts: Insert list) : Async<int> =
        match inserts with
        | [] -> async { return 0 }
        | _ ->
            let sql, ps = SqlGen.renderInsertMany conn.Vendor (Insert.combine inserts)
            conn.Execute(sql, ps)

    /// Inserts and hands back the key the database generated, which is what an
    /// identity or serial primary key makes you go looking for.
    ///
    /// PostgreSQL says `RETURNING` in the statement itself. SQLite and MySQL
    /// have no such thing, so this follows up with `last_insert_rowid()` /
    /// `LAST_INSERT_ID()`. Both are per-connection, and a connector owns exactly
    /// one connection -- but the two statements are not one atomic step, so do
    /// not interleave another insert on the same connector in between. Wrap the
    /// pair in a transaction if that is a real possibility.
    let insertReturning (conn: ISqlConnector) (key: Column<'T>) (i: Insert) : Async<SqlValue> =
        async {
            match SqlGen.renderInsertReturning conn.Vendor key.Name i with
            | Some(sql, ps) ->
                // One statement: the engine gives the key back directly.
                return! conn.Scalar(sql, ps)
            | None ->
                let sql, ps = SqlGen.renderInsert conn.Vendor i
                let! _ = conn.Execute(sql, ps)

                match SqlGen.lastInsertedKeyQuery conn.Vendor with
                | None -> return failwith "insertReturning: this backend reports no generated key"
                | Some keyQuery -> return! conn.Scalar(keyQuery, Sql.noParams)
        }

    /// Runs a computation inside a transaction, committing if it finishes and
    /// rolling back if it does not.
    ///
    /// `Async<unit>` rather than a generic result, because `Async<'T>` with a
    /// bare generic parameter does not compile on Fable's Rust target (G21).
    /// Collect what the body produced in a mutable the caller owns, the way the
    /// suite does.
    let inTransaction (conn: ISqlConnector) (body: Async<unit>) : Async<unit> =
        async {
            do! conn.BeginTransaction()
            let mutable failed = false
            let mutable failure = ""

            try
                do! body
                do! conn.Commit()
            with e ->
                // Recorded rather than re-raised here: the rollback has to
                // happen first, and raising from the handler would skip it. A
                // flag, not `failure <> ""`, so an exception with an empty
                // message still counts as one.
                failed <- true
                failure <- e.Message

            if failed then
                // Best-effort, as in `submit`: a failed COMMIT has already ended
                // the transaction, and the rollback's own protest must not
                // replace the original message.
                try
                    do! conn.Rollback()
                with _ ->
                    ()

                return failwith ("the transaction was rolled back: " + failure)
        }
