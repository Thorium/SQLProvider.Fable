namespace SQLProvider.Fable

open System

/// A database value, normalised to a closed set.
///
/// ADO.NET carries values as `obj` plus a `DBNull` sentinel. On the Rust target
/// `obj` becomes `Lrc<dyn Any>`, so every column read would be a downcast and
/// every null check a type test. A union costs one match instead, and lets the
/// exact same query and mapping code compile for both backends.
///
/// The first six cases are SQLite's storage classes -- what a driver can hand
/// back without being told the schema. The last three are what a *schema* says a
/// column means: a DECIMAL(18,4) column is a `decimal` field, not a float, and a
/// money value that round-trips through `float` is already wrong. Backends encode
/// them per their own conventions (for SQLite, all three become TEXT).
type SqlValue =
    | SqlNull
    | SqlBool of bool
    | SqlInt of int64
    | SqlFloat of float
    | SqlText of string
    | SqlBlob of byte[]
    | SqlDecimal of decimal
    | SqlDate of DateTime
    | SqlGuid of Guid

module SqlValue =

    /// Stable numeric tag. The Rust binding passes this across the native
    /// boundary instead of the union itself, so the hand-written .rs shim never
    /// has to know Fable's generated type layout. Tags 6-8 never reach the shim:
    /// connectors encode them to one of the storage classes first.
    let kind v =
        match v with
        | SqlNull -> 0
        | SqlBool _ -> 1
        | SqlInt _ -> 2
        | SqlFloat _ -> 3
        | SqlText _ -> 4
        | SqlBlob _ -> 5
        | SqlDecimal _ -> 6
        | SqlDate _ -> 7
        | SqlGuid _ -> 8

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
        | SqlDecimal _ -> "decimal"
        | SqlDate _ -> "date"
        | SqlGuid _ -> "guid"

/// Culture-independent text conversion.
///
/// Hand-rolled on purpose. The obvious approach -- `Decimal.Parse(s, CultureInfo
/// .InvariantCulture)` -- does not compile on Fable's Rust backend, where the
/// culture-taking overloads are not modelled and `CultureInfo.InvariantCulture`
/// itself lowers to unit (GAP-15). The culture-*less* `Decimal.Parse s` is worse
/// than unavailable: it silently depends on the caller's locale and throws
/// outright under a comma-separator culture such as fi-FI.
///
/// F#'s `string` operator *is* invariant on both platforms (verified: it yields
/// "1234.5678" under fi-FI while `.ToString()` yields "1234,5678"), so it is the
/// encoder here and these are its inverses.
module Convert =

    let private isDigit (c: char) = c >= '0' && c <= '9'

    let private allDigits (s: string) =
        let mutable ok = true
        let mutable i = 0

        while ok && i < s.Length do
            if not (isDigit s.[i]) then
                ok <- false

            i <- i + 1

        ok

    /// Invariant decimal formatting. Never yields exponent notation.
    let decimalToText (d: decimal) : string = string d

    /// Inverse of decimalToText. Returns None rather than throwing, so a mapper
    /// can report which column was bad.
    let tryParseDecimal (s: string) : decimal option =
        if String.IsNullOrEmpty s then
            None
        else

            let negative = s.[0] = '-'
            let body = if negative || s.[0] = '+' then s.Substring 1 else s

            if body.Length = 0 then
                None
            else

                let dot = body.IndexOf '.'
                let intPart = if dot < 0 then body else body.Substring(0, dot)
                let fracPart = if dot < 0 then "" else body.Substring(dot + 1)

                if intPart.Length = 0 && fracPart.Length = 0 then
                    None
                elif not ((allDigits intPart) && (allDigits fracPart)) then
                    None
                else
                    // Accumulate every digit into a decimal mantissa, then scale down by
                    // a power of ten. Both steps are exact in base-10 arithmetic, and
                    // accumulating in `decimal` rather than int64 keeps the full 28-digit
                    // range instead of overflowing at 19.
                    let mutable mantissa = 0M

                    for i in 0 .. intPart.Length - 1 do
                        mantissa <- mantissa * 10M + decimal (int intPart.[i] - int '0')

                    for i in 0 .. fracPart.Length - 1 do
                        mantissa <- mantissa * 10M + decimal (int fracPart.[i] - int '0')

                    let mutable scale = 1M

                    for _ in 1 .. fracPart.Length do
                        scale <- scale * 10M

                    let value = mantissa / scale
                    Some(if negative then -value else value)

    /// Left-pads with zeros to `width`. Hand-rolled because `PadLeft` and the
    /// numeric format strings are not uniformly available across the targets.
    let private pad (width: int) (value: int) : string =
        let digits = string value
        let mutable out = digits

        while out.Length < width do
            out <- "0" + out

        out

    /// Round-trip ISO-8601, matching what DateTime.ToString "o" produces on
    /// .NET: `yyyy-MM-ddTHH:mm:ss.fffffff`.
    ///
    /// Built by hand rather than through `ToString "o"` because the targets do
    /// not agree on it -- Fable's JavaScript backend goes through
    /// `Date.toISOString` and emits three fractional digits where .NET and Rust
    /// emit seven. That is exactly the kind of divergence that makes two
    /// backends write different bytes for the same value, so the format is
    /// spelled out here instead of borrowed.
    let dateToText (d: DateTime) : string =
        // Ticks are 100ns units; the remainder within a second is the seven
        // fractional digits.
        let fraction = int (d.Ticks % 10000000L)

        pad 4 d.Year
        + "-"
        + pad 2 d.Month
        + "-"
        + pad 2 d.Day
        + "T"
        + pad 2 d.Hour
        + ":"
        + pad 2 d.Minute
        + ":"
        + pad 2 d.Second
        + "."
        + pad 7 fraction

    /// Inverse of dateToText. Accepts the "o" shape with or without the
    /// fractional part and with or without a trailing Z.
    let tryParseDate (s: string) : DateTime option =
        if String.IsNullOrEmpty s || s.Length < 19 then
            None
        else

            let part (start: int) (len: int) = s.Substring(start, len)

            let shapeOk =
                allDigits (part 0 4)
                && s.[4] = '-'
                && allDigits (part 5 2)
                && s.[7] = '-'
                && allDigits (part 8 2)
                && (s.[10] = 'T' || s.[10] = ' ')
                && allDigits (part 11 2)
                && s.[13] = ':'
                && allDigits (part 14 2)
                && s.[16] = ':'
                && allDigits (part 17 2)

            if not shapeOk then
                None
            else

                // Integer parsing carries no separators, so it is locale-independent.
                let year = Int32.Parse(part 0 4)
                let month = Int32.Parse(part 5 2)
                let day = Int32.Parse(part 8 2)
                let hour = Int32.Parse(part 11 2)
                let minute = Int32.Parse(part 14 2)
                let second = Int32.Parse(part 17 2)

                if
                    month < 1
                    || month > 12
                    || day < 1
                    || day > 31
                    || hour > 23
                    || minute > 59
                    || second > 59
                then
                    None
                else

                    let baseDate = DateTime(year, month, day, hour, minute, second)

                    // optional ".fffffff"
                    if s.Length > 19 && s.[19] = '.' then
                        let mutable last = 20

                        while last < s.Length && isDigit s.[last] do
                            last <- last + 1

                        let digits = s.Substring(20, last - 20)

                        if digits.Length = 0 then
                            Some baseDate
                        else
                            // pad or truncate to 100ns ticks
                            let mutable ticks = 0L

                            for i in 0..6 do
                                let d = if i < digits.Length then int digits.[i] - int '0' else 0
                                ticks <- ticks * 10L + int64 d

                            // Constructed from a tick count rather than `AddTicks`, which
                            // Fable maps for DateTimeOffset but not for DateTime on the
                            // BEAM target. Both produce a Kind of Unspecified, matching
                            // baseDate.
                            Some(DateTime(baseDate.Ticks + ticks))
                    else
                        Some baseDate

    let guidToText (g: Guid) : string = string g

    let tryParseGuid (s: string) : Guid option =
        // Guid parsing involves no numeric separators, so it is culture-free on
        // both platforms.
        if String.IsNullOrEmpty s then
            None
        else
            try
                Some(Guid.Parse s)
            with _ ->
                None
