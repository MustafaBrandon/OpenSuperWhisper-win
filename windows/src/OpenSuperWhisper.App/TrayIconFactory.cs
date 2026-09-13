using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.App;

/// <summary>What the tray icon is saying.</summary>
public enum TrayIconState
{
    /// <summary>Present, not listening.</summary>
    Idle,

    /// <summary>The microphone is open.</summary>
    Recording,
}

/// <summary>
/// Draws the notification-area icon from a vector glyph.
/// </summary>
/// <remarks>
/// <para>
/// Rendered rather than loaded from files, for two reasons. The taskbar asks for
/// different pixel sizes at different DPI — 16 at 100%, 20 at 125%, 24 at 150%, 32 at
/// 200% — and a vector gives each of them a clean render instead of a resampled one.
/// And the glyph has to change colour: macOS tints a template image for the menu bar
/// automatically, <b>Windows does not</b>, so a black glyph is invisible on the default
/// dark taskbar and a white one is invisible on a light one. The theme has to be read
/// and the icon redrawn.
/// </para>
/// <para>
/// Every size goes into one multi-frame icon and Windows picks the frame it wants, so
/// there is no DPI-change handling here at all — the alternative is listening for
/// per-monitor DPI changes and re-rendering, which is a great deal more code for the
/// same picture.
/// </para>
/// </remarks>
public static class TrayIconFactory
{
    /// <summary>
    /// Sizes Windows asks for across the DPI range, smallest first.
    /// </summary>
    private static readonly int[] Sizes = [16, 20, 24, 32];

    /// <summary>
    /// Recording red. Deliberately not the accent colour.
    /// </summary>
    /// <remarks>
    /// A tool that hears everything said near it should say so somewhere that is always
    /// visible, and the indicator overlay is not — it sits by the caret, which is
    /// wherever the user is typing rather than wherever they are looking. Red is the
    /// one colour that reads as "live" without a legend, and it stays legible on both
    /// taskbar themes.
    /// </remarks>
    private static readonly Color RecordingColor = Color.FromRgb(0xE8, 0x3E, 0x48);

    /// <summary>
    /// Rendered icon files, keyed by state and taskbar theme.
    /// </summary>
    /// <remarks>
    /// Bytes rather than <see cref="System.Drawing.Icon"/> instances, deliberately.
    /// The tray control takes ownership of the icon it is handed and disposes it when
    /// it is replaced, so a cache of live Icon objects hands out a disposed one the
    /// second time round — which presented as the dictation silently aborting, because
    /// the throw happened inside the handler the trigger thread was waiting on.
    /// <para>
    /// Caching the bytes keeps the expensive half (rendering four sizes through WPF)
    /// and makes every handout a fresh, independently owned object.
    /// </para>
    /// </remarks>
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(TrayIconState, bool), byte[]> Cache = [];

    private static Geometry? _glyph;

    /// <summary>
    /// The taskbar's own brightness, which is what the icon has to contrast against.
    /// </summary>
    /// <remarks>
    /// <c>SystemUsesLightTheme</c>, not <c>AppsUseLightTheme</c>: they are separate
    /// settings and a light-app/dark-taskbar combination is common. Reading the wrong
    /// one produces an icon that is invisible for exactly the users who mixed them.
    /// </remarks>
    public static bool TaskbarIsLight
    {
        get
        {
            try
            {
                var value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", null);

                // Absent means the default, which on Windows 11 is a dark taskbar.
                return value is int light && light != 0;
            }
            catch (Exception ex)
            {
                Log.Error("could not read the taskbar theme", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// An icon for this state, matched to the current taskbar theme.
    /// </summary>
    /// <returns>
    /// A fresh icon each call. The caller owns it — the tray control disposes the one
    /// it holds when it is replaced.
    /// </returns>
    public static System.Drawing.Icon Create(TrayIconState state)
    {
        var light = TaskbarIsLight;
        byte[] bytes;

        lock (Gate)
        {
            if (!Cache.TryGetValue((state, light), out var cached))
            {
                cached = Render(state, light);
                Cache[(state, light)] = cached;
            }

            bytes = cached;
        }

        // A new stream per icon: Icon reads it in the constructor, but sharing one
        // would mean sharing its position too.
        using var stream = new MemoryStream(bytes, writable: false);
        return new System.Drawing.Icon(stream);
    }

    /// <summary>
    /// Forgets the rendered icons, so the next <see cref="Create"/> re-reads the theme.
    /// </summary>
    /// <remarks>
    /// They are cached because rendering four sizes through WPF is not free. A theme
    /// change is the one event that invalidates them.
    /// </remarks>
    public static void Invalidate()
    {
        lock (Gate) Cache.Clear();
    }

    private static byte[] Render(TrayIconState state, bool taskbarIsLight)
    {
        var color = state == TrayIconState.Recording
            ? RecordingColor
            : taskbarIsLight ? Colors.Black : Colors.White;

        var frames = Sizes.ToDictionary(size => size, size => RenderPng(size, color));

        return IconFrom(frames);
    }

    /// <summary>One frame of the icon, as PNG bytes.</summary>
    private static byte[] RenderPng(int pixels, Color color)
    {
        var glyph = Glyph();
        var bounds = glyph.Bounds;

        // A small margin: a glyph run right to the edge looks larger than its
        // neighbours in the tray, where every other icon is drawn with padding.
        var box = pixels * 0.92;
        var scale = Math.Min(box / bounds.Width, box / bounds.Height);

        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(
            (pixels - bounds.Width * scale) / 2,
            (pixels - bounds.Height * scale) / 2));

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(transform);
            context.DrawGeometry(new SolidColorBrush(color), null, glyph);
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return stream.ToArray();
    }

    /// <summary>
    /// Packs PNG frames into a multi-size icon.
    /// </summary>
    /// <remarks>
    /// PNG-compressed frames rather than the older uncompressed bitmap layout: they
    /// have been supported since Vista and this app's floor is Windows 11, so the only
    /// thing the legacy format would buy is a larger file.
    /// </remarks>
    private static byte[] IconFrom(Dictionary<int, byte[]> frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // type: icon
        writer.Write((ushort)frames.Count);

        var offset = 6 + 16 * frames.Count;

        foreach (var (size, png) in frames)
        {
            // 0 means 256 in this field; every size here is smaller, but the rule is
            // worth respecting in case a larger frame is ever added.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);               // palette entries
            writer.Write((byte)0);               // reserved
            writer.Write((ushort)1);             // colour planes
            writer.Write((ushort)32);            // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);

            offset += png.Length;
        }

        foreach (var png in frames.Values) writer.Write(png);

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// The glyph, parsed once.
    /// </summary>
    /// <remarks>
    /// Converted from the mac app's <c>tray_icon.pdf</c> — see assets/README.md. Frozen
    /// so it can be drawn from any thread without WPF objecting to cross-thread access.
    /// </remarks>
    private static Geometry Glyph()
    {
        if (_glyph is not null) return _glyph;

        var assembly = Assembly.GetExecutingAssembly();
        var name = $"{typeof(TrayIconFactory).Namespace}.tray-glyph.path";

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded tray glyph '{name}' is missing.");

        using var reader = new StreamReader(stream);
        var geometry = Geometry.Parse(reader.ReadToEnd());
        geometry.Freeze();

        _glyph = geometry;
        return geometry;
    }
}
