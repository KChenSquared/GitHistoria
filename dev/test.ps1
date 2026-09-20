<#
.SYNOPSIS
    Runs Historia test suites by performance tier.

.DESCRIPTION
    Wraps 'dotnet test' with predefined filters matching Historia's test tier architecture:
      - fast    : Pure unit tests touching neither git nor disk (< 10-20s, every save/pre-commit)
      - git     : Integration tests creating git fixtures (< 90s, pre-push)
      - ui      : Headless Avalonia UI tests in Historia.App.Tests
      - slow    : Long-running or manual-fixture tests
      - all     : Full Historia.Tests test suite

.PARAMETER Tier
    The test tier to run: fast (default), git, ui, slow, all.

.PARAMETER NoBuild
    Do not build the test projects before running (saves build latency if already built).

.PARAMETER Project
    Optional path to the project or solution to test. Defaults based on Tier.

.EXAMPLE
    .\dev\test.ps1
    .\dev\test.ps1 -Tier fast -NoBuild
    .\dev\test.ps1 -Tier git
    .\dev\test.ps1 -Tier all
#>
[CmdletBinding()]
param(
    [ValidateSet("fast", "git", "ui", "slow", "all")]
    [string]$Tier = "fast",

    [switch]$NoBuild,

    [string]$Project,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$targetProject = $Project
if (-not $targetProject) {
    if ($Tier -eq "ui") {
        $targetProject = Join-Path $repoRoot "tests/Historia.App.Tests"
    } else {
        $targetProject = Join-Path $repoRoot "tests/Historia.Tests"
    }
}

$dotnetArgs = @("test", $targetProject)

if ($NoBuild) {
    $dotnetArgs += "--no-build"
}

switch ($Tier) {
    "fast" {
        $dotnetArgs += @("--filter", "tier=unit")
    }
    "git" {
        $dotnetArgs += @("--filter", "tier=git")
    }
    "ui" {
        $dotnetArgs += @("--filter", "tier=ui")
    }
    "slow" {
        $dotnetArgs += @("--filter", "tier=slow")
    }
    "all" {
        # Runs full target test suite
    }
}

if ($AdditionalArgs) {
    $dotnetArgs += $AdditionalArgs
}

Write-Host "Running: dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet @dotnetArgs
$exitCode = $LASTEXITCODE
$sw.Stop()

Write-Host ("`nTier '{0}' completed in {1:F2}s with exit code {2}" -f $Tier, $sw.Elapsed.TotalSeconds, $exitCode)
exit $exitCode
