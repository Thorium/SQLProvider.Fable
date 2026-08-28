# Compiles the smoke suite to Rust and runs it against sqlx.
#
#   pwsh tests/Rust/build.ps1
#   pwsh tests/Rust/build.ps1 -Url "postgres://user:pw@localhost/testdb"
#   pwsh tests/Rust/build.ps1 -Url "mysql://user:pw@localhost/testdb"
#
# The default is a private in-memory SQLite database, which needs nothing
# installed. A server URL must point at an empty database: the suite creates its
# own tables and does not drop them.
#
# Requires Fable built from main (see GAPS.md): the released 5.15.0 predates the
# merged quotation work. Set FABLE_LOCAL_DLL to point at it.
param(
    [string]$Url = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'build/rust'
$fable = $env:FABLE_LOCAL_DLL

if (-not $fable) {
    $fable = Join-Path $env:TEMP 'claude/fable-local/Fable.dll'
}

if (-not (Test-Path $fable)) {
    throw "Set FABLE_LOCAL_DLL to a Fable.dll built from main. See GAPS.md."
}

if (Test-Path "$env:USERPROFILE\.cargo\bin") {
    $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
}

Push-Location $root
try {
    dotnet $fable tests/Rust --lang rust --outDir build/rust --noCache
    if ($LASTEXITCODE -ne 0) { throw "fable failed" }

    # Fable emits the `#[path]` module declaration for sqlx_native.rs but does
    # not copy the file, and never writes a Cargo.toml, so both are staged here.
    New-Item -ItemType Directory -Force (Join-Path $out 'src/SQLProvider.Fable.Rust') | Out-Null

    Copy-Item 'src/SQLProvider.Fable.Rust/sqlx_native.rs' `
              (Join-Path $out 'src/SQLProvider.Fable.Rust/sqlx_native.rs') -Force

    Copy-Item 'tests/Rust/Cargo.toml' (Join-Path $out 'Cargo.toml') -Force

    Push-Location $out
    try {
        # cargo writes its progress and any warnings to stderr, which under
        # 'Stop' PowerShell would turn into a terminating NativeCommandError even
        # on a clean build. The exit code is what decides here.
        $ErrorActionPreference = 'Continue'

        if ($Url) {
            cargo run --quiet -- $Url
        }
        else {
            cargo run --quiet
        }

        exit $LASTEXITCODE
    }
    finally { Pop-Location }
}
finally { Pop-Location }
