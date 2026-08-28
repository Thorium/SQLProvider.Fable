# Compiles the shared suites to JavaScript and runs them on Node.
#
#   pwsh tests/Js/build.ps1
#
# Needs Node 22.5 or newer for the built-in `node:sqlite` module -- there is no
# npm install and nothing to compile. Requires Fable built from main (see
# GAPS.md); set FABLE_LOCAL_DLL to point at it.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'build/js'
$fable = $env:FABLE_LOCAL_DLL

if (-not $fable) {
    $fable = Join-Path $env:TEMP 'claude/fable-local/Fable.dll'
}

if (-not (Test-Path $fable)) {
    throw "Set FABLE_LOCAL_DLL to a Fable.dll built from main. See GAPS.md."
}

Push-Location $root
try {
    dotnet $fable tests/Js --lang javascript --outDir build/js --extension .mjs --noCache
    if ($LASTEXITCODE -ne 0) { throw "fable failed" }

    # Fable emits ES modules; Node needs to be told so even with the .mjs
    # extension, because the fable_modules copies are plain .js.
    Set-Content -Path (Join-Path $out 'package.json') -Value '{"type":"module"}' -Encoding utf8

    $ErrorActionPreference = 'Continue'
    node (Join-Path $out 'Program.mjs')
    exit $LASTEXITCODE
}
finally { Pop-Location }
