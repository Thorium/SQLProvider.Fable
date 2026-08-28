# Compiles the portable half of the library to Erlang/BEAM and runs it.
#
#   pwsh tests/Beam/build.ps1
#
# There is no Erlang connector yet, so this runs the parts that need no
# database: placeholder rewriting, the shared value encoding, the row readers
# and the async builder. Requires Fable built from main (see GAPS.md); set
# FABLE_LOCAL_DLL to point at it.
#
# Erlang and rebar3 must be on PATH. On a machine where they live in WSL, run
# the two commands this script prints instead.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'build/beam'
$fable = $env:FABLE_LOCAL_DLL

if (-not $fable) {
    $fable = Join-Path $env:TEMP 'claude/fable-local/Fable.dll'
}

if (-not (Test-Path $fable)) {
    throw "Set FABLE_LOCAL_DLL to a Fable.dll built from main. See GAPS.md."
}

Push-Location $root
try {
    dotnet $fable tests/Beam --lang beam --outDir build/beam --noCache
    if ($LASTEXITCODE -ne 0) { throw "fable failed" }

    if (-not (Get-Command rebar3 -ErrorAction SilentlyContinue)) {
        Write-Host "rebar3 is not on PATH. From a shell that has it (WSL here):"
        Write-Host "  cd build/beam && rebar3 compile"
        Write-Host "  erl -noshell `$(find _build/default/lib -maxdepth 2 -type d -name ebin | sed 's|^|-pa |') -eval 'main:main()' -s init stop"
        exit 0
    }

    Push-Location $out
    try {
        $ErrorActionPreference = 'Continue'
        rebar3 compile
        if ($LASTEXITCODE -ne 0) { throw "rebar3 compile failed" }

        $paths = Get-ChildItem -Path '_build/default/lib' -Depth 1 -Directory -Filter 'ebin' |
                 ForEach-Object { '-pa'; $_.FullName }

        erl -noshell @paths -eval 'main:main()' -s init stop
        exit $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally { Pop-Location }
