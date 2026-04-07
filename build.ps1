param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$SkipTests,

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$WorkingDirectory = $repoRoot
    )

    Write-Host ">> [$WorkingDirectory] dotnet $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & dotnet @Arguments
    }
    finally {
        Pop-Location
    }

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$commonArgs = @()
if ($NoRestore) {
    $commonArgs += "--no-restore"
}

$appBuildArgs = @(
    "build",
    "src/KeystrokeApp/KeystrokeApp.csproj",
    "-c", $Configuration,
    "-nologo"
) + $commonArgs
Invoke-Step -Arguments $appBuildArgs

$hookBuildArgs = @(
    "build",
    "src/KeystrokeHook/KeystrokeHook.csproj",
    "-c", $Configuration,
    "-nologo"
) + $commonArgs
Invoke-Step -Arguments $hookBuildArgs

if (-not $SkipTests) {
    $testArgs = @(
        "test",
        "tests/KeystrokeApp.Tests/KeystrokeApp.Tests.csproj",
        "-c", $Configuration,
        "-nologo"
    ) + $commonArgs
    Invoke-Step -Arguments $testArgs
}
