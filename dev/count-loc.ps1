<#
.SYNOPSIS
    Counts lines of code across the Historia solution, split by comments, tests, and functional code.
.DESCRIPTION
    Executes count-loc.py and outputs to console and dev/loc-report.md.
#>
[CmdletBinding()]
param(
    [string]$Output = "$PSScriptRoot/loc-report.md",
    [string]$Root = (Resolve-Path "$PSScriptRoot/..").Path
)

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) {
    Write-Error "Python 3 is required to run count-loc.py"
    exit 1
}

& $python "$PSScriptRoot/count-loc.py" --root $Root --output $Output
