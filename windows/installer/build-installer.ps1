<#
.SYNOPSIS
    Publishes the app self-contained and builds the installer.

.DESCRIPTION
    Two steps that must not drift apart: the .iss file reads from build\publish and
    takes its version from the built assembly, so publishing and packaging happen
    together or not at all.

    The native DLLs are NOT built here. Run native\build-native.ps1 first; this script
    fails early if its output is missing rather than shipping an installer whose app
    cannot transcribe.

.PARAMETER Configuration
    Build configuration. Release for anything you intend to hand to someone.

.PARAMETER SkipPublish
    Package whatever is already in build\publish. For iterating on the .iss file.

.EXAMPLE
    ./installer/build-installer.ps1
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$windows = Split-Path -Parent $here
$publish = Join-Path $windows 'build\publish'
$project = Join-Path $windows 'src\OpenSuperWhisper.App\OpenSuperWhisper.App.csproj'

# --- the native layer has to exist first -------------------------------------------

$native = Join-Path $windows 'artifacts\native\whisper.dll'
if (-not (Test-Path $native)) {
    throw "whisper.dll is missing. Run native/build-native.ps1 -Configuration $Configuration first."
}

# --- publish ------------------------------------------------------------------------

if (-not $SkipPublish) {
    Write-Host 'publishing self-contained...' -ForegroundColor Cyan

    # Self-contained, and deliberately NOT trimmed or single-file. Trimming removes
    # types WPF only reaches by reflection, and the failure shows up the first time
    # something is drawn rather than at build time - the same class of problem as the
    # InvariantGlobalization one at M5.
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    dotnet publish $project `
        --configuration $Configuration `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        --output $publish `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path (Join-Path $publish 'OpenSuperWhisper.exe'))) {
    throw "No published app in $publish."
}

# Guard against shipping an installer that cannot transcribe: these arrive by
# different routes (the native build, and a publish-only ItemGroup) and either can be
# silently absent.
foreach ($required in 'whisper.dll', 'ggml-tiny.en.bin', 'ggml-silero-v5.1.2.bin') {
    if (-not (Test-Path (Join-Path $publish $required))) {
        throw "$required is missing from the publish output."
    }
}

# --- version, taken from what was actually built -------------------------------------

$exe = Get-Item (Join-Path $publish 'OpenSuperWhisper.exe')
$version = $exe.VersionInfo.FileVersion

# Inno wants x.y.z(.w); FileVersion is already in that shape.
if (-not $version) { throw 'Could not read the version from the published executable.' }

Write-Host "version: $version" -ForegroundColor Cyan
$size = (Get-ChildItem $publish -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("payload: {0:N0} MB across {1} files" -f $size, (Get-ChildItem $publish -Recurse -File).Count)

# --- compile the installer ------------------------------------------------------------

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if ($null -eq $iscc) {
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { $iscc = $candidate; break }
    }
}
else { $iscc = $iscc.Source }

if (-not $iscc) {
    throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup'
}

Write-Host 'compiling installer...' -ForegroundColor Cyan

& $iscc "/DAppVersion=$version" (Join-Path $here 'OpenSuperWhisper.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$output = Get-ChildItem (Join-Path $windows 'build\installer') -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ''
Write-Host ("installer: {0} ({1:N0} MB)" -f $output.FullName, ($output.Length / 1MB)) -ForegroundColor Green
Write-Host ''
Write-Host 'NOT SIGNED. Authenticode signing is a separate step, and until it happens'
Write-Host 'SmartScreen will warn on every download. See docs/windows-port.md, M7.'
