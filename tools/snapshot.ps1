<#
.SYNOPSIS
  UI snapshots of the app for visual QA, rendered from the app's own visual tree (never from the screen).

.DESCRIPTION
  Builds the app and HiddenDesktopRunner, then runs BetterNotepad.exe with --test-mode on a private, invisible
  Windows desktop (the user's desktop, mouse and keyboard are never involved). The app drives itself
  (Core\SnapshotMode.cs) and writes PNGs to tests\output\snap\png. Everything is verified afterwards.

  -Offscreen runs the exe directly instead (windows at -32000,-32000, not activated, no taskbar entry,
  WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW). Use only if the hidden desktop cannot render.
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [int] $TimeoutSec = 300,
    [switch] $NoBuild,
    [switch] $Offscreen,
    # only these scenario prefixes, e.g. -Only 17,19 (quick iteration; the full set is the default)
    [string[]] $Only = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\output\snap'
$png = Join-Path $out 'png'
$data = Join-Path $out 'data'
$runner = Join-Path $root 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\HiddenDesktopRunner.exe'
$exe = Join-Path $root "src\BetterNotepad\bin\$Configuration\net10.0-windows\win-x64\BetterNotepad.exe"

if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'tools\HiddenDesktopRunner') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'HiddenDesktopRunner build failed' }
    & dotnet build (Join-Path $root 'src\BetterNotepad') -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'BetterNotepad build failed' }
}
foreach ($f in @($runner, $exe)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

# fresh state; only ever inside tests\output
if (Test-Path $out) {
    $resolved = (Resolve-Path $out).Path
    if (-not $resolved.StartsWith((Join-Path $root 'tests\output'), [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to clean $resolved" }
    Remove-Item $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force $png, $data | Out-Null

$env:BETTERNOTEPAD_SNAPSHOT = $png
if ($Only.Count -gt 0) { $env:BETTERNOTEPAD_SNAPSHOT_ONLY = ($Only -join ',') } else { Remove-Item Env:\BETTERNOTEPAD_SNAPSHOT_ONLY -ErrorAction SilentlyContinue }
$sw = [Diagnostics.Stopwatch]::StartNew()
if ($Offscreen) {
    Write-Host 'mode: offscreen (no hidden desktop)'
    $p = Start-Process -FilePath $exe -ArgumentList @('--test-mode', '--data-dir', $data) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill($true); $code = 124 } else { $code = $p.ExitCode }
}
else {
    Write-Host 'mode: hidden desktop'
    & $runner --timeout $TimeoutSec -- $exe --test-mode --data-dir $data
    $code = $LASTEXITCODE
}
Remove-Item Env:\BETTERNOTEPAD_SNAPSHOT
Remove-Item Env:\BETTERNOTEPAD_SNAPSHOT_ONLY -ErrorAction SilentlyContinue
Write-Host ("exit code {0}, {1:n1} s" -f $code, $sw.Elapsed.TotalSeconds)

# verify
$problems = @()
$index = Join-Path $png 'index.txt'
$failed = Join-Path $png 'FAILED.txt'
if ($code -ne 0) { $problems += "app exit code $code" }
if (-not (Test-Path $index)) { $problems += 'index.txt missing (app did not finish)' }
if (Test-Path $failed) { $problems += 'snapshot failures:'; $problems += (Get-Content $failed) }
$pngs = @(Get-ChildItem $png -Filter *.png -ErrorAction SilentlyContinue)
if ($pngs.Count -eq 0) { $problems += 'no PNG files' }
foreach ($f in $pngs) { if ($f.Length -lt 3000) { $problems += "suspiciously small: $($f.Name) ($($f.Length) bytes)" } }
$log = Join-Path $data 'local\logs\app.log'
if (Test-Path $log) {
    $errors = @(Select-String -Path $log -Pattern 'ERROR|SNAPSHOT FAIL' | ForEach-Object { $_.Line })
    if ($errors.Count -gt 0) { $problems += 'app.log errors:'; $problems += ($errors | Select-Object -First 20) }
}

Write-Host ("{0} PNG files in {1}" -f $pngs.Count, $png)
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  ! $_" -ForegroundColor Yellow }
    if (Test-Path $log) { Write-Host '--- app.log (tail) ---'; Get-Content $log -Tail 25 }
    exit 1
}
Write-Host 'snapshot OK'
