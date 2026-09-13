using System.Windows;
using System.Windows.Controls;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Settings;

// Core's MouseButton, not System.Windows.Input's — the WPF one has no None.
using MouseButton = OpenSuperWhisper.Core.Input.MouseButton;

namespace OpenSuperWhisper.App;

/// <summary>
/// First-run setup: pick a trigger, confirm the microphone works, learn where the app
/// lives.
/// </summary>
/// <remarks>
/// There are no permissions to request on Windows, so the mac app's onboarding — which
/// is mostly accessibility and input-monitoring consent — does not carry over. What
/// remains is the three things a new user cannot discover on their own: which key to
/// hold, whether their microphone is the one being listened to, and that the app has no
/// main window.
/// <para>
/// The "try it" step deliberately uses the real dictation path rather than a simulated
/// one. It is the only check that covers trigger, capture, transcription and insertion
/// together — the join that no automated test can drive, because SendInput marks its
/// events injected and the coordinator drops injected events by design. A user who
/// completes this step has proven the whole chain on their own hardware.
/// </para>
/// </remarks>
public partial class OnboardingWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly MicrophoneService _microphones;
    private readonly DictationController _controller;

    private int _page;

    /// <summary>
    /// Set while the controls are being populated.
    /// </summary>
    /// <remarks>
    /// Setting <c>SelectedIndex</c> raises SelectionChanged, so without this the
    /// microphone handler writes settings during construction — a pointless save and a
    /// pointless re-apply before the user has chosen anything.
    /// </remarks>
    private bool _loading = true;

    public OnboardingWindow(SettingsStore settings, MicrophoneService microphones,
        DictationController controller)
    {
        InitializeComponent();

        _settings = settings;
        _microphones = microphones;
        _controller = controller;

        BuildTriggerList();
        BuildMicrophoneList();

        _controller.Transcribed += OnTranscribed;
        _loading = false;

        ShowPage(0);
    }

    private StackPanel[] Pages => [PageWelcome, PageTry, PageDone];

    // =====================================================================
    // Page 1 — trigger
    // =====================================================================

    private void BuildTriggerList()
    {
        foreach (var key in TriggerBindings.SelectableModifiers)
        {
            TriggerCombo.Items.Add(new ComboBoxItem { Content = key.DisplayName(), Tag = $"key:{key}" });
        }

        foreach (var button in TriggerBindings.SelectableButtons)
        {
            TriggerCombo.Items.Add(new ComboBoxItem { Content = button.DisplayName(), Tag = $"mouse:{button}" });
        }

        var current = TriggerBindings.Resolve(_settings.Current);

        var tag = current.Mode == TriggerMode.MouseButton
            ? $"mouse:{current.Button}"
            : $"key:{current.Key}";

        for (var i = 0; i < TriggerCombo.Items.Count; i++)
        {
            if (TriggerCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
            {
                TriggerCombo.SelectedIndex = i;
                break;
            }
        }

        // A shortcut set by hand leaves nothing selected here; onboarding does not offer
        // shortcut recording, so fall back to showing the default rather than a blank.
        if (TriggerCombo.SelectedIndex < 0) TriggerCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Applies the trigger chosen on page 1, so page 2 can actually test it.
    /// </summary>
    /// <remarks>
    /// Written through the settings store rather than straight to the coordinator: that
    /// is what makes it survive the window closing, and the store's change event rebinds
    /// the hooks on the way past.
    /// </remarks>
    private void ApplyTrigger()
    {
        if ((TriggerCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

        _settings.Update(s =>
        {
            if (tag.StartsWith("mouse:", StringComparison.Ordinal))
            {
                s.MouseButtonHotkey = tag["mouse:".Length..];
                return;
            }

            s.MouseButtonHotkey = nameof(MouseButton.None);

            var key = tag["key:".Length..];
            s.ModifierOnlyHotkey = key;
            s.LastModifierOnlyHotkey = key;

            // Chosen here means chosen: a shortcut left over from a previous run would
            // otherwise never be reached, since a bare modifier outranks it.
            s.ShortcutHotkey = string.Empty;
        });

        Log.Write($"onboarding: trigger set to {tag}");
    }

    private string TriggerDescription()
    {
        var resolved = TriggerBindings.Resolve(_settings.Current);

        return resolved.Mode switch
        {
            TriggerMode.MouseButton => $"hold {resolved.Button.DisplayName()}",
            TriggerMode.Shortcut => $"hold {KeyboardLayoutProvider.DisplayName(resolved.Shortcut)}",
            _ => $"hold {resolved.Key.DisplayName()}",
        };
    }

    // =====================================================================
    // Page 2 — microphone and a real dictation
    // =====================================================================

    private void BuildMicrophoneList()
    {
        MicrophoneCombo.Items.Clear();
        MicrophoneCombo.Items.Add(new ComboBoxItem { Content = "System default", Tag = string.Empty });

        foreach (var device in _microphones.Devices)
        {
            var label = device.RequiresWarmUp ? $"{device.Name}  (Bluetooth)" : device.Name;
            MicrophoneCombo.Items.Add(new ComboBoxItem { Content = label, Tag = device.Id });
        }

        var preferred = _settings.Current.SelectedMicrophoneId ?? string.Empty;

        for (var i = 0; i < MicrophoneCombo.Items.Count; i++)
        {
            if (MicrophoneCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == preferred)
            {
                MicrophoneCombo.SelectedIndex = i;
                break;
            }
        }

        if (MicrophoneCombo.SelectedIndex < 0) MicrophoneCombo.SelectedIndex = 0;
    }

    private void OnMicrophoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if ((MicrophoneCombo.SelectedItem as ComboBoxItem)?.Tag is not string id) return;

        // Applied at once rather than on Next: the point of this page is to try the
        // microphone, which requires the one being chosen to be the one in use.
        _settings.Update(s => s.SelectedMicrophoneId = string.IsNullOrEmpty(id) ? null : id);
    }

    private void OnTranscribed(object? sender, string text)
    {
        // Raised from the transcription thread.
        Dispatcher.Invoke(() =>
        {
            if (!IsLoaded) return;

            TryStatus.Text = string.IsNullOrWhiteSpace(text)
                ? "Nothing was heard. Check the microphone above and try again."
                : "That worked. It reaches any application the same way.";
        });
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    private void ShowPage(int index)
    {
        _page = Math.Clamp(index, 0, Pages.Length - 1);

        for (var i = 0; i < Pages.Length; i++)
        {
            Pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        }

        BackButton.IsEnabled = _page > 0;
        NextButton.Content = _page == Pages.Length - 1 ? "Finish" : "Next";
        SkipButton.Visibility = _page == Pages.Length - 1 ? Visibility.Collapsed : Visibility.Visible;

        if (_page == 1)
        {
            TryInstruction.Text =
                $"Click in the box below, then {TriggerDescription()} and say a sentence. "
                + "Let go and it will be typed in, exactly as it would be anywhere else.";

            TryStatus.Text = string.Empty;

            // Focused so the transcript has somewhere to land — the text goes to
            // whatever has focus, which is the entire point of the app.
            TryBox.Focus();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowPage(_page - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        // The trigger is applied on the way out of page 1 so the next page can use it.
        if (_page == 0) ApplyTrigger();

        if (_page < Pages.Length - 1)
        {
            ShowPage(_page + 1);
            return;
        }

        Complete();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Complete();

    /// <summary>
    /// Marks onboarding done so it never runs again.
    /// </summary>
    /// <remarks>
    /// Skipping counts as completing. Someone who dismissed this once has said what they
    /// think of it, and showing it again at every launch would be the app arguing.
    /// </remarks>
    private void Complete()
    {
        _settings.Update(s => s.HasCompletedOnboarding = true);
        Log.Write("onboarding complete");

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _controller.Transcribed -= OnTranscribed;

        // Closing with the X is the same decision as Skip. Asking again next launch
        // would be worse than taking the hint.
        if (!_settings.Current.HasCompletedOnboarding)
        {
            _settings.Update(s => s.HasCompletedOnboarding = true);
        }

        base.OnClosed(e);
    }
}
