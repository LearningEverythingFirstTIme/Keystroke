param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$NoBuild,

    [switch]$NoRestore,

    [string]$Filter,

    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$Verbosity = "minimal"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

$arguments = @(
    "test",
    "tests/KeystrokeApp.Tests/KeystrokeApp.Tests.csproj",
    "-c", $Configuration,
    "-v", $Verbosity,
    "-nologo"
)

if ($NoBuild) {
    $arguments += "--no-build"
}

if ($NoRestore) {
    $arguments += "--no-restore"
}

if ($Filter) {
    $arguments += "--filter"
    $arguments += $Filter
}

Write-Host ">> [$repoRoot] dotnet $($arguments -join ' ')"
Push-Location $repoRoot
try {
    & dotnet @arguments
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
