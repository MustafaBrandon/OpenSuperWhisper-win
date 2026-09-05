using System.Diagnostics;
using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Indicator;

/// <summary>A screen point to anchor the indicator near, in physical pixels.</summary>
public readonly record struct AnchorPoint(int X, int Y, string Source);

/// <summary>One way of finding the text cursor.</summary>
public interface ICaretStrategy
{
    string Name { get; }

    /// <summary>Locates the caret, or null when this strategy cannot.</summary>
    AnchorPoint? TryLocate();
}

/// <summary>
/// Finds where to put the indicator, within a strict time budget.
/// Port of the mac app's <c>FocusUtils</c> anchor resolution.
/// </summary>
/// <remarks>
/// <para>
/// The budget is the point. Asking another process where its caret is means a
/// cross-process call that a busy application can stall for seconds; the mac app hit
/// exactly this and capped it. Recording has already started by the time this runs, so
/// a slow answer costs indicator placement, never audio.
/// </para>
/// <para>
/// Strategies run in order, cheapest first, and the first plausible answer wins. The
/// mouse fallback always succeeds, so there is no "no anchor" case to handle.
/// </para>
/// </remarks>
public sealed class CaretLocator(IEnumerable<ICaretStrategy> strategies)
{
    /// <summary>
    /// How long the whole resolution may take before the fallback is used.
    /// </summary>
    /// <remarks>
    /// Inherited from the mac app. Long enough for a responsive app to answer, short
    /// enough that a hung one does not visibly delay the indicator.
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(150);

    private readonly IReadOnlyList<ICaretStrategy> _strategies = [.. strategies];

    /// <summary>
    /// Resolves an anchor, never exceeding <see cref="Budget"/> by more than the
    /// granularity of a single strategy call.
    /// </summary>
    public AnchorPoint Resolve()
    {
        var clock = Stopwatch.StartNew();

        foreach (var strategy in _strategies)
        {
            if (clock.Elapsed >= Budget) break;

            try
            {
                if (strategy.TryLocate() is { } anchor) return anchor;
            }
            catch (Exception)
            {
                // A strategy failing is normal — most applications do not expose a
                // caret to most mechanisms. Move on.
            }
        }

        return MouseAnchor();
    }

    /// <summary>The last-resort anchor: wherever the pointer is.</summary>
    public static AnchorPoint MouseAnchor()
    {
        if (Win32Window.GetCursorPos(out var point))
        {
            return new AnchorPoint(point.X, point.Y, "mouse");
        }

        return new AnchorPoint(0, 0, "origin");
    }

    /// <summary>
    /// Rejects rectangles that cannot be a real caret.
    /// </summary>
    /// <remarks>
    /// Applications report degenerate rectangles more often than one would like —
    /// all-zero when they have no caret, or a position on a monitor that no longer
    /// exists. Placing the indicator at the origin because of one is worse than
    /// falling through to the mouse.
    /// </remarks>
    public static bool IsPlausibleCaret(Win32Window.Rect rect)
    {
        if (rect.Left == 0 && rect.Top == 0 && rect.Right == 0 && rect.Bottom == 0) return false;

        // A caret is a thin vertical bar: zero width is normal, zero height is not.
        if (rect.Height <= 0) return false;

        // Absurd values indicate a misreported rectangle rather than a real position.
        const int limit = 100_000;
        if (Math.Abs(rect.Left) > limit || Math.Abs(rect.Top) > limit) return false;

        return true;
    }
}

/// <summary>
/// Finds the caret through <c>GetGUIThreadInfo</c>.
/// </summary>
/// <remarks>
/// The cheap first try: a synchronous call with no COM and no cross-process wait. It
/// works for classic Win32 edit controls — Notepad, many native dialogs — and reports
/// nothing for WPF, Chromium, Electron and UWP, which is why it runs before the more
/// expensive strategies rather than instead of them.
/// </remarks>
public sealed class Win32CaretStrategy : ICaretStrategy
{
    public string Name => "win32-caret";

    public AnchorPoint? TryLocate()
    {
        var foreground = Win32Window.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        var threadId = Win32Window.GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0) return null;

        var info = new Win32Window.GuiThreadInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Window.GuiThreadInfo>(),
        };

        if (!Win32Window.GetGUIThreadInfo(threadId, ref info)) return null;
        if (info.hwndCaret == IntPtr.Zero) return null;
        if (!CaretLocator.IsPlausibleCaret(info.rcCaret)) return null;

        // rcCaret is client-relative; the indicator needs screen coordinates.
        var point = new Win32Window.Point { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        if (!Win32Window.ClientToScreen(info.hwndCaret, ref point)) return null;

        return new AnchorPoint(point.X, point.Y, Name);
    }
}
