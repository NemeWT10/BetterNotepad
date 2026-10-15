<#
.SYNOPSIS
  Tray memory / CPU measurement (budget: tray <= 50 MB private working set, 0 % CPU while idle).

.DESCRIPTION
  Runs BetterNotepad.exe --test-mode on the hidden desktop (tools\HiddenDesktopRunner) once per scenario with a fresh
  data folder under tests\output\perf. The app drives itself (Core\PerfProbe.cs): fresh = start -> hide to the tray
  -> idle -> restore; files = 10 code files + Markdown + PDF used first; hidden = autostart straight into the tray.
  Numbers (private working set = Task Manager "Memory", working set, private bytes, GC heap, threads, handles, CPU
  in the idle window, dispatcher operations, restore latency, private-WS breakdown) go to tests\output\perf\report.md.

  -Exe measures another build, e.g. the published single-file exe; its native libraries are extracted under
  tests\output\perf\extract (DOTNET_BUNDLE_EXTRACT_BASE_DIR), never to %TEMP%.
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string] $Exe = '',
    [string[]] $Scenario = @('fresh', 'files', 'hidden'),
    [int] $IdleSec = 30,
    [int] $Repeat = 1,
    [string] $Label = '',
    [int] $TimeoutSec = 300
)

$ErrorActionPreference = 'Stop'
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' }) # -File passes "a,b" as one string
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\output\perf'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\HiddenDesktopRunner.exe'
if ($Exe -eq '') { $Exe = Join-Path $root "src\BetterNotepad\bin\$Configuration\net10.0-windows\win-x64\BetterNotepad.exe" }
$Exe = (Resolve-Path $Exe).Path
foreach ($f in @($runner, $Exe)) { if (-not (Test-Path $f)) { throw "missing: $f" } }
if (-not $Exe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "the exe must live inside the repository: $Exe" }

New-Item -ItemType Directory -Force $out | Out-Null
$report = Join-Path $out 'report.md'
if ($Label -ne '') { Add-Content -Path $report -Value "`n# $Label`n" -Encoding utf8 }

$env:BETTERNOTEPAD_PERF = $report
$env:BETTERNOTEPAD_PERF_IDLE = "$IdleSec"
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $out 'extract'
try {
    foreach ($i in 1..$Repeat) {
        foreach ($s in $Scenario) {
            $data = Join-Path $out "data-$s"
            if (Test-Path $data) { Remove-Item $data -Recurse -Force }
            New-Item -ItemType Directory -Force $data | Out-Null
            $env:BETTERNOTEPAD_PERF_SCENARIO = $s
            Write-Host "scenario $s ($i/$Repeat)..."
            & $runner --timeout $TimeoutSec -- $Exe --test-mode --data-dir $data
            if ($LASTEXITCODE -ne 0) { Write-Host "  exit code $LASTEXITCODE" -ForegroundColor Yellow }
            $log = Join-Path $data 'local\logs\app.log'
            if (Test-Path $log) {
                $errors = @(Select-String -Path $log -Pattern 'ERROR|PERF FAIL' | ForEach-Object { $_.Line })
                if ($errors.Count -gt 0) { $errors | Select-Object -First 10 | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Yellow } }
            }
        }
    }
}
finally {
    Remove-Item Env:\BETTERNOTEPAD_PERF, Env:\BETTERNOTEPAD_PERF_IDLE, Env:\BETTERNOTEPAD_PERF_SCENARIO, Env:\DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
}
Write-Host "report: $report"
