namespace SQLProvider.Fable.Design

/// The schema model the code generator emits from.
///
/// Deliberately small, and deliberately not SQLProvider's own `Table`/`Column`
/// types: this is a design-time model that only has to carry what the generated
/// code needs -- a name, a kind, and whether a column is nullable or a key.
/// Anything that reads a schema (a live connection today, SQLProvider's offline
/// `SchemaCache` JSON later) produces one of these, and the generator does not
/// care which.
module Schema =

    /// Which `Col` helper and which `Row` reader a column maps to.
    ///
    /// Narrower than the database's own type system on purpose. `SqlValue` has
    /// nine cases and every engine spells its types differently, so the mapping
    /// collapses to what the runtime can actually distinguish.
    type ColumnKind =
        | KInt
        | KFloat
        | KDecimal
        | KText
        | KBool
        | KDate
        | KGuid
        | KBlob

    type ColumnInfo =
        { Name: string
          Kind: ColumnKind
          IsNullable: bool
          IsPrimaryKey: bool }

    type ForeignKey =
        {
            /// The key's column pairs, (own column, referenced column), in key
            /// order. One entry for an ordinary key; a composite key carries
            /// them all, so the generated join condition covers the whole key
            /// instead of pairing its columns off wrongly one by one.
            Columns: (string * string)[]
            ReferencesTable: string
        }

    type TableInfo =
        {
            Name: string
            Columns: ColumnInfo[]
            ForeignKeys: ForeignKey[]
            /// A view reads like a table and generates the same columns and
            /// mapper, but has no key and nothing to write through, so the
            /// generated module says so.
            IsView: bool
        }

    type Database = { Tables: TableInfo[] }

    /// Maps a database type name to a kind.
    ///
    /// Matched on a lower-cased prefix rather than exactly, because the engines
    /// decorate: PostgreSQL reports `character varying`, SQLite hands back
    /// whatever the DDL said (`VARCHAR(100)`, `DECIMAL(18,4)`), MySQL adds
    /// `unsigned`. Anything unrecognised is text, which is what the runtime
    /// stores an unknown value as anyway.
    let kindOfDbType (dbType: string) : ColumnKind =
        let t = dbType.ToLowerInvariant().Trim()

        let starts (prefix: string) = t.StartsWith prefix
        let has (needle: string) = t.Contains needle

        if starts "bool" || starts "bit" then
            KBool
        elif starts "uuid" || starts "uniqueidentifier" then
            KGuid
        // Checked before the integer prefixes: `numeric` and `decimal` are exact
        // and must not become floats, which is the whole reason SqlValue has a
        // decimal case.
        elif starts "numeric" || starts "decimal" || starts "money" then
            KDecimal
        elif
            starts "int"
            || starts "serial"
            || starts "bigint"
            || starts "smallint"
            || starts "tinyint"
        then
            KInt
        elif starts "real" || starts "double" || starts "float" then
            KFloat
        elif starts "timestamp" || starts "datetime" || starts "date" || starts "time" then
            KDate
        elif starts "bytea" || starts "blob" || starts "binary" || has "varbinary" then
            KBlob
        else
            KText
