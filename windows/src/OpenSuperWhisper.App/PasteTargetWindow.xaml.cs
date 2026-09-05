using System.Windows;
using System.Windows.Controls;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.App;

/// <summary>
/// A focusable text box used to verify that insertion actually lands.
/// </summary>
/// <remarks>
/// Started with <c>--paste-target</c>. Insertion cannot be verified against a return
/// code — <c>SendInput</c> reports success whether or not anything receives the
/// keystroke — and testing against a real application means typing into a document
/// somebody cares about. This is a target we own and can read back.
/// </remarks>
public sealed class PasteTargetWindow : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _status;

    public PasteTargetWindow()
    {
        Title = "OpenSuperWhisper — paste target";
        Width = 640;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _status = new TextBlock
        {
            Margin = new Thickness(12, 12, 12, 6),
            TextWrapping = TextWrapping.Wrap,
            Text = "Focus this box, then trigger a dictation or run: osw insert \"hello\"\n"
                 + "Anything that arrives is echoed to the log.",
        };

        _input = new TextBox
        {
            Margin = new Thickness(12, 6, 12, 12),
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14,
        };

        _input.TextChanged += (_, _) =>
        {
            Log.Write($"paste-target received: '{_input.Text.Replace("\r\n", "\\n")}'");
            _status.Text = $"Received {_input.Text.Length} characters. See the log.";
        };

        var panel = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        panel.Children.Add(_status);
        panel.Children.Add(_input);

        Content = panel;

        // Focus the box on open so a paste has somewhere to land without the operator
        // having to click first.
        Loaded += (_, _) => _input.Focus();
    }
}
