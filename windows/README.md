# OpenSuperWhisper for Windows

Native Windows client. The macOS app in the parent directory is **reference only** —
it is not built from here, and no code is shared with it. See
[`../docs/windows-port.md`](../docs/windows-port.md) for the plan, the behavioral
spec inherited from the mac app, and the decisions behind this layout.

Current milestone: **M7 — packaging.** M0–M6 are done: the app runs from the
tray, dictates on a global trigger, inserts into the focused application, and
carries its settings, model manager, history and first-run flow.

## Prerequisites

| Tool | Version | Install |
| --- | --- | --- |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Visual Studio Build Tools | 2022, C++ workload | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| Rust | any, with `x86_64-pc-windows-msvc` | `winget install Rustlang.Rustup` |

CMake is **not** a separate prerequisite: `build-native.ps1` uses the one bundled
with the VS Build Tools C++ workload. That is deliberate — the bundled CMake is 3.x,
inside ggml's declared `3.14...3.28` policy range, while a current standalone CMake
is 4.x and applies newer policies to a tree whose top level still declares
`VERSION 3.5`. If you have standalone CMake on PATH it is used only as a fallback,
with a warning.

Visual Studio 2019 is not sufficient: whisper.cpp and ggml are tested against the
v143 toolset, and .NET 10 needs 2022-era tooling.

Submodules must be present:

```powershell
git submodule update --init --recursive
```

## Build

Native first — the C# projects copy the DLLs from `artifacts/native/` and do not
build native code themselves:

```powershell
./native/build-native.ps1            # -Configuration Debug, -Clean also available
dotnet build OpenSuperWhisper.slnx
dotnet test  OpenSuperWhisper.slnx
```

`build-native.ps1` produces `whisper.dll` and its `ggml*.dll` companions from the
pinned whisper.cpp submodule, plus `autocorrect_swift.dll` from the Rust
autocorrect crate.

## Layout

```text
windows/
├── Directory.Build.props   shared settings + the ONLY paths reaching outside windows/
├── OpenSuperWhisper.slnx
├── native/                 CMake driver and build script for the native DLLs
├── src/
│   ├── OpenSuperWhisper.App/       WPF: tray, indicator, settings, history, onboarding
│   ├── OpenSuperWhisper.Cli/       osw — headless diagnostics (record, listen, insert)
│   ├── OpenSuperWhisper.Core/      app logic ported from the mac source
│   └── OpenSuperWhisper.Interop/   hand-written P/Invoke
└── tests/
```

The UI came last on purpose: the native layer, capture, input hooks and text
injection were proven headlessly first, because they are where the surprises
live. `./run-app` stops, rebuilds and relaunches the app in one step.

Useful flags: `--settings`, `--history` and `--onboarding` open those windows
directly, which matters because Windows 11 hides new tray icons behind the
chevron. `--paste-target` and `--test-insert` are the M5 insertion diagnostics.

## Two rules that keep the split cheap

This tree moves to its own repository before release, via
`git subtree split --prefix=windows`. Two constraints keep that a command rather
than a migration, and CI enforces the first:

1. **Nothing reaches outside `windows/`** except the two submodules and the bundled
   model, and those go through `Directory.Build.props` (managed) or the
   `OSW_WHISPER_CPP_DIR` cache variable (native). Never hardcode a relative path to
   the repo root anywhere else.
2. **Architecture is a build variable.** x64 is the only target for v1, but nothing
   hardcodes `x64` in a path or filename, so ARM64 later is a configuration rather
   than a port.

## Interop safety

`WhisperNative.cs` is hand-written against a specific whisper.cpp revision, recorded
in `WhisperNative.PinnedRevision`. A wrong struct layout is silent memory corruption,
not a compile error — so **bumping the whisper.cpp submodule is a reviewed change**:
re-check every declaration against the header and require the tests to pass.
