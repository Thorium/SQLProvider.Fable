/// The tiny assertion harness the portable test files share.
///
/// Not xunit: these files are compiled by Fable to Rust as well as run on .NET,
/// so they cannot depend on a test framework. Everything is compared as text so
/// a failure can report what it actually got.
module SQLProvider.Fable.SmokeTests.Harness

type TestResult =
    { Name: string
      Passed: bool
      Detail: string }

let pass name =
    { Name = name
      Passed = true
      Detail = "" }

let fail name detail =
    { Name = name
      Passed = false
      Detail = detail }

let check name expected actual =
    if expected = actual then
        pass name
    else
        fail name ("expected " + expected + ", got " + actual)

/// Asserts a condition without stringifying it. `string true` is "True" on
/// .NET and Rust but "true" on JavaScript, so a boolean must never reach
/// `check`.
let isTrue name condition =
    if condition then
        pass name
    else
        fail name "expected the condition to hold"

/// Renders results as text, for the Rust runner (which has no test framework).
let report (results: TestResult[]) =
    let failed = results |> Array.filter (fun r -> not r.Passed)

    let lines =
        results
        |> Array.map (fun r ->
            if r.Passed then
                "  PASS  " + r.Name
            else
                "  FAIL  " + r.Name + " -- " + r.Detail)

    let summary =
        "  "
        + string (results.Length - failed.Length)
        + "/"
        + string results.Length
        + " passed"

    String.concat "\n" (Array.append lines [| summary |])
