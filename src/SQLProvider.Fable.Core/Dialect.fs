namespace SQLProvider.Fable

module Dialect =

    /// The scheme of a connection URL: everything before "://", lower-cased.
    /// "" if there is no scheme at all.
    let scheme (url: string) =
        let i = url.IndexOf "://"

        if i >= 0 then
            url.Substring(0, i).ToLowerInvariant()
        else
            // sqlx also takes the schemeless-authority form, "sqlite::memory:".
            let j = url.IndexOf ":"
            if j < 0 then "" else url.Substring(0, j).ToLowerInvariant()

    /// The placeholder style a connection URL implies. PostgreSQL numbers its
    /// markers; SQLite and MySQL/MariaDB take a bare `?`. An unrecognised scheme
    /// gets the positional form, which is the more common of the two.
    let forUrl (url: string) =
        match scheme url with
        | "postgres"
        | "postgresql" -> Numbered
        | _ -> Positional

    /// The engine a connection URL points at.
    let vendorOf (url: string) =
        match scheme url with
        | "postgres"
        | "postgresql" -> Postgres
        | "mysql"
        | "mariadb" -> MySql
        | "sqlite" -> Sqlite
        | _ -> Generic

    let private isNameChar (c: char) =
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c = '_'

    /// Rewrites the `@name` placeholders in `sql` into `style`, and returns the
    /// parameter values in the order that style expects.
    ///
    /// A name used more than once produces one entry per use for the positional
    /// styles, since neither can refer back to an earlier one. Unknown names are
    /// left untouched: an `@` that is not a parameter (an email in a literal, a
    /// MySQL user variable) must survive unchanged.
    let bind (style: Placeholder) (sql: string) (ps: SqlParam[]) : string * SqlValue[] =
        if style = Named then
            sql, ps |> Array.map (fun p -> p.Value)
        else

            let lookup name =
                ps |> Array.tryFind (fun p -> p.Name = name) |> Option.map (fun p -> p.Value)

            let out = System.Text.StringBuilder()
            let ordered = ResizeArray<SqlValue>()
            let mutable i = 0

            while i < sql.Length do
                if sql.[i] = '@' && i + 1 < sql.Length && isNameChar sql.[i + 1] then
                    let start = i + 1
                    let mutable last = start

                    while last < sql.Length && isNameChar sql.[last] do
                        last <- last + 1

                    let name = sql.Substring(start, last - start)

                    match lookup name with
                    | Some value ->
                        ordered.Add value

                        match style with
                        | Numbered -> out.Append('$').Append(ordered.Count) |> ignore
                        | Named | Positional -> out.Append('?') |> ignore

                        i <- last
                    | None ->
                        // Not one of ours; copy it through verbatim.
                        out.Append(sql.Substring(i, last - i)) |> ignore
                        i <- last
                else
                    out.Append(sql.[i]) |> ignore
                    i <- i + 1

            out.ToString(), ordered.ToArray()
