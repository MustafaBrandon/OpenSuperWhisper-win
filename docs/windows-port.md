# OpenSuperWhisper for Windows — Porting Plan

**Rev. 4** · Derived from the macOS source at `master` — 43 Swift files, 11,194 lines in the app target.

A module-by-module plan for rebuilding the macOS dictation app as a native Windows client: what
carries over as logic, what gets rewritten against Win32, what gets dropped, and how each piece is
proven correct.

- [1. The decision](#1-the-decision)
- [2. Target stack](#2-target-stack)
- [3. Layer map](#3-layer-map)
- [4. Modules](#4-modules)
- [5. Behavioral constants](#5-behavioral-constants)
- [6. Preferences inventory](#6-preferences-inventory)
- [7. Data, paths & models](#7-data-paths--models)
- [8. Test plan](#8-test-plan)
- [9. Milestones](#9-milestones)
- [10. Risks](#10-risks)
- [11. Settled decisions](#11-settled-decisions)
- [12. Out of scope](#12-out-of-scope)

> **Rev. 4 changes.** M0 is built and green, and it turned up one thing that revises a decision's
> reasoning without changing the decision: upstream whisper.cpp now ships its own **Parakeet**
> implementation, so a second engine no longer needs ONNX Runtime — re-scoped from L to M in
> [Section 11](#11-settled-decisions). Still deferred, now deliberately schedulable.
>
> **Rev. 3 changes.** Three revisions to Rev. 2, all in [Section 11](#11-settled-decisions).
> The OS floor moves from Windows 10 22H2 to **Windows 11** — Windows 10 left support in October
> 2025 and consumer ESU ends October 2026, so the earlier floor was set on facts that no longer
> hold. The runtime is **.NET 10 LTS**, not .NET 8, whose support ends November 2026. And whisper
> interop is settled: **our own DLL built from the pinned submodule, with hand-written P/Invoke**,
> not Whisper.net's prebuilt runtimes.
>
> **Rev. 2 changes.** The five original open questions were settled: Whisper-only, x64-only,
> default model bundled, developed in-tree under `windows/` and split out to its own repository
> later.

---

## 1. The decision

The macOS app cannot be recompiled for Windows. Swift itself runs there, but SwiftUI and AppKit do
not, and the app's entire identity — the floating indicator, the global hotkey layer, the
paste-into-the-focused-app trick — lives in Apple frameworks. Three quarters of the source is bound
to them.

So this is a **new Windows client that shares a native core and a behavioral specification** with
the mac app, not a branch of it. The whisper.cpp submodule carries over as a DLL. The product logic
— queue semantics, VAD gating, cancel rules, post-processing — carries over as a spec, hand-
translated. Everything that touches the operating system is written fresh.

| Metric | Value |
| --- | --- |
| Lines portable as logic | ~2,900 |
| Lines rewritten | ~8,300 |
| Modules | 14 |
| Milestones | 8 |
| Mac tests to map | ~180 |

### Scope

Fixed for v1. Each of these was an open question in Rev. 1; the reasoning behind each answer is in
[Section 11](#11-settled-decisions).

| Constraint | v1 |
| --- | --- |
| Engine | Whisper only. No Parakeet, no second engine surface. |
| Whisper interop | Our own `whisper.dll` from the pinned submodule, hand-written P/Invoke. No Whisper.net. |
| Runtime | .NET 10 LTS + WPF. |
| Architecture | x64 only. Build scripts stay architecture-parametric so ARM64 is later a configuration, not a port. |
| OS floor | Windows 11 (build 22000). Windows 10 not supported. |
| Default model | `ggml-tiny.en.bin` bundled, not downloaded. First run works offline. |
| Location | Developed in-tree at `windows/`; splits to its own repository before release. |

### The thing worth stealing

The mac source is unusually well-commented, and the comments encode fixes that cost someone real
debugging: a 1.5-second clipboard-restore delay because Electron apps service a synthesized paste
late; 0.25 seconds of extra audio after the hotkey is released so the last word survives; a fresh
decoder state per recording so a hallucination on silence cannot poison the next transcript.

Those constants and invariants are the real asset. [Section 5](#5-behavioral-constants) collects
every one of them so the Windows build inherits the fixes instead of rediscovering them.

---

## 2. Target stack

The choice that matters most is the app shell, because it decides how much Win32 interop you write
by hand. **WPF on .NET 10** wins here: the three hardest surfaces — a click-through always-on-top
overlay, a tray icon, and low-level input hooks — are raw interop in every .NET UI framework, and
WPF has the longest-settled story for hosting that interop, running unpackaged, and handling
per-monitor DPI.

| Layer | Recommended | Alternative | Rationale |
| --- | --- | --- | --- |
| App shell & UI | .NET 10 LTS + WPF | WinUI 3, Avalonia | Mature interop and unpackaged deployment. .NET 10 is LTS to Nov 2028; .NET 8 goes out of support Nov 2026. |
| Speech engine | whisper.cpp built as `whisper.dll` from the pinned submodule, hand-written P/Invoke | Whisper.net, sherpa-onnx | Our own build guarantees `whisper_vad_*` is present and keeps revision parity with the mac app, so golden-transcript comparison means something. |
| Acceleration | CPU (OpenBLAS) + Vulkan, selected at runtime | CUDA build | Vulkan covers AMD, Intel and NVIDIA from one binary. CUDA is a second artifact, not a default. |
| Audio capture | NAudio `WasapiCapture` | CSCore, direct WASAPI | Shared mode, event-driven, resampled to 16 kHz mono. |
| Audio decode | NAudio + Media Foundation reader | FFmpeg.AutoGen | MF handles wav/mp3/m4a/wma natively. FFmpeg only if you need exotic containers. |
| Storage | SQLite via `Microsoft.Data.Sqlite` + Dapper | LiteDB | Direct GRDB equivalent; keep the schema byte-compatible so fixtures transfer. |
| Settings | JSON file under `%LOCALAPPDATA%` | Registry | Replaces `UserDefaults`. Human-readable, easy to reset and diff. |
| Tray icon | `H.NotifyIcon.Wpf` | raw `Shell_NotifyIcon` | Replaces `NSStatusItem`, including the microphone and language submenus. |
| Input hooks | Hand-written `SetWindowsHookEx` interop | SharpHook | Own this one. The callback has a hard latency budget (see M3) and a library that blocks on it will get you silently unhooked. |
| Text injection | Hand-written `SendInput` interop | InputSimulator | Needs scancode and Unicode modes plus foreground-layout resolution — more control than wrappers expose. |
| Caret location | UI Automation + `GetGUIThreadInfo` fallback | MSAA | Direct analog of the mac Accessibility path, with the same unreliability profile. |
| Asian autocorrect | Existing Rust cdylib, rebuilt for `x86_64-pc-windows-msvc` | — | Genuine binary reuse: `autocorrect-swift` already exports a C ABI. |
| Installer | Inno Setup, Authenticode-signed | MSIX, WiX | Unpackaged install keeps hooks and `SendInput` unrestricted. |

### Repository layout

The Windows client is developed **in this repository**, under a top-level `windows/` directory, so
the mac source stays one directory away while the behavioral spec is being translated. It carries
no build dependency on the mac target and is never built by `run.sh`.

```text
OpenSuperWhisper-win/
├── OpenSuperWhisper/          # mac app — reference only, not built here
├── libwhisper/whisper.cpp/    # submodule, shared: mac links it static, Windows builds a DLL
├── asian-autocorrect/         # submodule, shared: rebuilt for x86_64-pc-windows-msvc
├── ggml-tiny.en.bin           # bundled default model, already in git
├── docs/windows-port.md       # this document
└── windows/                   # ← the Windows client
    ├── OpenSuperWhisper.sln
    ├── src/                   # app, core, interop projects
    ├── tests/                 # Tier 1 and Tier 2 suites
    ├── native/                # CMake driver for whisper.dll + autocorrect DLL
    └── build/                 # local output, gitignored
```

Both submodules are shared rather than duplicated, which is the main reason to co-locate: the
Windows build consumes the same pinned whisper.cpp revision the mac app does, so an engine change
is tested against one source of truth.

**Upstream posture.** This fork never pushes to the repository it was forked from. Upstream is a
read-only reference; nothing here is written with a pull request in mind.

**The split.** Before release the `windows/` tree moves to
`C:\Users\brand\Documents\GitHub\WindowsOpenSuperWhisper` as a standalone repository. Two things
make that cheap if they are respected from M0 onward:

- Nothing under `windows/` references a path outside `windows/`, except the two submodules and the
  bundled model — all reached through variables set in one place, not hardcoded relative paths.
- `git subtree split --prefix=windows` produces the new repository with history intact, so the
  split is a command rather than a migration.

At the split, the bundled model and the submodules become the new repository's problem — see the
repository-weight row in [Section 10](#10-risks).

---

## 3. Layer map

Every file in the mac app target, and where it lands.

- **Port** — the logic transfers and only the API calls change
- **Rewrite** — the behavior transfers but the code does not
- **Drop** — no Windows counterpart
- **New** — Windows-only concern

| macOS source | Lines | Disposition | Windows counterpart |
| --- | ---: | --- | --- |
| `Whis/*` (12 files) | 1,270 | Rewrite | Swift→C bindings replaced by hand-written C# P/Invoke against the same `whisper.h`. No source reuse, but the Swift file is a useful reference for which symbols and structs actually matter. |
| `Engines/WhisperEngine` | 520 | Port | Param mapping, VAD gate and segment stitching transfer verbatim. The AVFoundation decode half (lines 298–518) is rewritten. |
| `Engines/FluidAudioEngine` | 114 | Drop | CoreML-only. Parakeet returns later via ONNX Runtime, or not at all. |
| `TranscriptionQueue` | 297 | Port | Queue state machine is pure logic. `Task` → `Task`, `@MainActor` → dispatcher affinity. |
| `TranscriptionService` | 184 | Port | Engine lifecycle and the serialization guard that stops two transcriptions sharing one context. |
| `Models/Recording` + store | 510 | Port | GRDB → Dapper. Same schema, same fields, same retention rules. |
| `WhisperModelManager` | 231 | Port | `URLSession` → `HttpClient`. Progress, HTTP status validation and cancellation semantics are unchanged. |
| `Utils/AppPreferences` | 144 | Port | 29 keys and defaults transfer; three get Windows-appropriate defaults (Section 6). |
| `Utils/{Language,Text,DiskSpace}` | 115 | Port | Pure functions. Straight translation, tests included. |
| `AudioRecorder` | 396 | Rewrite | WASAPI capture. Keeps minimum duration, stop-tail, temp sweep and the device warm-up guard. |
| `MicrophoneService` | 486 | Rewrite | `IMMDeviceEnumerator` + `IMMNotificationClient`. Continuity/iPhone detection drops out. |
| `ShortcutManager`, `ModifierKeyMonitor`, `MouseButtonMonitor` | 597 | Rewrite | Low-level keyboard and mouse hooks. Hold, double-tap and mode exclusivity logic transfer exactly. |
| `Utils/ClipboardUtil` | 251 | Rewrite | Clipboard + `SendInput`. TIS layout resolution becomes `VkKeyScanEx` against the foreground layout. |
| `Utils/FocusUtils` | 293 | Rewrite | UI Automation caret resolution with the same timeout-and-fallback discipline. |
| `Indicator/*` | 766 | Rewrite | Layered click-through window. The six-state view model and its cancel rules port; the animation does not. |
| `ContentView`, `Settings`, `Onboarding` | 4,011 | Rewrite | The bulk of the work by volume, and the least risky. XAML against the same view models. |
| `OpenSuperWhisperApp` + `AppDelegate` | 450 | Rewrite | Tray, single-instance, file-open handling, window lifecycle. |
| `PermissionsManager` | 229 | Drop | No accessibility or input-monitoring consent on Windows. Shrinks to a microphone-availability check. |
| `Utils/KeyboardLayoutProvider` | 102 | Rewrite | Key-cap labels for the shortcut recorder, via `MapVirtualKeyEx`. |
| — | — | New | UAC/elevation handling, antivirus posture, autostart registration, update checker, crash logging. |

---

## 4. Modules

Sizes are rough: **S** a day or two, **M** most of a week, **L** multiple weeks.

### 01 · Transcription core — Port · M

**Owns** — Model load, whisper params, VAD gate, segment assembly, cancellation, progress.

**Sits on** — our own `whisper.dll` via hand-written P/Invoke; Silero VAD model shipped alongside.

**Must reproduce**

- A fresh `whisper_state` per recording — never share decoding state between recordings.
- VAD before the encoder; skip trimming entirely when timestamps are requested.
- Stitch speech segments with 0.1 s overlap and 0.1 s of inserted silence.
- Return empty string when VAD finds no speech — the caller depends on this to discard the recording.
- Progress mapped 10–95 %, with 0–10 % reserved for decode.
- Strip `[MUSIC]` and `[BLANK_AUDIO]`, then trim.

**Risk** — Hand-written P/Invoke against a moving submodule: a wrong struct layout corrupts memory
silently rather than failing to compile. Struct definitions live in one file next to the
`whisper.h` revision they mirror, and a submodule bump requires the Tier 2 suite to pass.

### 02 · Audio pipeline — Rewrite · M

**Owns** — Decoding any dropped file to 16 kHz mono float; capturing the microphone to 16-bit PCM wav.

**Sits on** — Media Foundation source reader; `WasapiCapture` in shared mode.

**Must reproduce**

- 16 kHz, 16-bit, mono-mixed output — half the I/O of float with no loss for speech.
- Multi-channel sources mix only channels above an RMS floor of 0.0001, then normalize by the
  active count. A six-channel interface with one live mic must not come out five-sixths silent.
- Flush the resampler at end of stream, or the last few milliseconds vanish.
- Parallel decode for files over ten seconds, with results concatenated in worker order.

**Risk** — Media Foundation's resampler quality differs from `AVAudioConverter`. Prove parity
against the mac output on a fixture set before building on top of it.

### 03 · Device service — Rewrite · M

**Owns** — Enumerating inputs, remembering the user's pick, reacting to hotplug, exposing the tray submenu.

**Sits on** — `IMMDeviceEnumerator`, `IMMNotificationClient`.

**Must reproduce**

- Selected device survives unplug and reappears when the device returns.
- Bluetooth devices are flagged as needing warm-up, which drives the *connecting* indicator state.
- The active-microphone check is a cached read — it must never make a blocking call on the UI thread.

**Simpler here** — WASAPI captures from a chosen device directly, so the mac trick of temporarily
switching the system default input — and restoring it only if the user did not change it meanwhile
— is unnecessary. Roughly 80 lines disappear.

### 04 · Trigger layer — Rewrite · L

**Owns** — All three recording triggers, hold-to-record, double-tap, and the Escape key.

**Sits on** — `SetWindowsHookEx` with `WH_KEYBOARD_LL` and `WH_MOUSE_LL`, on a dedicated thread
with its own message pump. `RegisterHotKey` is not sufficient — it cannot bind a bare modifier.

**Must reproduce**

- Exactly one trigger mode active: mouse button beats modifier key beats shortcut.
- Hold mode arms 0.3 s after key-down, and only on a press that *starts* a recording — arming on
  the stopping press causes a double stop on release.
- Double-tap is required only to start; once recording, a single press stops.
- Escape is enabled only while the indicator is up.

**Risk** — A low-level hook callback that exceeds `LowLevelHooksTimeout` (300 ms by default) is
silently removed by Windows, and the app stops responding to its hotkey with no error. The callback
must do nothing but post to a queue.

### 05 · Text injection — Rewrite · M

**Owns** — Getting the transcript into whatever the user was typing in.

**Sits on** — Clipboard APIs plus `SendInput`; layout via `GetKeyboardLayout` on the foreground
thread and `VkKeyScanEx`.

**Must reproduce**

- Three modes from two preferences: paste-and-restore, paste-and-keep, copy-only. Both off means do nothing.
- Restore the previous clipboard only after 1.5 s *and* only if the clipboard sequence number still
  matches — otherwise the user took the clipboard over and restoring would clobber it.
- Resolve the V key against the *target app's* layout, not the app's own.

**Windows opportunity** — `KEYEVENTF_UNICODE` types text directly without touching the clipboard at
all, which would delete the save/restore/delay mechanism entirely. Prototype both: Unicode injection
is slower for long text and misbehaves in some apps, so it may be the default with clipboard paste
as the fallback.

**Risk** — `SendInput` cannot reach a window owned by an elevated process. Dictating into an admin
terminal silently fails. Detect and surface it rather than appearing broken.

### 06 · Caret anchor — Rewrite · M

**Owns** — Finding where the text cursor is, so the indicator appears next to it.

**Sits on** — UI Automation `TextPattern`; `GetGUIThreadInfo().rcCaret` as a cheap first try; mouse
position as the final fallback.

**Must reproduce**

- Hard 150 ms budget on the whole resolution, racing against a timer. A busy focused app must never
  delay the indicator.
- Reject degenerate rectangles (zero size, off every screen) rather than placing the indicator at the origin.
- Recording starts *before* anchor resolution begins — the first words are never traded for placement.

**Risk** — UIA is unreliable in Chromium, Electron and Java apps — the same set where the mac
Accessibility path struggles. The fallback chain is the feature, not a workaround.

### 07 · Indicator overlay — Rewrite · M

**Owns** — The floating status card: six states, blink, cancel confirmation.

**Sits on** — A WPF window with `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE |
WS_EX_TOOLWINDOW`, topmost, per-monitor DPI aware.

**Must reproduce**

- States: idle, connecting, recording, decoding, busy, no-microphone.
- Busy and no-microphone auto-dismiss after 2 s.
- Escape cancels immediately under 10 s; past that it arms a confirmation for 5 s and needs a second
  press — unless the user disabled confirmation.
- Never steals focus. The user is typing in another app.
- A second stop request while decoding is ignored, not re-entered.

**Windows 11 affordance** — with the floor at Windows 11, `DwmSetWindowAttribute` makes Mica and
rounded-corner treatments available to the overlay. Optional: the card must stay legible if they
fail or are disabled.

**Risk** — Placement across mixed-DPI multi-monitor setups. Resolve the target monitor from the
anchor point, then convert with that monitor's scale.

### 08 · Queue, store & model manager — Port · M

**Owns** — Recording history, the pending-work queue, model download and catalog.

**Sits on** — SQLite; `HttpClient` with progress reporting.

**Must reproduce**

- Own temp recordings are *moved* into the library; user-dropped files are *copied*, leaving the
  original in place.
- An empty transcript from a dictation is discarded along with its audio; an empty transcript from
  an imported file is kept.
- Orphaned pending rows whose source file vanished are cleaned up at startup.
- Downloads validate HTTP status before accepting the file, and clamp progress to 1.0.
- Retention deletion only ever touches paths inside the recordings directory.
- The bundled `ggml-tiny.en.bin` is copied into the models directory on every startup where it is
  missing, not only the first — the mac app checks this on each launch so a user who deletes their
  models is not left with an app that cannot transcribe.

### 09 · Shell: tray, windows, lifecycle — Rewrite · M

**Owns** — Tray icon and menus, single-instance enforcement, start-hidden, file associations, autostart.

**Sits on** — `Shell_NotifyIcon`; a named mutex; `HKCU\...\CurrentVersion\Run`.

**Must reproduce**

- Tray menu carries microphone selection and language selection, both reflecting current state.
- Closing the main window leaves the app running in the tray.
- Audio files opened from Explorer are queued, not opened in a window.

### 10 · Main window, settings & onboarding — Rewrite · L

**Owns** — History list with playback and re-transcribe, the full settings surface, first-run flow.

**Sits on** — XAML over the ported view models.

**Must reproduce**

- Every preference in Section 6, with its default.
- Shortcut recorder showing correct key caps for the active layout.
- Model list with size, download progress, cancel, and the conditional visibility rule for
  language-specific models.
- Drag-and-drop of audio files onto the window, queued in order.
- Free-space check before starting a download.

**Note** — Largest by volume, lowest by risk. Schedule it after the OS-integration modules have
proven out, not before.

---

## 5. Behavioral constants

These are the numbers the mac app arrived at, with the reason each one exists. Changing one should
be a deliberate decision with a test behind it, not an accident of reimplementation.

| Constant | Value | Why |
| --- | --- | --- |
| Minimum recording | 1.0 s | Shorter captures are discarded as accidental taps. |
| Stop tail | 0.25 s | Keep capturing after the stop request so the last word, released with the hotkey, is not clipped. |
| Temp file max age | 24 h | Sweep abandoned temp recordings at startup. |
| Clipboard restore delay | 1.5 s | Slow consumers (browsers, Electron) service a synthesized paste late; restoring earlier makes them paste stale content. |
| Hold threshold | 0.3 s | Separates a press from a hold. |
| Double-tap window | system | Uses the OS double-click interval, so it follows the user's own setting. |
| Anchor resolve budget | 150 ms | Indicator placement never waits on a busy foreground app. |
| Accessibility call timeout | 0.25 s | Each cross-process query is capped individually. |
| Cancel confirm threshold | 10 s | Recordings longer than this require a second Escape. |
| Cancel confirm window | 5 s | How long the armed confirmation stays live. |
| Auto-dismiss message | 2.0 s | Busy and no-microphone states hide themselves. |
| Blink interval | 0.8 s | Recording indicator pulse. |
| VAD overlap / gap | 0.1 s / 0.1 s | Mirrors upstream whisper stitching so the decoder still hears natural pauses. |
| Decode threads | clamp(cores, 2, 8) | More than eight stops helping and starts contending. |
| Greedy best-of | 5 | Matches the whisper.cpp default; 1 degrades temperature fallback to a single random sample. |
| Device warm-up probe | 50 ms × 2 | Two consecutive file-growth checks above 8 KB before declaring a Bluetooth mic live. |
| Download timeouts | 60 s / 24 h | Per-request and whole-resource, for multi-gigabyte models on slow links. |
| Parallel decode floor | 10 s | Below this, single-threaded conversion is faster than coordinating workers. |

---

## 6. Preferences inventory

All 29 keys, their defaults, and what changes on Windows. Keeping the names identical makes it
possible to hand-migrate a settings file between platforms and to diff behavior when something
diverges.

| Key | Default | Windows note |
| --- | --- | --- |
| `selectedEngine` | `whisper` | Only value for v1. |
| `selectedWhisperModelPath` | `null` | Absolute path under the models directory. |
| `whisperLanguage` | `en` | — |
| `suppressBlankAudio` | `true` | — |
| `showTimestamps` | `false` | Disables VAD trimming when on. |
| `temperature` | `0.0` | — |
| `noSpeechThreshold` | `0.6` | — |
| `initialPrompt` | `""` | Carried across windows when set. |
| `useBeamSearch` | `false` | — |
| `beamSize` | `5` | — |
| `debugMode` | `false` | — |
| `playSoundOnRecordStart` | `false` | — |
| `hasCompletedOnboarding` | `false` | — |
| `useAsianAutocorrect` | `true` | Requires the Rust cdylib present. |
| `selectedMicrophoneData` | `null` | Store the WASAPI endpoint ID string instead of an archived object. |
| `modifierOnlyHotkey` | `none` | — |
| `lastModifierOnlyHotkey` | `leftCommand` | **Change to `leftAlt`** — there is no Command key. |
| `mouseButtonHotkey` | `none` | Middle, X1, X2. |
| `holdToRecord` | `true` | — |
| `doublePressToTrigger` | `false` | — |
| `addSpaceAfterSentence` | `true` | — |
| `autoCopyToClipboard` | `false` | — |
| `autoPasteTranscription` | `true` | — |
| `escCancelWithoutConfirmation` | `false` | — |
| `startHiddenInMenuBar` | `false` | **Rename to `startHiddenInTray`**. |
| `autoDeleteRecordingsEnabled` | `false` | — |
| `autoDeleteRecordingsAfterDays` | `30` | — |
| `fluidAudioModelVersion` | `v3` | **Drop** with the Parakeet engine. |
| `qwen3Variant` | `f32` | **Drop** — unreferenced in the mac app. |

### Default shortcut

The mac default is ``Option + ` ``. The direct Windows translation is ``Alt + ` ``, which is free in
most applications. Worth confirming against common targets before committing, since a bad default
is the first thing every user hits.

---

## 7. Data, paths & models

### Recording schema

Eight columns, unchanged from GRDB. Status is one of `pending`, `converting`, `transcribing`,
`completed`, `failed`.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | TEXT | GUID, primary key. |
| `timestamp` | INTEGER | Unix seconds; also derives the audio filename. |
| `fileName` | TEXT | `<unix>.wav` inside the recordings directory. |
| `transcription` | TEXT | Also carries user-facing failure messages. |
| `duration` | REAL | Seconds. |
| `status` | TEXT | See above. |
| `progress` | REAL | 0.0–1.0. Transient updates must not hit disk on every tick. |
| `sourceFileURL` | TEXT NULL | Set for queued and imported items; null once completed from dictation. |

### Filesystem layout

| Purpose | macOS | Windows |
| --- | --- | --- |
| App data root | `~/Library/Application Support/<bundleID>` | `%LOCALAPPDATA%\OpenSuperWhisper` |
| Models | `…/whisper-models/` | `…\models\` |
| Recordings | `…/recordings/` | `…\recordings\` |
| Temp captures | `$TMPDIR/temp_recordings/` | `%TEMP%\OpenSuperWhisper\recordings\` |
| Settings | UserDefaults plist | `…\settings.json` |

### Model catalog

| Name | Size | File / note |
| --- | ---: | --- |
| Tiny English | 77 MB | `ggml-tiny.en.bin` — bundled. Already committed at the repo root; the installer ships it and copies it into the models directory on first run, so a fresh install transcribes offline with no download. |
| Turbo V3 large | 1,624 MB | `ggml-large-v3-turbo.bin` |
| Turbo V3 medium | 874 MB | `ggml-large-v3-turbo-q8_0.bin` |
| Turbo V3 small | 574 MB | `ggml-large-v3-turbo-q5_0.bin` |
| Turbo V3 Hebrew | 1,624 MB | Saved as `ggml-ivrit-large-v3-turbo.bin`. Selecting it forces language to `he`, and it stays hidden unless Hebrew is selected, the system language is Hebrew, or it is already downloaded. |

---

## 8. Test plan

The mac app carries roughly 180 tests. Most of them are assertions about behavior, not about Swift,
so they transfer. They sort into four tiers by what they need to run, which also decides what CI can
enforce and what a human has to check.

### Tier 1 — Pure logic, runs anywhere

No OS interaction, no audio hardware. These run on every commit and are the direct ports of the mac
unit tests.

| Area | Cases | Representative assertions |
| --- | ---: | --- |
| Sentence post-processing | 14 | Trailing period, question mark, exclamation, comma, colon, semicolon, ellipsis get a space; a letter does not; disabled preference suppresses it; empty string is safe. |
| Empty-dictation discard | 4 | Empty from dictation is discarded; empty from an imported file is kept; non-empty is always kept. |
| Cancel confirmation | 6 | Short recording cancels at once; long one arms then cancels on second press; the toggle bypasses it; decoding cancels at once; the window expires; starting decode resets it. |
| Retention | 6 | Cutoff date subtracts days; separates old from fresh; non-positive days disables; deletion refuses paths outside the recordings directory. |
| Model download | 6 | 2xx yields no error; non-HTTP responses pass; 4xx/5xx produce a user-facing error; progress fraction clamps at 1.0 and is null for unknown length. |
| Model catalog | 12 | Filename defaults to the URL basename; explicit filename and preferred language honored; Hebrew model visibility rules; Hugging Face page URL derivation. |
| Language support | 5 | Whisper returns the full list including `auto`; every listed language has a display name; fallback behavior. |
| Duration formatting | 6 | 0, seconds, minutes+seconds, exact minutes, hours+minutes+seconds, exact hours. |
| Disk space guard | 4 | Above, at, and below threshold; the error carries a user-facing message. |
| Trigger enums | 5 | Mouse button raw values round-trip; unknown values yield null; `none` is not selectable. |
| VAD stitching | new | Segment assembly inserts the right overlap and gap; empty segment list yields empty output. |

### Tier 2 — Needs the native core and audio fixtures

Runs headless in CI on a Windows runner. This tier is where parity with the mac build is actually
proven, so it should exist before any UI is written.

- **Decode fidelity.** Sequential conversion preserves full duration; parallel conversion is
  continuous and matches the sequential result sample-for-sample; a 16 kHz 16-bit wav round-trips
  unchanged.
- **Multi-channel mixing.** Six-channel source with one active channel produces full-amplitude
  mono, not one-sixth.
- **VAD gate.** A fixture with speech surrounded by silence yields segments covering the speech and
  nothing else; a silent fixture yields none.
- **State isolation.** Two consecutive transcriptions on one loaded model produce identical output
  to two independent runs — no prompt bleed.
- **Golden transcripts.** `jfk.wav` and a small fixture set transcribe to a recorded reference
  string, per model. The reference is the Windows build's own verified-correct output, not the mac
  build's — see the parity waiver under M1. This is the regression net for every engine, parameter
  and submodule change.
- **Temp cleanup.** Only files older than 24 h are removed; a missing directory does not throw.

### Tier 3 — Needs a desktop session

Requires a logged-in interactive session, so it runs on a self-hosted runner or a scheduled VM job
rather than a standard hosted runner.

- **Layout resolution.** For each installed keyboard layout, resolving the V key produces a
  keystroke that pastes correctly. The mac suite covers about 40 layouts — Dvorak variants,
  Cyrillic, CJK, Arabic, Hebrew — and that matrix transfers directly.
- **Clipboard restore.** Untouched clipboard is restored after the delay; a clipboard changed
  meanwhile is left alone.
- **Hook survival.** The hotkey still works after screen lock and unlock, after a UAC prompt, and
  after the callback is deliberately starved.
- **Overlay placement.** Correct monitor and scale on a mixed-DPI dual-monitor setup; visible over a
  fullscreen app; never takes focus.
- **Caret resolution.** Anchor resolves within budget in Notepad, WordPad and a WPF app; falls back
  cleanly where UIA returns nothing.
- **Device hotplug.** Unplugging the selected microphone mid-recording is handled; reconnecting
  restores the selection.

### Tier 4 — Manual matrix

Run before each release. The axes that actually break things:

| Axis | Values |
| --- | --- |
| Paste target | Notepad, Word, Chrome, Slack, Discord, VS Code, Windows Terminal, PowerShell, an elevated console (expected failure), a fullscreen game |
| Layout | US, UK, German, French, Russian, Japanese IME, Korean IME, Dvorak |
| Input device | Built-in array, USB interface, Bluetooth headset, virtual cable |
| Display | Single 100 %, single 150 %, dual mixed-DPI, laptop + external, monitor unplugged mid-recording |
| Trigger | Shortcut, bare modifier, mouse button × hold, toggle, double-tap |
| OS | Windows 11 — current release and one prior, with and without a third-party antivirus |

---

## 9. Milestones

Ordered so that the unknowns come first. Everything through M4 is headless or nearly so, which means
the risky parts — native interop, hooks, injection — are proven before a single screen is designed.
The largest chunk of work, the UI, is deliberately last because it is the part least likely to
surprise anyone.

### M0 · Build and CI

The `windows/` tree per [Repository layout](#repository-layout): a .NET 10 solution and project
skeleton targeting `net10.0-windows`, whisper.cpp compiled to an x64 DLL from the pinned submodule
with MSVC, the Rust autocorrect crate rebuilt for `x86_64-pc-windows-msvc`, and a Windows CI job.

The existing mac workflow gets `paths-ignore` for `windows/**` and `docs/**` so Windows-only commits
stop spinning up macOS runners for an app that is not built here.

This is also where the split-friendliness rules start being enforced: architecture as a build
variable, and no path reaching outside `windows/` except the two submodules and the bundled model,
each through a single variable.

> **Exit** — CI on `windows-latest` produces `whisper.dll`, the autocorrect DLL, and a building
> solution, from a clean checkout with submodules. `git subtree split --prefix=windows` produces a
> tree that still builds, verified once here and re-verified at M7.

### M1 · Headless transcription

A console app that takes a file and prints a transcript. Model loading, params, VAD gate, segment
assembly, cancellation, progress. No UI, no capture.

This is where the VAD binding question gets answered, and where golden-transcript parity with the
mac build is established.

> **Exit** — `jfk.wav` transcribes correctly; Tier 2 decode, VAD and isolation tests pass.
> **Met.** ~13x realtime on CPU with tiny.en.

**Mac parity comparison waived.** The original criterion said "matches the mac output". Running it
needs a Mac, and the decision is to accept the Windows transcript as the reference instead. What the
golden test pins is therefore Windows-to-Windows consistency — a regression net for engine,
parameter and submodule changes — not cross-platform equivalence. Same model, same whisper.cpp
revision and same parameters should give identical text, but that is reasoning, not evidence. If a
Mac becomes available, running the comparison once would upgrade the guarantee cheaply.

### M2 · Microphone to file

WASAPI capture at 16 kHz mono 16-bit, device enumeration and hotplug, minimum-duration and stop-tail
rules, temp sweep.

> **Exit** — A harness records from a chosen device and feeds M1 end to end; short captures are
> discarded; Bluetooth warm-up is detected. **Met, with one caveat below.**

Verified on hardware: `osw record --seconds 3` captures 3.24 s — the extra 0.24 s is the stop tail
doing its job — resamples 48 kHz stereo to 16 kHz mono, gates through VAD and transcribes. A 0.5 s
capture is discarded with no temp files left behind.

**Bluetooth warm-up is implemented but not verified on hardware.** Transport classification is
tested against real `PKEY_Device_EnumeratorName` strings, and the built-in array on the dev machine
classifies correctly as `Builtin`. But no Bluetooth microphone was available, so the warm-up path
itself — the delay between starting capture and the device actually delivering samples — has never
been exercised. Treat it as untested until someone pairs a headset. It is on the Tier 4 matrix.

One finding worth remembering: NAudio's `PKEY_Device_InstanceId` is **not** the property to
classify transport with. It resolves to the MMDEVAPI software endpoint
(`SWD\MMDEVAPI\{0.0.1...}`), identical in shape for every device, so every microphone comes back
`Unknown` and Bluetooth never gets its warm-up state. The bus name lives in
`PKEY_Device_EnumeratorName` (`{a45c254e-df1c-4efd-8020-67d146a850e0}`, PID 24), which NAudio does
not expose and which the code declares by hand. There is a regression test pinning this.

### M3 · Global input

Low-level keyboard and mouse hooks on a dedicated pump thread. All three trigger modes with correct
precedence, hold-to-record, double-tap, Escape.

> **Exit** — Every trigger mode drives start and stop correctly; the hook survives lock/unlock and a
> starved callback. **Partially met — see below.**

Verified automatically: the state machine's every branch (24 unit tests covering hold, toggle,
double tap, and the stopping-press bug), that the hook installs and delivers real events end to end,
that synthesised input is flagged injected, and that four consecutive rebinds tear down and
reinstall without deadlocking.

**The join between them cannot be self-tested.** `SendInput` always marks events injected, and the
coordinator deliberately drops injected events so the app cannot trigger itself with its own paste
keystrokes. That defence is correct and worth keeping — but it means no synthesised press can ever
drive the trigger. Only a human pressing a real key exercises hook → coordinator → recording. Run
`osw listen` and hold right Alt.

**Lock/unlock recovery is unverified.** Windows gives no "your hook was removed" signal, so there is
nothing to react to. The mitigation is a watchdog that reinstalls every 30 s, bounding how long a
silently-dead hook stays dead; the reinstall path is tested, the lock/unlock scenario is not. On the
Tier 4 matrix.

The real defence against the timeout is structural: the hook callback pushes onto a lock-free queue
and returns, with no allocation, logging, locks or user code on that path.

### M4 · Text injection

Clipboard paste with layout-correct `SendInput`, plus the Unicode direct-typing path. Restore guard,
three output modes, elevated-window detection.

> **Exit** — Text lands correctly across the Tier 4 paste-target list; clipboard restore behaves
> under both conditions; elevated failure is reported, not silent.

### M5 · Indicator and tray

The layered overlay with all six states, caret anchoring, cancel confirmation, tray icon and menus.
At the end of this milestone the product exists.

> **Exit** — Full dictation loop — hotkey, record, transcribe, paste — with no main window open, on
> a mixed-DPI setup.

### M6 · Windows and settings

History list with playback and re-transcribe, the full settings surface, model manager with
downloads, drag-and-drop, onboarding.

> **Exit** — Every preference in Section 6 is reachable and takes effect; parity checklist against
> the mac app is complete or has explicit exceptions.

### M7 · Packaging

Signed installer, autostart registration, update check, crash logging, uninstall that leaves no
hooks behind.

> **Exit** — Clean install, use, update and uninstall on a fresh VM, with a mainstream antivirus
> active and no warning.

---

## 10. Risks

| Risk | When known | Mitigation |
| --- | --- | --- |
| ~~VAD not exposed by the chosen binding~~ | — | **Retired in Rev. 3.** Building our own DLL means `whisper_vad_*` is exported by construction. The residual risk is only that we forget to enable it in the CMake configuration — caught by the Tier 2 VAD test. |
| P/Invoke signature drift | M1, ongoing | Hand-written interop against a submodule that moves. A wrong struct layout is silent memory corruption, not a compile error. Pin the submodule deliberately, keep the struct definitions in one file next to the header they mirror, and treat a submodule bump as a change that requires the Tier 2 suite to pass. |
| Hook silently removed under load | M3 | The callback posts to a queue and returns; never allocate, log or take a lock inside it. Add a watchdog that re-installs the hook if it stops firing. |
| Antivirus flags the app | M7 | A program that hooks the keyboard, reads the clipboard and synthesizes input is a textbook heuristic match. Authenticode signing with a reputable certificate, plus submission to major vendors ahead of release. Budget real time for this. |
| Injection fails into elevated apps | M4 | Inherent to UIPI. Detect the foreground process integrity level and tell the user plainly. A `uiAccess` manifest is possible but requires signing and installation under Program Files, and is not worth it for v1. |
| UIA caret unavailable in common apps | M6 | Chromium, Electron and Java surfaces are unreliable. The fallback chain — UIA, then `rcCaret`, then mouse position — is the design, and the 150 ms budget keeps failure cheap. |
| Resampler parity drift | M1 | Media Foundation and `AVAudioConverter` will not produce identical samples. Golden transcripts, not sample equality, are the acceptance criterion. |
| Bluetooth warm-up detection | M2 | The mac approach watches the output file grow. WASAPI exposes buffer state directly, so this likely gets simpler — but it needs a prototype against a real headset, not an assumption. |
| GPU backend selection | M1 | Ship CPU plus Vulkan and probe at startup, falling back silently. A CUDA build is a separate optional artifact, not the default download. |
| Repository weight at the split | pre-M7 | `ggml-tiny.en.bin` is 77 MB and already committed here, so bundling costs this repo nothing extra. The cost lands when `windows/` splits out: a `subtree split` carries the blob into the new repository's history. Decide then whether the new repo commits it, pulls it via Git LFS, or fetches it at build time. Keep all other fixtures small so this stays the only large object. |
| Installer size | M7 | Bundling adds 77 MB to every installer and every update. If updates ship as full installers rather than deltas, revisit — the model changes far less often than the app. |

---

## 11. Settled decisions

Eight decisions, each recording what was decided, why, and what it obliges the implementation to do
— so a later reversal is a deliberate change rather than a rediscovery. The first five answer Rev.
1's open questions; the last three were settled in Rev. 3, one of them reversing an earlier call.

### Co-located during development, split before release

**Decided.** Develop under `windows/` in this repository; move to a standalone
`WindowsOpenSuperWhisper` repository before release. Never push to the upstream repository this was
forked from.

Co-location buys two things while the spec is being translated: the mac source is one directory
away for reference, and both submodules are shared rather than duplicated, so the Windows build
consumes the same pinned whisper.cpp revision the mac app does.

**Obliges** — the isolation rules and the `subtree split` path in
[Repository layout](#repository-layout). Respecting them from M0 keeps the split a command rather
than a migration.

### Whisper only for v1

**Decided.** No Parakeet, no second engine. **Reasoning revised in Rev. 4** — the decision stands,
but it is cheaper to reverse than Rev. 2 assumed.

Rev. 2 costed Parakeet as an ONNX Runtime port: a new inference runtime, new model plumbing, new
everything. The M0 build turned up something better. Upstream whisper.cpp — the submodule we already
build — now ships its own Parakeet implementation, and `parakeet.dll` comes out of our existing
CMake driver with no extra configuration.

What that changes and what it does not:

| | Rev. 2 assumption | Actual |
| --- | --- | --- |
| Inference runtime | Add ONNX Runtime | Already built, same submodule |
| Interop | New binding stack | Second P/Invoke surface, 71 symbols, same technique |
| Model | ONNX exports | `ggml-parakeet-tdt-0.6b-v3.bin`, same loader shape |
| Engine implementation | Full | Full — unchanged |
| Model catalog, language list, engine-selection UI | Full | Full — unchanged |

So acquisition got much cheaper; integration did not. Parakeet is a genuinely separate engine — its
own context, state, params, mel and tokenize entry points — not a mode of Whisper. The reason to
defer still holds: a second engine doubles model management, language support, progress semantics
and settings surface while the first one is still being proven against the mac build.

Re-scoped from **L, needs a new runtime** to **M, second interop surface against a DLL we already
produce**. Worth scheduling deliberately after M6, not slipping into the current milestones.

**Obliges** — `selectedEngine` keeps its key and its `whisper` default but has no second value;
`fluidAudioModelVersion` is dropped. The engine interface stays a real interface rather than
collapsing into `WhisperEngine` — that obligation now has a concrete payoff, since the second
implementation is a known quantity rather than a hypothetical. `parakeet.dll` is built but not
copied into artifacts; re-including it is one line in `build-native.ps1`.

### x64 only

**Decided.** One architecture for v1.

ARM64 Windows machines matter, and whisper.cpp builds for them, but a second native target doubles
build artifacts and test surface from M0 onward for a minority of users.

**Obliges** — architecture is a build variable everywhere it appears: CMake target, Rust triple,
`RuntimeIdentifier`, installer. Nothing hardcodes `x64` in a path or a filename, so ARM64 is later
a configuration rather than a port.

### Windows 11 only

**Decided in Rev. 3**, revising Rev. 2's Windows 10 22H2 floor.

Rev. 2 set the floor at Windows 10 on the reasoning that it "keeps almost everyone in scope and
costs little." That was decided without a fact that turns out to matter: Windows 10 mainstream
support ended 14 October 2025, and consumer ESU expires 13 October 2026. The old floor was a floor
at an unsupported OS.

The cost of dropping it is real — a slice of users who have not upgraded. What it buys is a halved
manual test matrix and permission to use Windows 11 APIs where they help.

**Obliges**

- Minimum build 22000, enforced in the app manifest and the installer, with a clear message rather
  than a crash on older systems.
- The Tier 4 OS axis collapses to Windows 11: the current release and one prior.
- Windows 11 window effects (Mica and friends, via `DwmSetWindowAttribute`) are now available to the
  indicator overlay. Optional, not required — the overlay must still be legible if they fail.

### .NET 10 LTS + WPF

**Decided in Rev. 3.** The runtime was written as .NET 8 through Rev. 2; WPF was a standing
recommendation, now ratified.

.NET 8's LTS support ends November 2026, so a new project started on it would need an upgrade before
v1 shipped. .NET 10 has been LTS since November 2025 and is supported to November 2028.

WPF wins on the three surfaces this app actually depends on — a layered click-through overlay, a
tray icon, and low-level input hooks — all of which are raw Win32 interop in every .NET UI
framework, and all of which WPF has hosted for longest. The cost is that WPF looks dated by default,
so the UI needs deliberate styling rather than stock controls.

**Obliges** — `net10.0-windows` target framework; no reliance on WPF's default control appearance;
budget styling time at M6 rather than assuming stock controls will do.

### Our own whisper.dll, hand-written P/Invoke

**Decided in Rev. 3.** Rev. 2 left this open between Whisper.net and direct interop.

Building the DLL ourselves from the pinned submodule guarantees `whisper_vad_*` is present — VAD
gating is central to output quality and not negotiable — and keeps the Windows build on the same
whisper.cpp revision the mac app uses, which is what makes golden-transcript comparison meaningful
rather than approximate.

The cost is a heavier M0: a CMake driver and an interop layer before anything transcribes, where
Whisper.net would have had audio flowing in an afternoon.

**Obliges**

- The interop layer is hand-written and therefore hand-verified. Struct layouts live in one file,
  next to a comment naming the `whisper.h` revision they mirror.
- A submodule bump is a reviewed change, not a routine update — see the P/Invoke drift row in
  [Section 10](#10-risks).
- The engine stays behind the same interface the mac app uses, so swapping in a managed binding
  later is a substitution rather than a rewrite.

### Bundle the default model

**Decided.** `ggml-tiny.en.bin` ships with the installer.

First run works with no network, which matters more for a dictation tool than installer size does:
a user who installs and immediately tries to dictate should not meet a download.

**Obliges** — the installer carries 77 MB; the first-run copy is checked on every launch, not only
the first ([module 08](#08--queue-store--model-manager--port--m)); and the repository-weight and
installer-size rows in [Section 10](#10-risks) both become live concerns at the split and at M7
respectively.

---

## 12. Out of scope

- **Streaming transcription.** Open on the mac side too. Not a porting concern.
- **Custom dictionary and keyword boosting.** Same — an unbuilt feature, not a port.
- **Agent mode.** Same.
- **The Parakeet engine.** Deferred, per [Section 11](#11-settled-decisions).
- **ARM64.** Deferred, per [Section 11](#11-settled-decisions). Build scripts stay
  architecture-parametric so it is later a configuration, not a port.
- **Apple Continuity microphone support.** No counterpart; the detection logic disappears with it.
- **Contributing the Windows client upstream.** A separate conversation with the maintainer, and not
  a prerequisite for any of the above.
