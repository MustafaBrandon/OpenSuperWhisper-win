# Assets

Both files derive from the mac app's artwork, which is MIT-licensed along with the
rest of this repository. They are **committed in generated form** so the build never
reaches outside `windows/` — see the split rules in [`../README.md`](../README.md).

| File | Source | Used by |
| --- | --- | --- |
| `app.ico` | `OpenSuperWhisper/AppIcon.icns` | `<ApplicationIcon>`: Explorer, taskbar, Alt-Tab, and the M7 installer and shortcut |
| `tray-glyph.path` | `OpenSuperWhisper/tray_icon.pdf` | `TrayIconFactory`, rendered at run time |

## app.ico

Eight frames — 16, 20, 24, 32, 48, 64, 128, 256 — PNG-compressed, which Windows has
read since Vista and this app's floor is Windows 11. The `.icns` supplies 16, 32, 64,
128, 256, 512 and 1024 directly; 20, 24 and 48 are resampled from the largest frame.

## tray-glyph.path

WPF path mini-language, converted from the PDF's content stream: `m`/`l`/`c`/`h`
operators map to `M`/`L`/`C`/`Z`, and the leading `1 0 0 -1 0 1536 cm` flip is already
applied, so the coordinates are top-down.

The whole glyph is one flat shape — the mac app draws it as a template image, where
the head fill and the feature strokes are all the same colour. It renders as a
silhouette at every size, which is what makes it legible at 16 px.

**It carries no colour of its own.** macOS tints a template image for the menu bar
automatically; Windows does not, so `TrayIconFactory` picks black or white from the
taskbar theme, and red while recording.

## Replacing the artwork

The pipeline is artwork-independent: drop in a new `app.ico` and a new single-shape
path, and nothing else changes. The path must be a filled silhouette rather than an
outline — an outline loses its detail at 16 px, which is the size that matters most.
