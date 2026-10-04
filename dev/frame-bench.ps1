<#
.SYNOPSIS
    Runs the A/B frame benchmark (Historia.exe --frame-bench) and summarises it.

.DESCRIPTION
    For each repository: one warm-up launch per build, then -Runs measured launches per build,
    alternating A B A B ..., of `--frame-bench all`; then the same for `--frame-bench terminal`.
    A launch that fails (non-zero exit) is retried once; a second failure is logged and its runs
    stay invalid, which the summariser reports as an invalid cell. Warm-ups go to
    <Out>\warmup\A|B and are not summarised; measured runs go to <Out>\A and <Out>\B.
    Finally runs `Historia.exe --frame-bench-summarize <Out> --runs <Runs>` with build A, which
    writes <Out>\summary.md.

    Refuses to start while any Historia process is running, and never stops one.
    Works on Windows PowerShell 5.1.

.EXAMPLE
    .\dev\frame-bench.ps1 -A Historia-DevDocs\bench\avalonia12\A-11.3.20 -B Historia-DevDocs\bench\avalonia12\B-12.1.3 -Out Historia-DevDocs\bench\avalonia12\runs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Runs = 5,
    [double]$Seconds = 20,
    [int]$Seed = 1,
    [string[]]$Repos = @('C:\_Testing\linux', 'C:\_Testing\chromium'),
    [string]$Data,
    [string]$Config
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Data) { $Data = Join-Path $repoRoot 'Historia-DevDocs\bench\avalonia12\data' }
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'frame-bench.json' }

function Resolve-Exe([string]$dir, [string]$label) {
    $full = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path (Get-Location) $dir }))
    $exe = Join-Path $full 'Historia.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Build $label has no Historia.exe in $full" }
    return $exe
}

function Get-FullPath([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { return [IO.Path]::GetFullPath($path) }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location) $path))
}

function Test-HistoriaRunning {
    return @(Get-Process -Name 'Historia' -ErrorAction SilentlyContinue).Count -gt 0
}

function Wait-HistoriaGone([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Test-HistoriaRunning) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    return -not (Test-HistoriaRunning)
}

function Quote([string]$s) { return '"' + $s + '"' }

$exeA = Resolve-Exe $A 'A'
$exeB = Resolve-Exe $B 'B'
$Out = Get-FullPath $Out
$Data = Get-FullPath $Data
$Config = Get-FullPath $Config
if (-not (Test-Path -LiteralPath $Config)) { throw "No benchmark config at $Config" }

if (Test-HistoriaRunning) {
    [Console]::Error.WriteLine('A Historia process is running. Close it before benchmarking; this script never stops it.')
    exit 1
}

foreach ($dir in @($Out, (Join-Path $Out 'A'), (Join-Path $Out 'B'), (Join-Path $Out 'warmup\A'), (Join-Path $Out 'warmup\B'), (Join-Path $Out 'logs'), $Data)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$launchLog = Join-Path $Out 'launches.csv'
if (-not (Test-Path -LiteralPath $launchLog)) {
    'time,repo,mode,side,run,warmup,attempt,exit,seconds' | Out-File -LiteralPath $launchLog -Encoding utf8
}
$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Launch([string]$side, [string]$repo, [string]$mode, [int]$run, [bool]$warmup) {
    $exe = if ($side -eq 'A') { $exeA } else { $exeB }
    $dest = if ($warmup) { Join-Path $Out "warmup\$side" } else { Join-Path $Out $side }
    $repoName = Split-Path -Leaf $repo
    $arguments = @(
        '--frame-bench', $mode,
        '--repo', (Quote $repo),
        '--out', (Quote $dest),
        '--data', (Quote $Data),
        '--config', (Quote $Config),
        '--run', $run,
        '--seconds', ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $Seconds)),
        '--seed', $Seed
    ) -join ' '

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if (-not (Wait-HistoriaGone 60)) {
            [Console]::Error.WriteLine('A Historia process is still running; stopping rather than touching it.')
            exit 1
        }
        $tag = '{0}-{1}-{2}-r{3}{4}-a{5}' -f $repoName, $mode, $side, $run, $(if ($warmup) { '-warmup' } else { '' }), $attempt
        $stderr = Join-Path $Out "logs\$tag.stderr.txt"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -Wait -RedirectStandardError $stderr
        $exit = $process.ExitCode
        $elapsed = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        ('{0},{1},{2},{3},{4},{5},{6},{7},{8}' -f (Get-Date -Format 's'), $repoName, $mode, $side, $run, $warmup, $attempt, $exit, $elapsed) |
            Out-File -LiteralPath $launchLog -Append -Encoding utf8
        Write-Host ('{0,-34} exit {1} in {2}s' -f $tag, $exit, $elapsed)
        if ($exit -eq 0) { return $true }
        if ($exit -eq 5) {
            [Console]::Error.WriteLine("$repo is not at its pinned HEAD (exit 5); the repositories are never reset by this script.")
            exit 5
        }
    }
    $failures.Add("$repoName $mode $side r$run$(if ($warmup) { ' (warm-up)' } else { '' }) failed twice") | Out-Null
    return $false
}

foreach ($repo in $Repos) {
    foreach ($mode in @('all', 'terminal')) {
        Invoke-Launch 'A' $repo $mode 0 $true | Out-Null
        Invoke-Launch 'B' $repo $mode 0 $true | Out-Null
        for ($i = 1; $i -le $Runs; $i++) {
            Invoke-Launch 'A' $repo $mode $i $false | Out-Null
            Invoke-Launch 'B' $repo $mode $i $false | Out-Null
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | Out-File -LiteralPath (Join-Path $Out 'failed-launches.txt') -Encoding utf8
    Write-Warning ("Launches that failed twice:`n  " + ($failures -join "`n  "))
}

$summarize = Start-Process -FilePath $exeA -ArgumentList @('--frame-bench-summarize', (Quote $Out), '--runs', $Runs) -PassThru -Wait `
    -RedirectStandardError (Join-Path $Out 'logs\summarize.stderr.txt') -RedirectStandardOutput (Join-Path $Out 'logs\summarize.stdout.txt')
if ($summarize.ExitCode -ne 0) {
    [Console]::Error.WriteLine("The summariser exited with $($summarize.ExitCode).")
    exit $summarize.ExitCode
}
Write-Host "Summary: $(Join-Path $Out 'summary.md')"
