namespace SQLProvider.Fable.Design

open System.Text
open SQLProvider.Fable.Design.Schema

/// Emits the F# a schema implies: one module per table, holding the table name,
/// a typed `Column` per column, a record for a row, and the `ofRow` mapper that
/// fills it.
///
/// This is the piece that makes the runtime worth using -- hand-writing
/// `Col.text "Customer" "Name"` for every column is exactly the work a provider
/// is supposed to remove. The output is ordinary source: it compiles to every
/// target the library does, and it can be read, diffed and stepped through,
/// which an erased type provider's output cannot.
module CodeGen =

    /// F# keywords a column name could collide with. A generated identifier
    /// that is one of these is escaped rather than renamed, so it still matches
    /// the database.
    let private keywords =
        set
            [ "type"
              "module"
              "let"
              "and"
              "or"
              "not"
              "to"
              "when"
              "with"
              "match"
              "function"
              "value"
              "end"
              "begin"
              "class"
              "default"
              "done"
              "downcast"
              "else"
              "exception"
              "false"
              "for"
              "fun"
              "if"
              "in"
              "inherit"
              "internal"
              "member"
              "new"
              "of"
              "open"
              "private"
              "public"
              "rec"
              "return"
              "static"
              "struct"
              "then"
              "true"
              "try"
              "use"
              "val"
              "while"
              "yield" ]

    /// The identifier a name becomes in the generated source.
    let identifier (name: string) =
        if keywords.Contains name then
            $"``{name}``"
        else
            // Anything that is not a plain identifier gets backticks too, which
            // covers spaces and punctuation without renaming the column.
            let isPlain =
                name.Length > 0
                && (System.Char.IsLetter name.[0] || name.[0] = '_')
                && name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

            if isPlain then name else $"``{name}``"

    let private colFactory (kind: ColumnKind) =
        match kind with
        | KInt -> "Col.int64"
        | KFloat -> "Col.float"
        | KDecimal -> "Col.decimal"
        | KText -> "Col.text"
        | KBool -> "Col.bool"
        | KDate -> "Col.date"
        | KGuid -> "Col.guid"
        | KBlob -> "Col.blob"

    let private fsharpType (kind: ColumnKind) =
        match kind with
        | KInt -> "int64"
        | KFloat -> "float"
        | KDecimal -> "decimal"
        | KText -> "string"
        | KBool -> "bool"
        | KDate -> "System.DateTime"
        | KGuid -> "System.Guid"
        | KBlob -> "byte[]"

    let private reader (kind: ColumnKind) =
        match kind with
        | KInt -> "Row.int64"
        | KFloat -> "Row.float"
        | KDecimal -> "Row.decimal"
        | KText -> "Row.text"
        | KBool -> "Row.bool"
        | KDate -> "Row.dateTime"
        | KGuid -> "Row.guid"
        | KBlob -> "Row.blob"

    /// A nullable column reads through the `*Opt` reader and lands in an
    /// `option` field, which is the whole reason the runtime has both.
    let private readerFor (c: ColumnInfo) =
        if c.IsNullable then
            reader c.Kind + "Opt"
        else
            reader c.Kind

    let private typeFor (c: ColumnInfo) =
        if c.IsNullable then
            fsharpType c.Kind + " option"
        else
            fsharpType c.Kind

    let private emitTable (sb: StringBuilder) (t: TableInfo) =
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line ("/// " + t.Name + (if t.IsView then " (view -- readable, not writable)" else ""))
        line ("module " + identifier t.Name + " =")
        line ""
        line ($"    let table = \"{t.Name}\"")
        line ""

        for c in t.Columns do
            line (
                "    let "
                + identifier c.Name
                + " = "
                + colFactory c.Kind
                + " table \""
                + c.Name
                + "\""
            )

        line ""

        if t.Columns.Length > 0 then
            line "    /// One row, as a record."
            line "    type Row ="
            let mutable first = true

            for c in t.Columns do
                let prefix = if first then "        { " else "          "
                first <- false
                line (prefix + identifier c.Name + ": " + typeFor c)

            sb.Length <- sb.Length - 1
            line " }"
            line ""

            line "    let ofRow (r: SqlRow) : Row ="
            let mutable firstField = true

            for c in t.Columns do
                let prefix = if firstField then "        { " else "          "
                firstField <- false
                line (prefix + identifier c.Name + " = " + readerFor c + " r \"" + c.Name + "\"")

            sb.Length <- sb.Length - 1
            line " }"
            line ""

            // The qualified pair exists for joins: `SELECT *` across two
            // tables collides wherever they share a column name, and `Row`
            // would quietly read whichever came first. Aliasing every column
            // Table_Column keeps them apart, and the matching reader puts the
            // prefix back.
            line "    /// Every column aliased Table_Column, for selecting this table out of a"
            line "    /// join without column-name collisions:"
            line "    /// `selectAs (Array.append A.qualified B.qualified)`. Read the results"
            line "    /// back with `ofQualifiedRow`."
            line "    let qualified ="

            let mutable firstQ = true

            for c in t.Columns do
                let prefix = if firstQ then "        [| " else "           "
                firstQ <- false
                line (prefix + "\"" + t.Name + "_" + c.Name + "\", " + identifier c.Name + ".E")

            sb.Length <- sb.Length - 1
            line " |]"
            line ""

            line "    /// `ofRow` over the `qualified` aliases."
            line "    let ofQualifiedRow (r: SqlRow) : Row ="

            let mutable firstQF = true

            for c in t.Columns do
                let prefix = if firstQF then "        { " else "          "
                firstQF <- false

                line (
                    prefix
                    + identifier c.Name
                    + " = "
                    + readerFor c
                    + " r \""
                    + t.Name
                    + "_"
                    + c.Name
                    + "\""
                )

            sb.Length <- sb.Length - 1
            line " }"
            line ""

        // Foreign keys become ready-made join conditions rather than navigation
        // properties: there is no lazy-loading context here to hang a property
        // off, and a join condition is what the query builder actually consumes.
        if t.ForeignKeys.Length > 0 then
            line "    /// Join conditions from this table's foreign keys."
            line "    module Relations ="
            line ""

            // Two keys to the same table cannot both be `toX` -- a module
            // refuses the duplicate `let` -- so a repeated target carries its
            // own columns in the name: `toAddressViaBillingAddressId`.
            let repeated =
                t.ForeignKeys
                |> Array.countBy (fun fk -> fk.ReferencesTable)
                |> Array.filter (fun (_, count) -> count > 1)
                |> Array.map fst
                |> Set.ofArray

            for fk in t.ForeignKeys do
                let name =
                    if repeated.Contains fk.ReferencesTable then
                        "to"
                        + fk.ReferencesTable
                        + "Via"
                        + (fk.Columns |> Array.map fst |> String.concat "And")
                    else
                        "to" + fk.ReferencesTable

                let pairDoc (ownCol, refCol) =
                    t.Name + "." + ownCol + " = " + fk.ReferencesTable + "." + refCol

                let pairExpr (ownCol, refCol) =
                    "Expr.eq (ColumnRef(table, \""
                    + ownCol
                    + "\")) (ColumnRef(\""
                    + fk.ReferencesTable
                    + "\", \""
                    + refCol
                    + "\"))"

                // A composite key is one condition over every column pair, so
                // using the relation can never match half a key.
                let condition =
                    fk.Columns
                    |> Array.map pairExpr
                    |> Array.reduce (fun acc part -> $"Expr.andAlso ({acc}) ({part})")

                line ("        /// " + (fk.Columns |> Array.map pairDoc |> String.concat ", "))
                line ("        let " + identifier name + " = " + condition)

            line ""

    /// Emits a module for the whole database. `moduleName` is the top-level
    /// module the tables are nested in.
    let emit (moduleName: string) (db: Database) : string =
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line "// Generated by SQLProvider.Fable. Do not edit."
        line "//"
        line "// Regenerate rather than adjust: the column names here have to match the"
        line "// database exactly, and a hand edit is a silent mismatch waiting to happen."
        line ("module " + moduleName)
        line ""
        line "open SQLProvider.Fable"
        line ""

        for t in db.Tables do
            emitTable sb t

        sb.ToString()
