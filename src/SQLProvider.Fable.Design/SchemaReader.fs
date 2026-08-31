namespace SQLProvider.Fable.Design

open SQLProvider.Fable
open SQLProvider.Fable.Design.Schema

/// Reads a schema out of a live database, over the same `ISqlConnector` the
/// runtime uses.
///
/// This runs at design time on .NET, so it is free to be as vendor-specific as
/// it needs to be. PostgreSQL and MySQL go through `information_schema`, which
/// is standard; SQLite has no such thing and uses its `pragma_*` table-valued
/// functions instead.
module SchemaReader =

    let private text (row: SqlRow) (name: string) = Row.text row name

    // --- SQLite ------------------------------------------------------------

    [<Literal>]
    let private sqliteTables =
        "SELECT name, type FROM sqlite_master
         WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
         ORDER BY name"

    let private readSqlite (conn: ISqlConnector) =
        async {
            let! tableRows = conn.Query(sqliteTables, Sql.noParams)

            let names =
                tableRows |> ResultSet.map (fun r -> text r "name", text r "type" = "view")

            let tables = ResizeArray<TableInfo>()

            for name, isView in names do
                // pragma_table_info takes the table name as a value, so this is
                // still a bound parameter rather than string-built SQL.
                let! cols =
                    conn.Query(
                        "SELECT name, type, \"notnull\", pk FROM pragma_table_info(@t) ORDER BY cid",
                        [| Sql.pText "t" name |]
                    )

                let columns =
                    cols
                    |> ResultSet.map (fun r ->
                        { Name = text r "name"
                          Kind = kindOfDbType (text r "type")
                          IsNullable = Row.int r "notnull" = 0
                          IsPrimaryKey = Row.int r "pk" > 0 })

                // One row per column, with `id` naming the constraint a row
                // belongs to -- a composite key is its rows grouped by id, in
                // seq order, not one foreign key per row.
                let! fks =
                    conn.Query(
                        "SELECT id, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list(@t) ORDER BY id, seq",
                        [| Sql.pText "t" name |]
                    )

                let foreignKeys =
                    fks
                    |> ResultSet.map (fun r ->
                        Row.int r "id",
                        text r "table",
                        text r "from",
                        // SQLite leaves `to` NULL when the reference is to the
                        // other table's primary key by implication.
                        Row.textOpt r "to" |> Option.defaultValue "")
                    |> Array.groupBy (fun (id, table, _, _) -> id, table)
                    |> Array.map (fun ((_, table), parts) ->
                        { Columns = parts |> Array.map (fun (_, _, ownCol, refCol) -> ownCol, refCol)
                          ReferencesTable = table })

                tables.Add
                    { Name = name
                      Columns = columns
                      ForeignKeys = foreignKeys
                      IsView = isView }

            return { Tables = tables.ToArray() }
        }

    // --- information_schema (PostgreSQL, MySQL) ----------------------------

    /// PostgreSQL folds unquoted identifiers to lower case and MySQL keeps the
    /// case it was given, so the schema filter differs but the shape does not.
    let private infoSchemaTables (vendor: Vendor) =
        match vendor with
        | Postgres ->
            "SELECT table_name, table_type FROM information_schema.tables
             WHERE table_schema = 'public' AND table_type IN ('BASE TABLE', 'VIEW')
             ORDER BY table_name"
        | _ ->
            "SELECT table_name, table_type FROM information_schema.tables
             WHERE table_schema = DATABASE() AND table_type IN ('BASE TABLE', 'VIEW')
             ORDER BY table_name"

    let private infoSchemaColumns (vendor: Vendor) =
        let schemaFilter =
            match vendor with
            | Postgres -> "'public'"
            | _ -> "DATABASE()"

        "SELECT c.column_name, c.data_type, c.is_nullable,
                CASE WHEN k.column_name IS NULL THEN 0 ELSE 1 END AS is_key
         FROM information_schema.columns c
         LEFT JOIN information_schema.key_column_usage k
              ON  k.table_schema = c.table_schema
              AND k.table_name   = c.table_name
              AND k.column_name  = c.column_name
              AND k.constraint_name IN (
                  SELECT constraint_name FROM information_schema.table_constraints
                  WHERE table_schema = c.table_schema
                    AND table_name   = c.table_name
                    AND constraint_type = 'PRIMARY KEY')
         WHERE c.table_schema = "
        + schemaFilter
        + " AND c.table_name = @t
         ORDER BY c.ordinal_position"

    /// One row per column, ordered within its constraint, so a composite key
    /// reassembles by grouping on constraint_name.
    [<Literal>]
    let private infoSchemaForeignKeys =
        "SELECT k.constraint_name, k.column_name, k.referenced_table_name, k.referenced_column_name
         FROM information_schema.key_column_usage k
         WHERE k.table_schema = DATABASE()
           AND k.table_name = @t AND k.referenced_table_name IS NOT NULL
         ORDER BY k.constraint_name, k.ordinal_position"

    /// PostgreSQL's key_column_usage has no `referenced_*` columns -- those are
    /// a MySQL extension -- so the referenced side comes from the unique
    /// constraint the key points at, through referential_constraints.
    /// Not constraint_column_usage: that view is a bare set of columns, so
    /// joining it against a composite key cross-multiplies the pairs.
    /// `position_in_unique_constraint` is what lines each column up with the
    /// one it references.
    [<Literal>]
    let private postgresForeignKeys =
        "SELECT kcu.constraint_name,
                kcu.column_name,
                rcu.table_name  AS referenced_table_name,
                rcu.column_name AS referenced_column_name
         FROM information_schema.referential_constraints rc
         JOIN information_schema.key_column_usage kcu
              ON  kcu.constraint_name = rc.constraint_name
              AND kcu.constraint_schema = rc.constraint_schema
         JOIN information_schema.key_column_usage rcu
              ON  rcu.constraint_name = rc.unique_constraint_name
              AND rcu.constraint_schema = rc.unique_constraint_schema
              AND rcu.ordinal_position = kcu.position_in_unique_constraint
         WHERE kcu.table_schema = 'public'
           AND kcu.table_name = @t
         ORDER BY kcu.constraint_name, kcu.ordinal_position"

    let private readInfoSchema (conn: ISqlConnector) (vendor: Vendor) =
        async {
            let! tableRows = conn.Query(infoSchemaTables vendor, Sql.noParams)

            let names =
                tableRows
                |> ResultSet.map (fun r -> text r "table_name", text r "table_type" = "VIEW")

            let tables = ResizeArray<TableInfo>()

            for name, isView in names do
                let! cols = conn.Query(infoSchemaColumns vendor, [| Sql.pText "t" name |])

                let columns =
                    cols
                    |> ResultSet.map (fun r ->
                        { Name = text r "column_name"
                          Kind = kindOfDbType (text r "data_type")
                          IsNullable = System.String.Equals((text r "is_nullable"), "YES", System.StringComparison.OrdinalIgnoreCase)
                          // COUNT-style flags come back as an integer on
                          // PostgreSQL and can arrive as a decimal on MySQL.
                          IsPrimaryKey =
                            match Row.value r "is_key" with
                            | SqlInt n -> n > 0L
                            | SqlDecimal d -> d > 0M
                            | SqlBool b -> b
                            | _ -> false })

                let fkSql =
                    match vendor with
                    | Postgres -> postgresForeignKeys
                    | _ -> infoSchemaForeignKeys

                let! fks = conn.Query(fkSql, [| Sql.pText "t" name |])

                let foreignKeys =
                    fks
                    |> ResultSet.map (fun r ->
                        text r "constraint_name",
                        text r "referenced_table_name",
                        text r "column_name",
                        text r "referenced_column_name")
                    |> Array.groupBy (fun (constraintName, table, _, _) -> constraintName, table)
                    |> Array.map (fun ((_, table), parts) ->
                        { Columns = parts |> Array.map (fun (_, _, ownCol, refCol) -> ownCol, refCol)
                          ReferencesTable = table })

                tables.Add
                    { Name = name
                      Columns = columns
                      ForeignKeys = foreignKeys
                      IsView = isView }

            return { Tables = tables.ToArray() }
        }

    /// Reads the whole schema. The connector's vendor decides how.
    let read (conn: ISqlConnector) : Async<Database> =
        match conn.Vendor with
        | Sqlite
        | Generic -> readSqlite conn
        | vendor -> readInfoSchema conn vendor
