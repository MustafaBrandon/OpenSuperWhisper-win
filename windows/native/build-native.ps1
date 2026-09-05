<#
.SYNOPSIS
  Builds the native dependencies for the Windows client.

.DESCRIPTION
  Produces, into windows/artifacts/native/:
    whisper.dll + ggml*.dll   from the pinned whisper.cpp submodule (CMake/MSVC)
    autocorrect_swift.dll     from the asian-autocorrect submodule (cargo)

  Run this before building the solution. The C# projects copy from the
  artifacts directory; they never build native code themselves.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Clean
  Delete the build and artifacts directories first.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$NativeDir    = $PSScriptRoot
$WindowsRoot  = Split-Path -Parent $NativeDir
$RepoRoot     = Split-Path -Parent $WindowsRoot          # only reach outside windows/
$AutocorrectDir = Join-Path $RepoRoot 'asian-autocorrect'
$BuildDir     = Join-Path $WindowsRoot 'build\native'
$ArtifactsDir = Join-Path $WindowsRoot 'artifacts\native'
$RustTarget   = 'x86_64-pc-windows-msvc'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Fail($msg) { Write-Error $msg; exit 1 }

# ---------------------------------------------------------------------------
# Toolchain
# ---------------------------------------------------------------------------
Write-Step 'Checking toolchain'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail 'vswhere.exe not found - Visual Studio Build Tools are not installed.' }

# -version 17 pins VS 2022+. whisper.cpp/ggml are tested against the v143
# toolset; VS 2019's v142 is not a configuration we want to debug against.
$vsPath = & $vswhere -products * -version '[17.0,)' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -latest -property installationPath
if (-not $vsPath) {
    Fail @'
No Visual Studio 2022+ with the C++ workload found.
  winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
'@
}
Write-Host "msvc      $vsPath"

# Prefer the CMake that ships with VS Build Tools. It is inside ggml's declared
# 3.14...3.28 policy range, whereas a current standalone CMake is 4.x and applies
# newer policies to a tree whose top level still declares `VERSION 3.5` - right at
# the edge of what CMake 4 still accepts. Using the bundled one also means
# standalone CMake is not a prerequisite at all.
$cmake = $null
$bundled = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (Test-Path $bundled) {
    $cmake = $bundled
} else {
    $onPath = Get-Command cmake -ErrorAction SilentlyContinue
    if ($onPath) { $cmake = $onPath.Source }
}
if (-not $cmake) {
    Fail @'
No cmake found. Expected it inside the VS Build Tools C++ workload.
Reinstall Build Tools with --includeRecommended, or: winget install Kitware.CMake
'@
}
$cmakeVersion = ((& $cmake --version) | Select-Object -First 1) -replace '^cmake version\s*', ''
Write-Host "cmake     $cmakeVersion  ($cmake)"
if ($cmakeVersion -match '^(\d+)\.') {
    if ([int]$Matches[1] -ge 4) {
        Write-Warning 'CMake 4.x applies policies newer than ggml declares support for. If configure fails, install VS Build Tools with --includeRecommended to get the bundled 3.x.'
    }
}

$cargoCmd = Get-Command cargo -ErrorAction SilentlyContinue
if (-not $cargoCmd) { Fail 'cargo not found on PATH. Install Rust: winget install Rustlang.Rustup' }
$cargo = $cargoCmd.Source
$installedTargets = & rustup target list --installed
if ($installedTargets -notcontains $RustTarget) {
    Fail "Rust target $RustTarget is not installed. Run: rustup target add $RustTarget"
}
Write-Host "cargo     $(((& $cargo --version) -split ' ')[1])  target $RustTarget"

if (-not (Test-Path (Join-Path $AutocorrectDir 'Cargo.toml'))) {
    Fail "asian-autocorrect submodule missing. Run: git submodule update --init --recursive"
}

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------
if ($Clean) {
    Write-Step 'Cleaning'
    foreach ($d in @($BuildDir, $ArtifactsDir)) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d; Write-Host "removed $d" }
    }
}

New-Item -ItemType Directory -Force -Path $ArtifactsDir | Out-Null

# ---------------------------------------------------------------------------
# whisper.cpp
# ---------------------------------------------------------------------------
Write-Step "Building whisper.cpp ($Configuration)"

& $cmake -S $NativeDir -B $BuildDir -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { Fail 'CMake configure failed.' }

& $cmake --build $BuildDir --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { Fail 'CMake build failed.' }

# whisper.cpp with BUILD_SHARED_LIBS emits whisper.dll plus several ggml DLLs
# (ggml, ggml-base, ggml-cpu). All of those are needed at runtime.
#
# It also builds parakeet.dll unconditionally - upstream whisper.cpp now ships a
# Parakeet implementation, with no CMake option to turn it off. Rev. 3 is
# Whisper-only, so it is excluded here rather than shipped unused. (Note for
# whenever Parakeet is reconsidered: it is available from this submodule
# directly, which is cheaper than the ONNX Runtime route the plan assumed.)
$excluded = @('parakeet.dll')

$dlls = Get-ChildItem -Path $BuildDir -Recurse -Filter '*.dll' |
        Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
        Where-Object { $excluded -notcontains $_.Name }
if (-not $dlls) { Fail "No DLLs produced under $BuildDir." }

foreach ($dll in $dlls) {
    Copy-Item $dll.FullName -Destination $ArtifactsDir -Force
    Write-Host "  -> $($dll.Name)"
}

if (-not (Test-Path (Join-Path $ArtifactsDir 'whisper.dll'))) {
    Fail 'whisper.dll was not produced. Check the CMake output above.'
}

# ---------------------------------------------------------------------------
# autocorrect
# ---------------------------------------------------------------------------
Write-Step 'Building autocorrect'

$env:CARGO_PROFILE_RELEASE_LTO           = 'true'
$env:CARGO_PROFILE_RELEASE_CODEGEN_UNITS = '1'
$env:CARGO_PROFILE_RELEASE_STRIP         = 'symbols'

& $cargo build -p autocorrect-swift --release --target $RustTarget `
    --manifest-path (Join-Path $AutocorrectDir 'Cargo.toml')
if ($LASTEXITCODE -ne 0) { Fail 'cargo build failed.' }

$acDll = Join-Path $AutocorrectDir "target\$RustTarget\release\autocorrect_swift.dll"
if (-not (Test-Path $acDll)) { Fail "autocorrect_swift.dll not found at $acDll" }
Copy-Item $acDll -Destination $ArtifactsDir -Force
Write-Host "  -> autocorrect_swift.dll"

# ---------------------------------------------------------------------------
Write-Step 'Done'
Get-ChildItem $ArtifactsDir -Filter '*.dll' |
    Select-Object Name, @{ N = 'Size'; E = { '{0:N0} KB' -f ($_.Length / 1KB) } } |
    Format-Table -AutoSize
Write-Host "Artifacts: $ArtifactsDir`n"
