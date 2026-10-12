<#
.SYNOPSIS
  Prepares a BetterNotepad release: version bump, tests, single-file exe, checksum, release notes and a clean
  export of the public tree. Nothing is pushed and nothing is published.

.DESCRIPTION
  1. Sets <VersionPrefix> in Directory.Build.props (single source of the version).
  2. Exports the tracked files of the current main (plus the bumped Directory.Build.props) into
     artifacts\public-repo: a fresh git repository with ONE commit "BetterNotepad <version>" authored by
     NemeWT10, remote origin = https://github.com/NemeWT10/BetterNotepad.git (not pushed).
  3. Clones that export into a scratch folder and, from there, builds Release, runs the unit tests and
     publishes the self-contained single-file exe - so what ships is exactly what the public repo contains.
  4. Writes artifacts\release\<version>\BetterNotepad.exe, BetterNotepad.exe.sha256 ("<lowercase hex>  BetterNotepad.exe",
     the format the in-app updater reads) and release-notes.md.
  5. Smoke-tests the exe on a private hidden desktop (tools\HiddenDesktopRunner, --test-mode).
  6. Prints a summary and the commands to run once the release is approved.

  Directory.Build.props in the working tree is the only file the script changes; commit it when you are happy.
  Tracked files must otherwise be clean (the exe is built from the export of HEAD), unless -AllowDirty is given.

.PARAMETER Version
  SemVer x.y.z (no leading "v"). The git tag / GitHub release will be v<Version>.

.PARAMETER PreRelease
  Marks the GitHub release as a pre-release (the printed gh command gets --prerelease).

.PARAMETER Notes
  Release notes text (markdown) used instead of the default feature list.

.PARAMETER NotesFile
  Path to a markdown file used instead of the default feature list.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $PreRelease,
    [string] $Notes,
    [string] $NotesFile,
    [switch] $SkipTests,
    [switch] $SkipSmoke,
    [switch] $AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$pubRepo = Join-Path $artifacts 'public-repo'
$releaseRoot = Join-Path $artifacts 'release'
$relDir = Join-Path $releaseRoot $Version
$work = Join-Path $releaseRoot ('.work-' + $Version)
$props = Join-Path $root 'Directory.Build.props'
$remoteUrl = 'https://github.com/NemeWT10/BetterNotepad.git'
$authorName = 'NemeWT10'
$authorEmail = '208810650+NemeWT10@users.noreply.github.com'
$exeName = 'BetterNotepad.exe'

# Local-only names that must not appear anywhere in the export.
$internalNames = @(('CLAU' + 'DE.md'), ('PROJ' + 'EKT.md'), ('.cla' + 'ude'), ('Co-Author' + 'ed-By'))
$internalPathRx = '(^|/)(' + (($internalNames[0], $internalNames[1], $internalNames[2], 'artifacts', 'worktrees' | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(/|$)|^tests/output(/|$)'

function Step([string] $text) { Write-Host ''; Write-Host ('== ' + $text) -ForegroundColor Cyan }

function Invoke-Native {
    param([string] $What, [string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

function Get-GitLines {
    param([string] $Dir, [string[]] $Arguments)
    $out = & git -C $Dir @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed (exit code $LASTEXITCODE)" }
    return @($out | Where-Object { $null -ne $_ })
}

function Remove-Inside {
    param([string] $Path, [string] $Boundary)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $p = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
    $b = (Resolve-Path -LiteralPath $Boundary).Path.TrimEnd('\')
    if (-not $p.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to delete outside $b : $p" }
    Remove-Item -LiteralPath $p -Recurse -Force
}

function Write-Utf8NoBom {
    param([string] $Path, [string] $Text)
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------- checks
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
foreach ($tool in 'git', 'dotnet') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on PATH." } }

$branch = @(Get-GitLines $root @('rev-parse', '--abbrev-ref', 'HEAD'))[0]
if ($branch -ne 'main') { throw "Release from main only (current branch: $branch)." }

$dirty = @(Get-GitLines $root @('status', '--porcelain', '--untracked-files=no')) | Where-Object { $_ -and $_ -notmatch 'Directory\.Build\.props$' }
if ($dirty -and -not $AllowDirty) {
    throw ("Tracked files have uncommitted changes (the exe is built from HEAD):`n" + ($dirty -join "`n") + "`nCommit them or pass -AllowDirty.")
}

# ---------------------------------------------------------------- 1. version
Step "Version $Version"
$latin1 = [Text.Encoding]::GetEncoding(28591)   # byte-exact round trip: keeps BOM, line endings and the (c) sign untouched
$text = $latin1.GetString([IO.File]::ReadAllBytes($props))
if ($text -notmatch '<VersionPrefix>[^<]*</VersionPrefix>') { throw 'Directory.Build.props has no <VersionPrefix>.' }
$newText = [regex]::Replace($text, '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$Version</VersionPrefix>")
if ($newText -ne $text) {
    [IO.File]::WriteAllBytes($props, $latin1.GetBytes($newText))
    Write-Host "Directory.Build.props: VersionPrefix set to $Version (uncommitted)"
} else {
    Write-Host "Directory.Build.props already at $Version"
}

# ---------------------------------------------------------------- 2. public export
Step 'Public export (artifacts\public-repo)'
New-Item -ItemType Directory -Force $artifacts, $releaseRoot | Out-Null
Remove-Inside $pubRepo $artifacts
Remove-Inside $relDir $releaseRoot
Remove-Inside $work $releaseRoot
New-Item -ItemType Directory -Force $pubRepo, $relDir | Out-Null

# Tracked files of HEAD only, written byte-for-byte through a throw-away index (the real index is not touched).
$tmpIndex = Join-Path $releaseRoot ('.index-' + $Version)
$env:GIT_INDEX_FILE = $tmpIndex
try {
    Invoke-Native 'git read-tree' 'git' @('-C', $root, 'read-tree', 'HEAD')
    Invoke-Native 'git checkout-index' 'git' @('-C', $root, 'checkout-index', '-a', '-f', ('--prefix=' + $pubRepo.Replace('\', '/') + '/'))
} finally {
    Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tmpIndex) { Remove-Item -LiteralPath $tmpIndex -Force }
}
Copy-Item -LiteralPath $props -Destination (Join-Path $pubRepo 'Directory.Build.props') -Force   # the bumped version

# Defensive: nothing internal may be in the tree.
$all = @(Get-ChildItem -LiteralPath $pubRepo -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($pubRepo.Length + 1).Replace('\', '/') })
$bad = @($all | Where-Object { $_ -match $internalPathRx })
foreach ($rel in $bad) { Write-Warning "removing internal path from export: $rel"; Remove-Item -LiteralPath (Join-Path $pubRepo $rel) -Force }
$all = @($all | Where-Object { $_ -notmatch $internalPathRx })
$textExt = '.cs', '.xaml', '.xshd', '.csproj', '.props', '.json', '.md', '.ps1', '.txt', '.sln', '.manifest', '.gitignore', ''
$contentRx = ($internalNames | ForEach-Object { [regex]::Escape($_) }) -join '|'
$leaks = @()
foreach ($rel in $all) {
    $ext = [IO.Path]::GetExtension($rel).ToLowerInvariant()
    if ($textExt -notcontains $ext -and [IO.Path]::GetFileName($rel) -ne 'LICENSE') { continue }
    if (Select-String -LiteralPath (Join-Path $pubRepo $rel) -Pattern $contentRx -Quiet) { $leaks += $rel }
}
if ($leaks) { throw ("Internal names found inside public files:`n" + ($leaks -join "`n")) }
foreach ($required in 'README.md', 'LICENSE', 'BetterNotepad.sln', 'Directory.Build.props') {
    if ($all -notcontains $required) { throw "Public tree is missing $required." }
}

# One clean commit, authored as the public identity. Repo-local settings only; global git config is never touched.
Invoke-Native 'git init' 'git' @('-C', $pubRepo, 'init', '-q', '-b', 'main')
Invoke-Native 'git config' 'git' @('-C', $pubRepo, 'config', 'core.autocrlf', 'false')
Invoke-Native 'git config' 'git' @('-C', $pubRepo, 'config', 'core.safecrlf', 'false')
Invoke-Native 'git add' 'git' @('-C', $pubRepo, 'add', '-A', '-f')
Invoke-Native 'git commit' 'git' @('-C', $pubRepo, '-c', "user.name=$authorName", '-c', "user.email=$authorEmail", '-c', 'commit.gpgsign=false', 'commit', '-q', '-m', "BetterNotepad $Version")
Invoke-Native 'git remote add' 'git' @('-C', $pubRepo, 'remote', 'add', 'origin', $remoteUrl)

# The export must equal HEAD of main except for the version file.
$a = Get-GitLines $root @('ls-tree', '-r', 'HEAD')
$b = Get-GitLines $pubRepo @('ls-tree', '-r', 'HEAD')
$delta = @(Compare-Object $a $b) | Where-Object { $_.InputObject -notmatch 'Directory\.Build\.props$' }
if ($delta) { throw ("Export differs from HEAD:`n" + (($delta | ForEach-Object { $_.SideIndicator + ' ' + $_.InputObject }) -join "`n")) }
$ignoredTracked = @(Get-GitLines $pubRepo @('ls-files', '-c', '-i', ('--exclude-from=' + (Join-Path $root '.git\info\exclude'))))
if ($ignoredTracked) { throw ("Tracked files match the local exclude list:`n" + ($ignoredTracked -join "`n")) }
$commit = @(Get-GitLines $pubRepo @('rev-parse', '--short', 'HEAD'))[0]
$identity = @(Get-GitLines $pubRepo @('log', '-1', '--format=%an <%ae> / %cn <%ce>'))[0]
$body = (Get-GitLines $pubRepo @('log', '-1', '--format=%B')) -join '|'
if ($identity -ne "$authorName <$authorEmail> / $authorName <$authorEmail>") { throw "Unexpected commit identity: $identity" }
if ($body.Trim('|').Trim() -ne "BetterNotepad $Version") { throw "Unexpected commit message: $body" }
$fileCount = @(Get-GitLines $pubRepo @('ls-files')).Count
Write-Host "Export commit $commit ($fileCount files), $identity"

# ---------------------------------------------------------------- 3. build + test + publish (from the export)
Step 'Build, test, publish (from a clone of the export)'
Invoke-Native 'git clone' 'git' @('-c', 'core.autocrlf=false', 'clone', '-q', '--no-hardlinks', $pubRepo, $work)
$publishOut = Join-Path $work 'out'
Push-Location $work
try {
    Invoke-Native 'dotnet build' 'dotnet' @('build', 'BetterNotepad.sln', '-c', 'Release', '--nologo', '-v', 'q')
    if (-not $SkipTests) {
        Invoke-Native 'dotnet test' 'dotnet' @('test', 'tests\BetterNotepad.Tests', '-c', 'Release', '--no-build', '--nologo')
    }
    Invoke-Native 'dotnet publish' 'dotnet' @(
        'publish', 'src\BetterNotepad', '-c', 'Release', '-r', 'win-x64', '--self-contained',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-o', $publishOut, '--nologo', '-v', 'q')
} finally {
    Pop-Location
}
$builtExe = Join-Path $publishOut $exeName
if (-not (Test-Path -LiteralPath $builtExe)) { throw "Publish did not produce $exeName." }
$exe = Join-Path $relDir $exeName
Copy-Item -LiteralPath $builtExe -Destination $exe -Force
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if (-not $productVersion.StartsWith($Version + '+') -and $productVersion -ne $Version) { throw "Exe reports version '$productVersion', expected $Version." }
Write-Host "Exe version: $productVersion"

# ---------------------------------------------------------------- 4. checksum + notes
Step 'Checksum and release notes'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$sumFile = Join-Path $relDir ($exeName + '.sha256')
Write-Utf8NoBom $sumFile ($hash + '  ' + $exeName + "`n")

if ($NotesFile) {
    $highlights = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path).Trim()
} elseif ($Notes) {
    $highlights = $Notes.Trim()
} else {
    $highlights = @'
- Tabs with full session restore, including unsaved changes
- Syntax highlighting for 41 languages, multi-cursor and IDE-style editing
- Find and replace with regex, command palette
- Markdown preview, PDF viewer (search, print, convert to Word), hex view
- File info with checksums and metadata cleaning
- Five themes, tray icon, global hotkey, file associations
- Automatic updates from GitHub Releases
- Polish and English interface
'@
}
$notesText = "## BetterNotepad $Version`n`n"
if ($PreRelease) { $notesText += "This is a pre-release.`n`n" }
$notesText += "A fast, light notepad and code editor for Windows 11.`n`n### Highlights`n`n$highlights`n`n"
$notesText += "### Install`n`nDownload ``$exeName``, run it and choose **Install** (per-user, no administrator rights) or run it as a portable app. "
$notesText += "The executable is not code-signed, so Windows SmartScreen may show a warning: choose **More info**, then **Run anyway**.`n`n"
$notesText += "SHA-256 of ``$exeName``: ``$hash```n"
$notesFileOut = Join-Path $relDir 'release-notes.md'
Write-Utf8NoBom $notesFileOut $notesText

# ---------------------------------------------------------------- 5. smoke test on a hidden desktop
$smokeResult = 'skipped'
if (-not $SkipSmoke) {
    Step 'Smoke test (hidden desktop, test mode)'
    $runner = Join-Path $work 'tools\HiddenDesktopRunner\bin\Release\net10.0-windows\HiddenDesktopRunner.exe'
    if (-not (Test-Path -LiteralPath $runner)) { throw 'HiddenDesktopRunner was not built.' }
    $smokeRoot = Join-Path $root 'tests\output\release-smoke'
    New-Item -ItemType Directory -Force (Join-Path $root 'tests\output') | Out-Null
    Remove-Inside $smokeRoot (Join-Path $root 'tests\output')
    $smokeData = Join-Path $smokeRoot 'data'
    New-Item -ItemType Directory -Force $smokeData | Out-Null
    $sample = Join-Path $smokeRoot 'sample.txt'
    Write-Utf8NoBom $sample "BetterNotepad smoke test`n"
    & $runner --timeout 15 -- $exe --test-mode --data-dir $smokeData $sample
    $code = $LASTEXITCODE
    # 124 = the runner's timeout: the app was still running after 15 s, which is what we want.
    if ($code -ne 124) { throw "Smoke test: the app exited early (exit code $code)." }
    # The app logs only problems, so no app.log means no errors. Proof of life: the opened file is in the saved session.
    $log = @(Get-ChildItem -LiteralPath $smokeData -Recurse -Filter 'app.log' -File -ErrorAction SilentlyContinue)
    if ($log.Count -gt 0) {
        $errors = @(Select-String -LiteralPath $log[0].FullName -Pattern '\[ERROR\]')
        if ($errors.Count -gt 0) { throw ("Smoke test: errors in app.log:`n" + (($errors | Select-Object -First 5 | ForEach-Object { $_.Line }) -join "`n")) }
    }
    $session = @(Get-ChildItem -LiteralPath $smokeData -Recurse -Filter 'session.json' -File -ErrorAction SilentlyContinue)
    if ($session.Count -eq 0 -or -not (Select-String -LiteralPath $session[0].FullName -Pattern 'sample\.txt' -Quiet)) {
        throw 'Smoke test: the app did not open the sample file / save its session.'
    }
    $smokeResult = 'OK (ran 15 s on a hidden desktop, opened the sample file, no errors logged)'
    Write-Host "Smoke test: $smokeResult"
}

# ---------------------------------------------------------------- 6. summary
Remove-Inside $work $releaseRoot
Step 'Summary'
$top = Get-GitLines $pubRepo @('ls-tree', '--name-only', 'HEAD')
$exeInfo = Get-Item -LiteralPath $exe
$tag = "v$Version"
$pre = ''
if ($PreRelease) { $pre = ' --prerelease' }
Write-Host ("Version        : {0}{1}" -f $Version, $(if ($PreRelease) { ' (pre-release)' } else { '' }))
Write-Host ("Public repo    : {0}" -f $pubRepo)
Write-Host ("  commit       : {0}  {1}" -f $commit, $identity)
Write-Host ("  files        : {0}" -f $fileCount)
Write-Host ("  top level    : {0}" -f ($top -join ', '))
Write-Host ("  remote       : origin {0} (not pushed)" -f $remoteUrl)
Write-Host ("Exe            : {0}  ({1:N1} MB)" -f $exe, ($exeInfo.Length / 1MB))
Write-Host ("SHA-256        : {0}" -f $hash)
Write-Host ("Checksum file  : {0}" -f $sumFile)
Write-Host ("Release notes  : {0}" -f $notesFileOut)
Write-Host ("Tests          : {0}" -f $(if ($SkipTests) { 'skipped' } else { 'passed' }))
Write-Host ("Smoke test     : {0}" -f $smokeResult)
Write-Host ''
Write-Host 'After approval, run:' -ForegroundColor Yellow
Write-Host ('  git -C "{0}" push -u origin main' -f $pubRepo)
Write-Host ('  gh release create {0} "{1}" "{2}" --repo NemeWT10/BetterNotepad --title "BetterNotepad {3}" --notes-file "{4}"{5}' -f $tag, $exe, $sumFile, $Version, $notesFileOut, $pre)
Write-Host ''
Write-Host 'Directory.Build.props now carries the new version in the working tree; commit it on main.'
