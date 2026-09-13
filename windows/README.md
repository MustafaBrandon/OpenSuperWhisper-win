# OpenSuperWhisper for Windows

The native Windows client. The macOS app in the parent directory is **reference only** —
it is not built from here, and no code is shared with it. See
[`../docs/windows-port.md`](../docs/windows-port.md) for the plan, the behavioural spec
inherited from the mac app, and the decisions behind this layout.

Milestones M0–M6 are complete and verified on hardware. M7 (packaging) is in progress:
the installer builds and works, and is not yet signed.

## Using it

There is no main window. The app lives in the notification area — and Windows 11 hides
new icons behind the `˄` chevron, so **click the chevron and drag the icon out** the
first time. Right-click it for the microphone, the transcription language, history and
settings.

**Hold right Ctrl and speak.** Let go, and the transcript is typed into whatever window
had focus. While the key is bound it does nothing else — it is withheld from other
applications so it cannot open menus or type, which is the trade for a dedicated
trigger. Left Ctrl is unaffected, and the trigger is configurable: any modifier key, a
mouse button, or a key combination such as `Alt` + `` ` ``.

Escape discards a recording. Past ten seconds it asks for a second press first, unless
you turn that off.

Useful command-line flags, because that tray icon can be genuinely hard to find:

| Flag | Effect |
| --- | --- |
| `--settings` | Open Settings directly |
| `--history` | Open History directly |
| `--onboarding` | Re-run first-run setup |
| *(an audio file)* | Transcribe it — also what happens when you open one with the app |

Opening the app while it is already running brings up History rather than starting a
second copy.

### Where your data lives

`%LOCALAPPDATA%\OpenSuperWhisper` — `settings.json` (plain, editable by hand),
`models\`, `recordings\`, and `log.txt` with one previous generation kept. History can be
turned off entirely in Settings, and off means nothing is written: no database row, and
the captured audio is deleted after transcription rather than kept and hidden.

## Building

| Tool | Version | Install |
| --- | --- | --- |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Visual Studio Build Tools | 2022, C++ workload | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| Rust | any, with `x86_64-pc-windows-msvc` | `winget install Rustlang.Rustup` |
| Inno Setup | 6 | `winget install JRSoftware.InnoSetup` — only needed to build an installer |

CMake is **not** a separate prerequisite: `build-native.ps1` uses the one bundled with
the VS Build Tools C++ workload. That is deliberate — the bundled CMake is 3.x, inside
ggml's declared `3.14...3.28` policy range, while a current standalone CMake is 4.x and
applies newer policies to a tree whose top level still declares `VERSION 3.5`. A
standalone CMake on PATH is used only as a fallback, with a warning.

Visual Studio 2019 is not sufficient: whisper.cpp and ggml are tested against the v143
toolset, and .NET 10 needs 2022-era tooling.

Submodules must be present:

```powershell
git submodule update --init --recursive
```

Native code first — the C# projects copy the DLLs from `artifacts/native/` and do not
build native code themselves:

```powershell
./native/build-native.ps1            # -Configuration Debug, -Clean also available
dotnet build OpenSuperWhisper.slnx
dotnet test  OpenSuperWhisper.slnx
```

`build-native.ps1` produces `whisper.dll` and its `ggml*.dll` companions from the pinned
whisper.cpp submodule, plus `autocorrect_swift.dll` from the Rust autocorrect crate.

### Running what you built

```sh
./run-app                 # stop any running copy, rebuild, relaunch
./run-app --settings      # ...with Settings open
```

The stop step is not optional: a running instance holds its DLLs open, and the build
fails with "file is being used by another process".

### Building an installer

```powershell
./installer/build-installer.ps1
```

Publishes self-contained and packages it — a per-user install needing no administrator
rights, around 120 MB. It is **not signed**, so SmartScreen will warn about any copy
that has been downloaded.

### Headless

`osw` is a development tool, not a shipped surface, but it is the fastest way to
exercise the pipeline without the UI:

```sh
./osw devices                     # list audio inputs
./osw record --seconds 3          # record, then transcribe
./osw listen --trigger rightctrl  # the full dictation loop
./osw ../jfk.wav                  # transcribe a file
```

## Layout

```text
windows/
├── Directory.Build.props   shared settings + the ONLY paths reaching outside windows/
├── OpenSuperWhisper.slnx
├── assets/                 app and tray icons, and where they came from
├── installer/              Inno Setup script and its build driver
├── native/                 CMake driver and build script for the native DLLs
├── src/
│   ├── OpenSuperWhisper.App/       WPF: tray, indicator, settings, history, onboarding
│   ├── OpenSuperWhisper.Cli/       osw — headless diagnostics
│   ├── OpenSuperWhisper.Core/      app logic ported from the mac source
│   └── OpenSuperWhisper.Interop/   hand-written P/Invoke
└── tests/
```

The UI came last on purpose: the native layer, capture, input hooks and text injection
were proven headlessly first, because that is where the surprises live.

## Things worth knowing before changing code here

**Two rules keep the eventual repository split cheap.** This tree moves to its own
repository via `git subtree split --prefix=windows`, and CI enforces the first of these:

1. **Nothing reaches outside `windows/`** except the two submodules and the bundled
   models, and those go through `Directory.Build.props` (managed) or the
   `OSW_WHISPER_CPP_DIR` cache variable (native). Never hardcode a relative path to the
   repository root anywhere else.
2. **Architecture is a build variable.** x64 is the only target for v1, but nothing
   hardcodes `x64` in a path or filename, so ARM64 later is a configuration rather than
   a port.

**Hand-written interop is hand-verified.** `WhisperNative.cs` mirrors a specific
whisper.cpp revision, recorded in `WhisperNative.PinnedRevision`. A wrong struct layout
is silent memory corruption, not a compile error — so **bumping the whisper.cpp
submodule is a reviewed change**: re-check every declaration against the header, and
require the tests to pass.

**The hook callback has a hard latency budget.** Windows silently removes a low-level
hook whose callback exceeds `LowLevelHooksTimeout` (300 ms), with no error — the app
just stops responding to its trigger. The callback pushes onto a lock-free queue and
returns: no allocation, no logging, no locks, no user code.

**The trigger-to-insertion path cannot be self-tested.** `SendInput` marks its events
injected and the coordinator drops injected events by design, so no synthesised
keystroke can drive the trigger. Two shipped bugs have lived in exactly that gap.
`OSW_ACCEPT_INJECTED=1` opens it for diagnosis, `--paste-target` gives a window the app
can read back, and `--test-insert` runs an insertion under real in-app conditions — but
a real key press landing real text still needs a human, and it is the single most
valuable manual check.

**WPF runtime settings can break rendering in ways that only appear when something is
drawn.** `InvariantGlobalization=true` cost a milestone's worth of confusion at M5:
the window was created and shown, and the failure arrived at the first piece of real
text. Trimming and AOT are the same class of hazard, which is why the installer
publishes untrimmed.
