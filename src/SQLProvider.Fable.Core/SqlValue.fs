namespace SQLProvider.Fable

/// A database value, normalised to a closed set.
///
/// ADO.NET carries values as `obj` plus a `DBNull` sentinel. On the Rust target
/// `obj` becomes `Lrc<dyn Any>`, so every column read would be a downcast and
/// every null check a type test. A union costs one match instead, and lets the
/// exact same query and mapping code compile for both backends.
///
/// The cases are deliberately SQLite's storage classes. Backends with richer
/// native types (date, decimal, uuid, arrays) will need extra cases; until then
/// `Row` carries DateTime as ISO-8601 text, which is what SQLite does anyway.
type SqlValue =
    | SqlNull
    | SqlBool of bool
    | SqlInt of int64
    | SqlFloat of float
    | SqlText of string
    | SqlBlob of byte[]

module SqlValue =

    /// Stable numeric tag. The Rust binding passes this across the native
    /// boundary instead of the union itself, so the hand-written .rs shim never
    /// has to know Fable's generated type layout.
    let kind v =
        match v with
        | SqlNull -> 0
        | SqlBool _ -> 1
        | SqlInt _ -> 2
        | SqlFloat _ -> 3
        | SqlText _ -> 4
        | SqlBlob _ -> 5

    let isNull v =
        match v with
        | SqlNull -> true
        | _ -> false

    /// Human-readable tag name, for error messages only.
    let typeName v =
        match v with
        | SqlNull -> "null"
        | SqlBool _ -> "bool"
        | SqlInt _ -> "int"
        | SqlFloat _ -> "float"
        | SqlText _ -> "text"
        | SqlBlob _ -> "blob"
