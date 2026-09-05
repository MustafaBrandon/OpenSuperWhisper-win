using System.Windows.Automation;
using System.Windows.Automation.Text;
using OpenSuperWhisper.Core.Indicator;

namespace OpenSuperWhisper.App;

/// <summary>
/// Locates the caret through UI Automation.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of the mac app's Accessibility path, and it shares that path's
/// unreliability: Chromium, Electron and Java surfaces frequently expose no text
/// pattern, or expose one that returns nothing. That is why it sits behind a budget
/// and in front of a fallback rather than being trusted on its own.
/// </para>
/// <para>
/// It lives in the app rather than Core because the managed UIA wrappers ship with the
/// WPF assemblies, and Core deliberately does not depend on WPF.
/// </para>
/// </remarks>
public sealed class UiaCaretStrategy : ICaretStrategy
{
    public string Name => "uia";

    public AnchorPoint? TryLocate()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null) return null;

        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return null;
        if (raw is not TextPattern pattern) return null;

        var selection = pattern.GetSelection();
        if (selection is null || selection.Length == 0) return null;

        var range = selection[0];

        // A collapsed range - the normal case for a caret with nothing selected - often
        // reports no rectangles at all. Expanding by one character gives something
        // measurable while staying at the insertion point.
        var rectangles = range.GetBoundingRectangles();
        if (rectangles.Length == 0)
        {
            var probe = range.Clone();
            probe.ExpandToEnclosingUnit(TextUnit.Character);
            rectangles = probe.GetBoundingRectangles();
        }

        if (rectangles.Length == 0) return null;

        var rect = rectangles[0];
        if (double.IsNaN(rect.X) || double.IsNaN(rect.Y)) return null;

        var bounds = new Interop.Win32Window.Rect
        {
            Left = (int)rect.Left,
            Top = (int)rect.Top,
            Right = (int)rect.Right,
            Bottom = (int)rect.Bottom,
        };

        if (!CaretLocator.IsPlausibleCaret(bounds)) return null;

        // Anchor at the bottom-left of the caret: the indicator hangs below the line
        // being typed, not over it.
        return new AnchorPoint(bounds.Left, bounds.Bottom, Name);
    }
}
