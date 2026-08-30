namespace SQLProvider.Fable

/// One column being written, and what it is being set to.
///
/// The value is an `SqlExpr`, not a `SqlValue`, so an update can be written in
/// terms of the row it is updating -- `Balance = Balance + 10` goes to the
/// database as one statement instead of a read followed by a write.
type Assignment = { Column: string; Value: SqlExpr }

type Insert =
    { Table: string
      Assignments: Assignment[] }

/// Several rows written by one statement.
///
/// Built from ordinary `Insert` values that agree on their table and columns,
/// so there is no second way to describe a row -- see `Db.insertMany`.
type InsertMany =
    { Table: string
      Columns: string[]
      Rows: SqlExpr[][] }

type Update =
    {
        Table: string
        Assignments: Assignment[]
        Where: SqlExpr option
        /// Set by `Update.all`. Without it, rendering an update that has no
        /// WHERE is an error rather than a statement that rewrites the table.
        Unconditional: bool
    }

type Delete =
    {
        Table: string
        Where: SqlExpr option
        /// Set by `Delete.all`. Without it, rendering a delete that has no
        /// WHERE is an error rather than a statement that empties the table.
        Unconditional: bool
    }

/// A single write. `Db.submit` runs a batch of these in one transaction, which
/// is what SQLProvider's `SubmitUpdates` does.
type Statement =
    | InsertStmt of Insert
    | UpdateStmt of Update
    | DeleteStmt of Delete

module Insert =

    let into (table: string) : Insert = { Table = table; Assignments = [||] }

    let private add (a: Assignment) (i: Insert) =
        { i with
            Assignments = Array.append i.Assignments [| a |] }

    /// Sets a column to a value of its own type.
    let set (c: Column<'T>) (value: 'T) (i: Insert) =
        add
            { Column = c.Name
              Value = Literal(c.Encode value) }
            i

    /// Sets a column to NULL. Separate from `set` because a column's type says
    /// what it holds, not whether it is nullable.
    let setNull (c: Column<'T>) (i: Insert) =
        add
            { Column = c.Name
              Value = Literal SqlNull }
            i

    /// Sets a column from an optional value, mapping None to NULL.
    let setOpt (c: Column<'T>) (value: 'T option) (i: Insert) =
        match value with
        | Some v -> set c v i
        | None -> setNull c i

    /// Sets a column to an arbitrary expression, for defaults and computed
    /// values the database should evaluate.
    let setExpr (c: Column<'T>) (e: SqlExpr) (i: Insert) = add { Column = c.Name; Value = e } i

    /// Collapses inserts that agree on their table and columns into one
    /// multi-row statement.
    ///
    /// Every engine here takes `VALUES (..), (..)`, and one round trip for a
    /// hundred rows is the difference that matters. Disagreement is an error
    /// rather than something silently reshaped: a row with a column missing
    /// would otherwise take the previous row's value for it.
    let combine (inserts: Insert list) : InsertMany =
        match inserts with
        | [] -> failwith $"Insert.combine: no rows, calling combine with inserts: {inserts}"
        | first :: _ ->
            let columns = first.Assignments |> Array.map (fun a -> a.Column)

            for i in inserts do
                if i.Table <> first.Table then
                    failwith (
                        "Insert.combine: rows target different tables, "
                        + first.Table
                        + " and "
                        + i.Table
                    )

                let theseColumns = i.Assignments |> Array.map (fun a -> a.Column)

                if theseColumns.Length <> columns.Length then
                    failwith $"Insert.combine: rows set different numbers of columns, calling combine with inserts: {inserts}"

                Array.iteri
                    (fun n (c: string) ->
                        if c <> theseColumns.[n] then
                            failwith (
                                "Insert.combine: rows set columns in different orders, "
                                + c
                                + " and "
                                + theseColumns.[n]
                            ))
                    columns

            { Table = first.Table
              Columns = columns
              Rows =
                inserts
                |> List.map (fun i -> i.Assignments |> Array.map (fun a -> a.Value))
                |> Array.ofList }

module Update =

    let table (table: string) : Update =
        { Table = table
          Assignments = [||]
          Where = None
          Unconditional = false }

    let private add (a: Assignment) (u: Update) =
        { u with
            Assignments = Array.append u.Assignments [| a |] }

    let set (c: Column<'T>) (value: 'T) (u: Update) =
        add
            { Column = c.Name
              Value = Literal(c.Encode value) }
            u

    let setNull (c: Column<'T>) (u: Update) =
        add
            { Column = c.Name
              Value = Literal SqlNull }
            u

    let setOpt (c: Column<'T>) (value: 'T option) (u: Update) =
        match value with
        | Some v -> set c v u
        | None -> setNull c u

    /// Sets a column from an expression over the row being updated, so
    /// `Balance = Balance + 10` is one statement rather than a read and a write.
    let setExpr (c: Column<'T>) (e: SqlExpr) (u: Update) = add { Column = c.Name; Value = e } u

    /// Adds a condition. Successive calls are ANDed, as on the query side.
    let where (condition: SqlExpr) (u: Update) =
        { u with
            Where =
                match u.Where with
                | None -> Some condition
                | Some existing -> Some(Binary(And, existing, condition)) }

    /// The common case: restrict to one row by its key.
    let whereKey (c: Column<'T>) (value: 'T) (u: Update) =
        where (Binary(Eq, ColumnRef(c.Table, c.Name), Literal(c.Encode value))) u

    /// Applies to every row. Spelled out because an update with no WHERE is
    /// otherwise indistinguishable from one whose condition was forgotten.
    let all (u: Update) = { u with Unconditional = true }

module Delete =

    let from (table: string) : Delete =
        { Table = table
          Where = None
          Unconditional = false }

    let where (condition: SqlExpr) (d: Delete) =
        { d with
            Where =
                match d.Where with
                | None -> Some condition
                | Some existing -> Some(Binary(And, existing, condition)) }

    let whereKey (c: Column<'T>) (value: 'T) (d: Delete) =
        where (Binary(Eq, ColumnRef(c.Table, c.Name), Literal(c.Encode value))) d

    /// Empties the table. Spelled out for the same reason as `Update.all`.
    let all (d: Delete) = { d with Unconditional = true }

/// A pending batch, submitted together.
///
/// This is the shape of SQLProvider's change tracking without the mutable
/// entities behind it: changes accumulate, and `Db.submit` applies them inside
/// one transaction so a batch is all-or-nothing. Tracking is explicit rather
/// than automatic, because reading a row here produces an immutable
/// `SqlValue[]` and there is no entity object to notice a property being set.
module Batch =

    let empty: Statement[] = [||]

    let private add (s: Statement) (batch: Statement[]) = Array.append batch [| s |]

    let insert (i: Insert) (batch: Statement[]) = add (InsertStmt i) batch
    let update (u: Update) (batch: Statement[]) = add (UpdateStmt u) batch
    let delete (d: Delete) (batch: Statement[]) = add (DeleteStmt d) batch

    let count (batch: Statement[]) = batch.Length
