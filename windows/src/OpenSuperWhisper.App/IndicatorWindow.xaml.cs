using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenSuperWhisper.Core.Indicator;

namespace OpenSuperWhisper.App;

/// <summary>
/// The floating status card. Port of the mac app's indicator panel.
/// </summary>
/// <remarks>
/// <para>
/// Must never take focus. The user is typing in another application, and stealing
/// focus would both interrupt them and break the paste, which targets whatever is
/// foreground.
/// </para>
/// <para>
/// Focus avoidance takes three separate mechanisms, none of which is sufficient alone:
/// <c>ShowActivated="False"</c> stops WPF activating on show, <c>WS_EX_NOACTIVATE</c>
/// stops Windows activating on click, and <c>WS_EX_TRANSPARENT</c> makes clicks pass
/// through to the window underneath.
/// </para>
/// </remarks>
public partial class IndicatorWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Gap between the caret and the card, in device-independent pixels.</summary>
    private const double AnchorOffset = 24;

    private readonly Storyboard _pulse;

    public IndicatorWindow()
    {
        InitializeComponent();

        _pulse = BuildPulse();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);

        // TOOLWINDOW additionally keeps it out of Alt+Tab, where a status card would
        // be noise rather than a destination.
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Applies a state to the card.</summary>
    public void Render(IndicatorState state, TimeSpan? elapsed, bool confirmingCancel)
    {
        var (text, colour) = Describe(state, confirmingCancel);

        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(colour);

        ElapsedText.Text = state is IndicatorState.Recording && elapsed is { } value
            ? $"{(int)value.TotalMinutes:00}:{value.Seconds:00}"
            : string.Empty;

        if (state == IndicatorState.Recording) StartPulse();
        else StopPulse();
    }

    private static (string Text, Color Colour) Describe(IndicatorState state, bool confirmingCancel) =>
        state switch
        {
            // The confirmation prompt has to say what a second press will do. "Press
            // Esc again" is the whole point of arming rather than cancelling.
            IndicatorState.Recording when confirmingCancel
                => ("Press Esc again to discard", Color.FromRgb(0xFF, 0x9F, 0x0A)),

            IndicatorState.Recording => ("Recording", Color.FromRgb(0xFF, 0x3B, 0x30)),
            IndicatorState.Connecting => ("Connecting…", Color.FromRgb(0xFF, 0x9F, 0x0A)),
            IndicatorState.Decoding => ("Transcribing…", Color.FromRgb(0x0A, 0x84, 0xFF)),
            IndicatorState.Busy => ("Busy — still transcribing", Color.FromRgb(0xFF, 0x9F, 0x0A)),
            IndicatorState.NoMicrophone => ("No microphone", Color.FromRgb(0xFF, 0x45, 0x3A)),
            _ => ("", Colors.Transparent),
        };

    /// <summary>
    /// Positions the card near an anchor, keeping it on the anchor's monitor.
    /// </summary>
    /// <remarks>
    /// The anchor arrives in physical pixels from a caret rectangle, while WPF lays out
    /// in device-independent ones. On a mixed-DPI desktop the conversion has to use the
    /// scale of the monitor the card will actually appear on, not the primary's.
    /// </remarks>
    public void MoveToAnchor(AnchorPoint anchor)
    {
        // Measure before positioning: SizeToContent means Width and Height are only
        // meaningful once layout has run.
        UpdateLayout();

        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var deviceX = anchor.X / scaleX;
        var deviceY = anchor.Y / scaleY;

        var screen = ScreenBoundsFor(anchor, scaleX, scaleY);

        // Prefer below the caret; flip above when that would run off the bottom.
        var left = deviceX - (ActualWidth / 2);
        var top = deviceY + AnchorOffset;

        if (top + ActualHeight > screen.Bottom) top = deviceY - AnchorOffset - ActualHeight;

        Left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - ActualWidth));
        Top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - ActualHeight));
    }

    private static Rect ScreenBoundsFor(AnchorPoint anchor, double scaleX, double scaleY)
    {
        var work = Interop.Win32Window.WorkAreaForPoint(
            new Interop.Win32Window.Point { X = anchor.X, Y = anchor.Y });

        return new Rect(
            work.Left / scaleX,
            work.Top / scaleY,
            work.Width / scaleX,
            work.Height / scaleY);
    }

    private Storyboard BuildPulse()
    {
        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.25,
            Duration = IndicatorStateMachine.BlinkInterval,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(fade, StatusDot);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        var board = new Storyboard();
        board.Children.Add(fade);
        return board;
    }

    private void StartPulse() => _pulse.Begin(this, true);

    private void StopPulse()
    {
        _pulse.Stop(this);
        StatusDot.Opacity = 1.0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
