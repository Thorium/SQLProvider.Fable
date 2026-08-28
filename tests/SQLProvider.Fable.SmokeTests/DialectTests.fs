/// Tests for the placeholder rewriting.
///
/// Pure logic and no database, so this runs everywhere the rest of the library
/// does and is the one part of the multi-vendor story that can be verified on
/// this machine without a server. Every backend routes its SQL through
/// `Dialect.bind`, so a bug here is a bug in all of them at once.
module SQLProvider.Fable.SmokeTests.DialectTests

open SQLProvider.Fable
open SQLProvider.Fable.SmokeTests.Harness

let private render (sql: string, values: SqlValue[]) =
    let vs =
        values
        |> Array.map (fun v ->
            match v with
            | SqlInt i -> string i
            | SqlText s -> s
            | SqlNull -> "null"
            | other -> SqlValue.typeName other)
        |> String.concat ","

    sql + " | " + vs

let run () : TestResult[] =
    let results = ResizeArray<TestResult>()

    let ps = [| Sql.pInt "id" 1; Sql.pText "name" "Alfreds"; Sql.pInt "limit" 10 |]

    // Named leaves everything alone; the driver matches on the names, so the
    // values go down in the order they were supplied, not in order of use.
    results.Add(
        check
            "named keeps the sql and the given order"
            "SELECT * FROM T WHERE Id = @id AND Name = @name | 1,Alfreds,10"
            (render (Dialect.bind Named "SELECT * FROM T WHERE Id = @id AND Name = @name" ps))
    )

    results.Add(
        check
            "positional numbers by use"
            "SELECT * FROM T WHERE Id = ? AND Name = ? | 1,Alfreds"
            (render (Dialect.bind Positional "SELECT * FROM T WHERE Id = @id AND Name = @name" ps))
    )

    results.Add(
        check
            "numbered counts from one"
            "SELECT * FROM T WHERE Id = $1 AND Name = $2 | 1,Alfreds"
            (render (Dialect.bind Numbered "SELECT * FROM T WHERE Id = @id AND Name = @name" ps))
    )

    // Order of use, not order of declaration: @name is declared second but used
    // first, so it must be $1.
    results.Add(
        check
            "ordering follows the sql, not the parameter array"
            "SELECT * FROM T WHERE Name = $1 AND Id = $2 | Alfreds,1"
            (render (Dialect.bind Numbered "SELECT * FROM T WHERE Name = @name AND Id = @id" ps))
    )

    // Neither `?` nor `$n` can refer back to an earlier binding, so a name used
    // twice has to be sent twice.
    results.Add(
        check
            "a repeated name is sent once per use"
            "SELECT * FROM T WHERE A > $1 OR B < $2 | 10,10"
            (render (Dialect.bind Numbered "SELECT * FROM T WHERE A > @limit OR B < @limit" ps))
    )

    results.Add(
        check
            "a repeated name is sent once per use, positional"
            "SELECT * FROM T WHERE A > ? OR B < ? | 10,10"
            (render (Dialect.bind Positional "SELECT * FROM T WHERE A > @limit OR B < @limit" ps))
    )

    // An @ that is not one of ours must survive: an address inside a literal, a
    // MySQL user variable, an @@ system variable.
    results.Add(
        check
            "unknown @names are left alone"
            "SELECT '@example' , @@version, @unbound WHERE Id = $1 | 1"
            (render (Dialect.bind Numbered "SELECT '@example' , @@version, @unbound WHERE Id = @id" ps))
    )

    // `@` followed by something that cannot start a name is not a placeholder.
    results.Add(
        check
            "a bare @ is not a placeholder"
            "SELECT a @ b, c@ | " // trailing @ at end of string must not read past it
            (render (Dialect.bind Numbered "SELECT a @ b, c@" ps))
    )

    // A prefix of a longer name must not match: @id must not fire on @identity.
    results.Add(
        check
            "names match in full, not by prefix"
            "SELECT @identity, $1 | 1"
            (render (Dialect.bind Numbered "SELECT @identity, @id" ps))
    )

    results.Add(
        check
            "no parameters at all"
            "SELECT COUNT(*) FROM T | "
            (render (Dialect.bind Numbered "SELECT COUNT(*) FROM T" [||]))
    )

    // Two digits, to catch a numbering scheme that only ever formats one.
    let many = Array.init 11 (fun i -> Sql.pInt ("p" + string i) i)
    let manySql = many |> Array.map (fun p -> "@" + p.Name) |> String.concat "+"
    let boundSql, boundValues = Dialect.bind Numbered manySql many

    results.Add(check "ten or more parameters keep numbering" "$1+$2+$3+$4+$5+$6+$7+$8+$9+$10+$11" boundSql)
    results.Add(check "ten or more parameters keep their values" "11" (string boundValues.Length))

    // A NULL is a value like any other and must still take a slot.
    results.Add(
        check
            "null takes a placeholder slot"
            "INSERT INTO T VALUES ($1, $2) | 1,null"
            (render (
                Dialect.bind Numbered "INSERT INTO T VALUES (@id, @missing)" [| Sql.pInt "id" 1; Sql.pNull "missing" |]
            ))
    )

    // --- the URL a connector is opened with picks its style ----------------

    let styleName (s: Placeholder) =
        match s with
        | Named -> "Named"
        | Positional -> "Positional"
        | Numbered -> "Numbered"

    let urlCases =
        [| "sqlite::memory:", "sqlite", "Positional"
           "sqlite://data/app.db", "sqlite", "Positional"
           "postgres://user:pw@localhost/testdb", "postgres", "Numbered"
           "postgresql://user:pw@localhost/testdb", "postgresql", "Numbered"
           // Case in a scheme is not significant, and a password may contain
           // a colon -- neither must change the answer.
           "POSTGRES://user:p:w@localhost/testdb", "postgres", "Numbered"
           "mysql://user:pw@localhost/testdb", "mysql", "Positional"
           "mariadb://user:pw@localhost/testdb", "mariadb", "Positional"
           "no-scheme-at-all", "", "Positional" |]

    for url, expectedScheme, expectedStyle in urlCases do
        results.Add(check ("scheme of " + url) expectedScheme (Dialect.scheme url))
        results.Add(check ("style of " + url) expectedStyle (styleName (Dialect.forUrl url)))

    results.ToArray()
