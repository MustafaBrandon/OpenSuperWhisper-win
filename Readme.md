# OpenSuperWhisper for Windows

Hold a key, speak, let go — what you said is transcribed and typed into whatever
application you were working in: an email, a terminal, a chat box. Transcription runs
entirely on your own machine.

This is a fork of [Starmel/OpenSuperWhisper](https://github.com/Starmel/OpenSuperWhisper)
that adds a **native Windows client**. It shares the macOS app's engine and its
behaviour, but not its code — SwiftUI and AppKit have no Windows counterpart, so
everything that touches the operating system is written fresh against Win32. What
carries over, what was rewritten, and why, is documented in
[`docs/windows-port.md`](docs/windows-port.md).

**The Windows client lives in [`windows/`](windows/).** The macOS sources in this tree
are upstream's, kept as a reference for the port; they are not built here.

## Status

Pre-1.0 and **not code-signed**. The dictation loop is complete and verified on real
hardware: trigger, capture, transcription, and insertion into other applications, with
settings, model management, history and a first-run flow. 297 automated tests.

What is missing is mostly distribution rather than function — see
[Known gaps](#known-gaps).

### What it does

- **Three trigger modes** — a bare modifier key (right Ctrl by default), a mouse button,
  or a key combination such as `Alt` + `` ` ``. Hold-to-record, press-to-toggle, or
  require a double tap.
- **Types into the focused application**, by clipboard paste or by Unicode typing, with
  the clipboard restored afterwards unless you took it over in the meantime.
- **A floating indicator** beside your cursor while recording, with Escape to discard.
- **History** of transcripts and audio — play back, copy, re-transcribe with a better
  model, delete, or turn the whole thing off so nothing is written at all.
- **Model manager** — the app ships with `tiny.en` so it works offline on first run, and
  larger Whisper models can be downloaded from Settings.
- **Audio files** — drop them on the History window, or open them with the app.
- **Tray menu** for microphone and transcription language.
- **No network access** except model downloads. Nothing you say leaves the machine.

### Known gaps

Stated plainly, because finding these out by surprise is worse than reading about them:

| | |
| --- | --- |
| **Not code-signed** | SmartScreen warns on any downloaded build. Getting past it is *More info → Run anyway*. |
| **Bluetooth microphones** | Warm-up detection is implemented and unit-tested, but has never run against a real Bluetooth headset. |
| **Lock/unlock** | Windows gives no signal when it silently removes an input hook. A watchdog reinstalls every 30 s, which bounds the damage; the lock/unlock case itself is unverified. |
| **Asian autocorrect** | The preference and the Rust library exist; nothing calls it yet. Deliberately absent from the UI rather than present and inert. |
| **Parakeet engine** | Not ported. Whisper only. |
| **ARM64** | Not built. The build scripts are architecture-parametric, so it is a configuration rather than a port. |
| **No update check** | Updating means downloading a new build. |

Windows 11 (build 22000) or later, x64. Windows 10 is not supported — it left mainstream
support in October 2025.

## Installing

There is no published release yet. To build one:

```powershell
git clone https://github.com/MustafaBrandon/OpenSuperWhisper-win.git
cd OpenSuperWhisper-win
git submodule update --init --recursive

./windows/native/build-native.ps1 -Configuration Release
./windows/installer/build-installer.ps1
```

That produces a per-user installer (no administrator rights needed) in
`windows/build/installer`. Prerequisites and the developer workflow are in
[`windows/README.md`](windows/README.md).

## Credits

The macOS application, its design, and the artwork this port reuses are the work of
[Starmel](https://github.com/Starmel/OpenSuperWhisper) and its contributors. The
behavioural specification the Windows client implements was read out of that source —
including the constants that encode real debugging, like the 1.5-second clipboard
restore delay and the 0.25 seconds of extra audio captured after the key is released.

Speech recognition is [whisper.cpp](https://github.com/ggerganov/whisper.cpp), built
from the pinned submodule.

MIT licensed, as upstream is. See [LICENSE](LICENSE).

## The macOS app

Unchanged from upstream, and not built by anything in `windows/`. Install it with
`brew install opensuperwhisper`, or from
[upstream's releases](https://github.com/Starmel/OpenSuperWhisper/releases); its
documentation, screenshots and build instructions live in
[upstream's repository](https://github.com/Starmel/OpenSuperWhisper).

<p align="center">
<img src="docs/image.png" width="400" alt="The macOS app's main window, showing transcription history" /> <img src="docs/image_indicator.png" width="400" alt="The macOS recording indicator" />
</p>

<p align="center"><em>The macOS app. The Windows client follows the same behaviour with
a native interface.</em></p>

### Whisper models

Model files (`.bin`) come from the
[whisper.cpp Hugging Face repository](https://huggingface.co/ggerganov/whisper.cpp/tree/main).
Both apps copy a default model into place on first launch, and can download larger ones
from their settings.

For Hebrew, the **Turbo V3 Hebrew** entry is [ivrit.ai](https://www.ivrit.ai/)'s
fine-tune of `whisper-large-v3-turbo`
([whisper-large-v3-turbo-ggml](https://huggingface.co/ivrit-ai/whisper-large-v3-turbo-ggml)).
Selecting it sets the transcription language to Hebrew, which these models need set
explicitly — on auto-detect they measurably degrade.
