# Compiles the smoke suite to Rust and runs it.
#
#   pwsh tests/Rust/build.ps1
#
# Fable emits the `#[path]` module declaration for sqlite_native.rs but does not
# copy the file itself, and it never writes a Cargo.toml, so both are staged here.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'build/rust'

if (Test-Path "$env:USERPROFILE\.cargo\bin") {
    $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
}

$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"

Push-Location $root
try {
    fable tests/Rust --lang rust --outDir build/rust --noCache
    if ($LASTEXITCODE -ne 0) { throw "fable failed" }

    New-Item -ItemType Directory -Force (Join-Path $out 'src/SQLProvider.Fable.Rust') | Out-Null

    Copy-Item 'src/SQLProvider.Fable.Rust/sqlite_native.rs' `
              (Join-Path $out 'src/SQLProvider.Fable.Rust/sqlite_native.rs') -Force

    Copy-Item 'tests/Rust/Cargo.toml' (Join-Path $out 'Cargo.toml') -Force

    Push-Location $out
    try {
        cargo run --quiet
        exit $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally { Pop-Location }
