namespace SQLProvider.Fable

/// A computation expression over `Query`, so a query reads the way SQLProvider's
/// `query { }` reads:
///
///     sqlQuery {
///         from Customer.table
///         where (Customer.Country == "UK")
///         sortByDescending Customer.Balance
///         take 10
///     }
///
/// This is a plain F# computation expression, not `query { }`: that one builds a
/// `System.Linq.Expressions` tree, which no Fable target has. It works here
/// because `where` is handed an `SqlExpr` that the typed operators already
/// built, rather than a lambda that would have to be decompiled -- so nothing
/// needs quotations, and it compiles on every target the library supports.
///
/// The operations are named after SQLProvider's, so a query ported from there
/// mostly reads the same. Each one is the matching `Query` function, which stays
/// available for building a query in pieces; the two styles produce the same
/// record and can be mixed.
type SqlQueryBuilder() =

    /// Generic rather than `unit`-typed on purpose. F# calls this as
    /// `Yield(())` for a block that is only custom operations, and Fable drops a
    /// unit argument at the call site while keeping it in the member
    /// declaration -- so a `unit` parameter here generates Rust that does not
    /// compile (G26 in GAPS.md). A generic parameter is not dropped.
    member _.Yield(_value: 'T) : Query = Query.blank

    /// The table to select from. Required: without it there is nothing to
    /// render, and `SqlGen` says so rather than emitting `FROM `.
    [<CustomOperation("from")>]
    member _.From(_: Query, table: string) = Query.from table

    /// The table under an explicit alias, for joining it to itself.
    [<CustomOperation("fromAs")>]
    member _.FromAs(_: Query, table: string, alias: string) = Query.fromAs table alias

    [<CustomOperation("where")>]
    member _.Where(q: Query, condition: SqlExpr) = Query.where condition q

    [<CustomOperation("orWhere")>]
    member _.OrWhere(q: Query, condition: SqlExpr) = Query.orWhere condition q

    [<CustomOperation("whereAll")>]
    member _.WhereAll(q: Query, conditions: SqlExpr list) = Query.whereAll conditions q

    [<CustomOperation("whereAny")>]
    member _.WhereAny(q: Query, conditions: SqlExpr list) = Query.whereAny conditions q

    [<CustomOperation("join")>]
    member _.Join(q: Query, table: string, on: SqlExpr) = Query.join table on q

    [<CustomOperation("joinAs")>]
    member _.JoinAs(q: Query, table: string, alias: string, on: SqlExpr) = Query.joinAs table alias on q

    [<CustomOperation("leftJoin")>]
    member _.LeftJoin(q: Query, table: string, on: SqlExpr) = Query.leftJoin table on q

    [<CustomOperation("leftJoinAs")>]
    member _.LeftJoinAs(q: Query, table: string, alias: string, on: SqlExpr) = Query.leftJoinAs table alias on q

    [<CustomOperation("sortBy")>]
    member _.SortBy(q: Query, column: Column<'T>) = Query.orderBy column q

    [<CustomOperation("sortByDescending")>]
    member _.SortByDescending(q: Query, column: Column<'T>) = Query.orderByDesc column q

    [<CustomOperation("thenBy")>]
    member _.ThenBy(q: Query, column: Column<'T>) = Query.thenBy column q

    [<CustomOperation("thenByDescending")>]
    member _.ThenByDescending(q: Query, column: Column<'T>) = Query.thenByDesc column q

    /// Sorts by an arbitrary expression, for `sortBy (upper name)` and the like.
    [<CustomOperation("sortByExpr")>]
    member _.SortByExpr(q: Query, e: SqlExpr) = Query.orderByExpr e q

    [<CustomOperation("sortByExprDescending")>]
    member _.SortByExprDescending(q: Query, e: SqlExpr) = Query.orderByExprDesc e q

    [<CustomOperation("skip")>]
    member _.Skip(q: Query, n: int) = Query.skip n q

    [<CustomOperation("take")>]
    member _.Take(q: Query, n: int) = Query.take n q

    [<CustomOperation("distinct")>]
    member _.Distinct(q: Query) = Query.distinct q

    /// Combines with another query, deduplicating -- `.Union()`. Ordering and
    /// paging stated in this block apply to the combined result.
    [<CustomOperation("union")>]
    member _.Union(q: Query, other: Query) = Query.union other q

    /// `UNION ALL` -- `.Concat()`: everything from both, duplicates kept.
    [<CustomOperation("unionAll")>]
    member _.UnionAll(q: Query, other: Query) = Query.unionAll other q

    /// Rows present in both -- `.Intersect()`. MySQL only from 8.0.31.
    [<CustomOperation("intersect")>]
    member _.Intersect(q: Query, other: Query) = Query.intersect other q

    /// Rows of this query not in the other -- `.Except()`. MySQL only from
    /// 8.0.31.
    [<CustomOperation("except")>]
    member _.Except(q: Query, other: Query) = Query.except other q

    /// The projection. Overloaded so a column can be named directly --
    /// `select Customer.Name`, or `select (Customer.Name, Customer.Country)` --
    /// which is the point of having typed columns at all. The array form is
    /// still there for anything the tuples do not cover.
    [<CustomOperation("select")>]
    member _.Select(q: Query, columns: SqlExpr[]) = Query.select columns q

    [<CustomOperation("select")>]
    member _.Select(q: Query, c: Column<'T>) = Query.select [| c.E |] q

    [<CustomOperation("select")>]
    member _.Select(q: Query, (a: Column<'A>, b: Column<'B>)) = Query.select [| a.E; b.E |] q

    [<CustomOperation("select")>]
    member _.Select(q: Query, (a: Column<'A>, b: Column<'B>, c: Column<'C>)) = Query.select [| a.E; b.E; c.E |] q

    [<CustomOperation("select")>]
    member _.Select(q: Query, (a: Column<'A>, b: Column<'B>, c: Column<'C>, d: Column<'D>)) =
        Query.select [| a.E; b.E; c.E; d.E |] q

    [<CustomOperation("select")>]
    member _.Select(q: Query, (a: Column<'A>, b: Column<'B>, c: Column<'C>, d: Column<'D>, e: Column<'E>)) =
        Query.select [| a.E; b.E; c.E; d.E; e.E |] q

    /// Adds one column to the projection. Chainable, so a projection is built
    /// a column at a time rather than as an array of expressions.
    [<CustomOperation("selectCol")>]
    member _.SelectCol(q: Query, column: Column<'T>) = Query.selectCol column q

    /// Adds one named expression to the projection.
    [<CustomOperation("selectExpr")>]
    member _.SelectExpr(q: Query, alias: string, e: SqlExpr) = Query.selectExpr alias e q

    [<CustomOperation("groupByCol")>]
    member _.GroupByCol(q: Query, column: Column<'T>) = Query.groupByCol column q

    [<CustomOperation("selectAs")>]
    member _.SelectAs(q: Query, items: (string * SqlExpr)[]) = Query.selectAs items q

    [<CustomOperation("groupBy")>]
    member _.GroupBy(q: Query, columns: SqlExpr[]) = Query.groupBy columns q

    [<CustomOperation("groupBy")>]
    member _.GroupBy(q: Query, c: Column<'T>) = Query.groupBy [| c.E |] q

    [<CustomOperation("groupBy")>]
    member _.GroupBy(q: Query, (a: Column<'A>, b: Column<'B>)) = Query.groupBy [| a.E; b.E |] q

    [<CustomOperation("having")>]
    member _.Having(q: Query, condition: SqlExpr) = Query.having condition q

[<AutoOpen>]
module QueryExpression =

    /// Not named `query`: that is FSharp.Core's LINQ builder, and shadowing it
    /// would silently change the meaning of any `query { }` in the same file.
    let sqlQuery = SqlQueryBuilder()

/// `sqlInsert { into t; set c v }` -- the write side of the same idea.
type SqlInsertBuilder() =

    member _.Yield(_value: 'T) : Insert = { Table = ""; Assignments = [||] }

    [<CustomOperation("into")>]
    member _.Into(_: Insert, table: string) = Insert.into table

    [<CustomOperation("set")>]
    member _.Set(i: Insert, column: Column<'T>, value: 'T) = Insert.set column value i

    [<CustomOperation("setOpt")>]
    member _.SetOpt(i: Insert, column: Column<'T>, value: 'T option) = Insert.setOpt column value i

    [<CustomOperation("setNull")>]
    member _.SetNull(i: Insert, column: Column<'T>) = Insert.setNull column i

    [<CustomOperation("setExpr")>]
    member _.SetExpr(i: Insert, column: Column<'T>, e: SqlExpr) = Insert.setExpr column e i

type SqlUpdateBuilder() =

    member _.Yield(_value: 'T) : Update =
        { Table = ""
          Assignments = [||]
          Where = None
          Unconditional = false }

    [<CustomOperation("table")>]
    member _.Table(_: Update, table: string) = Update.table table

    [<CustomOperation("set")>]
    member _.Set(u: Update, column: Column<'T>, value: 'T) = Update.set column value u

    [<CustomOperation("setOpt")>]
    member _.SetOpt(u: Update, column: Column<'T>, value: 'T option) = Update.setOpt column value u

    [<CustomOperation("setNull")>]
    member _.SetNull(u: Update, column: Column<'T>) = Update.setNull column u

    [<CustomOperation("setExpr")>]
    member _.SetExpr(u: Update, column: Column<'T>, e: SqlExpr) = Update.setExpr column e u

    [<CustomOperation("where")>]
    member _.Where(u: Update, condition: SqlExpr) = Update.where condition u

    [<CustomOperation("whereKey")>]
    member _.WhereKey(u: Update, column: Column<'T>, value: 'T) = Update.whereKey column value u

    [<CustomOperation("all")>]
    member _.All(u: Update) = Update.all u

type SqlDeleteBuilder() =

    member _.Yield(_value: 'T) : Delete =
        { Table = ""
          Where = None
          Unconditional = false }

    [<CustomOperation("from")>]
    member _.From(_: Delete, table: string) = Delete.from table

    [<CustomOperation("where")>]
    member _.Where(d: Delete, condition: SqlExpr) = Delete.where condition d

    [<CustomOperation("whereKey")>]
    member _.WhereKey(d: Delete, column: Column<'T>, value: 'T) = Delete.whereKey column value d

    [<CustomOperation("all")>]
    member _.All(d: Delete) = Delete.all d

[<AutoOpen>]
module CrudExpression =

    let sqlInsert = SqlInsertBuilder()
    let sqlUpdate = SqlUpdateBuilder()
    let sqlDelete = SqlDeleteBuilder()
