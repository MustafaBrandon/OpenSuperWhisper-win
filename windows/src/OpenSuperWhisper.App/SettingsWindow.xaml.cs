using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Core.Settings;

namespace OpenSuperWhisper.App;

/// <summary>
/// The settings surface. Every preference in the plan's Â§6 is reachable here.
/// </summary>
/// <remarks>
/// Edits a clone and applies it only on Save, so Cancel genuinely discards. Model
/// downloads are the exception: they act on the filesystem immediately, because a
/// download is not a preference and cancelling the dialog should not undo one.
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly ModelManager _models;
    private readonly MicrophoneService _microphones;
    private readonly RecordingStore? _history;
    private readonly AppSettings _draft;

    private readonly Dictionary<string, CancellationTokenSource> _downloads = [];

    public SettingsWindow(SettingsStore store, ModelManager models, MicrophoneService microphones,
        RecordingStore? history = null)
    {
        InitializeComponent();

        _history = history;

        _store = store;
        _models = models;
        _microphones = microphones;
        _draft = store.Current.Clone();

        LoadFromDraft();
        BuildModelList();
        UpdateDiskSpace();

        PathsText.Text = $"Settings   {store.Path}\nModels     {models.ModelsDirectory}\nRecordings {AppPaths.Recordings}";
    }

    // =====================================================================
    // Load
    // =====================================================================

    private void LoadFromDraft()
    {
        // Trigger: modifiers and mouse buttons in one list, since they are mutually
        // exclusive and presenting them as separate controls implies they are not.
        foreach (var key in TriggerBindings.SelectableModifiers)
        {
            TriggerCombo.Items.Add(new ComboBoxItem { Content = key.DisplayName(), Tag = $"key:{key}" });
        }

        foreach (var button in TriggerBindings.SelectableButtons)
        {
            TriggerCombo.Items.Add(new ComboBoxItem { Content = button.DisplayName(), Tag = $"mouse:{button}" });
        }

        SelectTrigger();

        foreach (var (code, name) in LanguageCatalog.ForDisplay())
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }

        SelectByTag(LanguageCombo, _draft.WhisperLanguage);

        BuildMicrophoneList();
        BuildActiveModelList();

        HoldToRecordCheck.IsChecked = _draft.HoldToRecord;
        DoubleTapCheck.IsChecked = _draft.DoublePressToTrigger;
        PlaySoundCheck.IsChecked = _draft.PlaySoundOnRecordStart;
        EscNoConfirmCheck.IsChecked = _draft.EscCancelWithoutConfirmation;

        AutoPasteCheck.IsChecked = _draft.AutoPasteTranscription;
        AutoCopyCheck.IsChecked = _draft.AutoCopyToClipboard;
        UnicodeTypingCheck.IsChecked = _draft.UseUnicodeTyping;
        TrailingSpaceCheck.IsChecked = _draft.AddSpaceAfterSentence;

        BeamSearchCheck.IsChecked = _draft.UseBeamSearch;
        InitialPromptBox.Text = _draft.InitialPrompt;
        AsianAutocorrectCheck.IsChecked = _draft.UseAsianAutocorrect;
        TimestampsCheck.IsChecked = _draft.ShowTimestamps;

        StartHiddenCheck.IsChecked = _draft.StartHiddenInTray;
        SaveHistoryCheck.IsChecked = _draft.SaveTranscriptionHistory;
        AutoDeleteCheck.IsChecked = _draft.AutoDeleteRecordingsEnabled;
        RetentionDaysBox.Text = _draft.AutoDeleteRecordingsAfterDays.ToString();

        UpdateHistoryCount();
    }

    private void UpdateHistoryCount()
    {
        if (_history is null)
        {
            HistoryCountText.Text = string.Empty;
            ClearHistoryButton.IsEnabled = false;
            OpenHistoryButton.IsEnabled = false;
            return;
        }

        var count = _history.Count();

        HistoryCountText.Text = count == 0
            ? "No recordings stored."
            : $"{count} recording{(count == 1 ? string.Empty : "s")} stored in {AppPaths.Recordings}";

        ClearHistoryButton.IsEnabled = count > 0;
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e) => OpenHistoryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when the user asks to see history, so the app can own that window.</summary>
    public event EventHandler? OpenHistoryRequested;

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_history is null) return;

        var count = _history.Count();

        var confirm = MessageBox.Show(
            $"Delete all {count} recording(s) and their audio?\n\nThis cannot be undone.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        // Acts immediately rather than on Save. Deleting data is not a preference, and
        // making it wait for Save would leave "Cancel" ambiguous about whether the
        // deletion happened.
        var removed = _history.DeleteAll();
        Log.Write($"history cleared from settings: {removed} recording(s) removed");

        UpdateHistoryCount();
        StatusText.Text = $"Removed {removed} recording(s).";
    }

    private void SelectTrigger()
    {
        // A configured mouse button takes precedence over a modifier key, matching the
        // coordinator's own rule.
        var wanted = Enum.TryParse<MouseButton>(_draft.MouseButtonHotkey, out var button)
            && button != MouseButton.None
                ? $"mouse:{button}"
                : $"key:{_draft.ModifierOnlyHotkey}";

        SelectByTag(TriggerCombo, wanted);

        if (TriggerCombo.SelectedIndex < 0) SelectByTag(TriggerCombo, $"key:{ModifierKey.RightControl}");
    }

    private void BuildMicrophoneList()
    {
        MicrophoneCombo.Items.Clear();

        // Null tag means "system default", which is what most users want and what the
        // app falls back to when a chosen device disappears.
        MicrophoneCombo.Items.Add(new ComboBoxItem { Content = "System default", Tag = string.Empty });

        foreach (var device in _microphones.Devices)
        {
            var label = device.RequiresWarmUp ? $"{device.Name}  (Bluetooth)" : device.Name;
            MicrophoneCombo.Items.Add(new ComboBoxItem { Content = label, Tag = device.Id });
        }

        SelectByTag(MicrophoneCombo, _draft.SelectedMicrophoneId ?? string.Empty);
        if (MicrophoneCombo.SelectedIndex < 0) MicrophoneCombo.SelectedIndex = 0;
    }

    private void BuildActiveModelList()
    {
        ActiveModelCombo.Items.Clear();

        foreach (var path in _models.InstalledModelPaths())
        {
            var filename = Path.GetFileName(path);
            var size = new FileInfo(path).Length / 1_000_000;

            ActiveModelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{filename}  ({size:N0} MB)",
                Tag = path,
            });
        }

        if (ActiveModelCombo.Items.Count == 0)
        {
            ActiveModelCombo.Items.Add(new ComboBoxItem { Content = "No models installed", Tag = string.Empty });
        }

        SelectByTag(ActiveModelCombo, _draft.SelectedWhisperModelPath ?? string.Empty);

        if (ActiveModelCombo.SelectedIndex < 0)
        {
            // No stored choice, or it has since been deleted: show what will actually
            // load rather than leaving the box blank.
            var bundled = _models.PathFor(ModelCatalog.BundledModelFilename);
            SelectByTag(ActiveModelCombo, bundled);
            if (ActiveModelCombo.SelectedIndex < 0) ActiveModelCombo.SelectedIndex = 0;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private static string TagOf(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    // =====================================================================
    // Model list
    // =====================================================================

    private void BuildModelList()
    {
        ModelList.Items.Clear();

        var selectedLanguage = TagOf(LanguageCombo);
        if (string.IsNullOrEmpty(selectedLanguage)) selectedLanguage = _draft.WhisperLanguage;

        var systemLanguage = LanguageCatalog.SystemLanguage();

        foreach (var model in ModelCatalog.Available)
        {
            var installed = _models.IsDownloaded(model.ResolvedFilename);

            // Language-specific models stay hidden unless relevant, so a Hebrew
            // fine-tune does not sit in every English user's list forever.
            if (!ModelCatalog.IsVisible(model, installed, selectedLanguage, systemLanguage)) continue;

            ModelList.Items.Add(BuildModelRow(model, installed));
        }
    }

    private Border BuildModelRow(DownloadableModel model, bool installed)
    {
        var name = new TextBlock
        {
            Text = model.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
        };

        var detail = new TextBlock
        {
            Text = $"{model.SizeLabel}  Â·  {model.Description}",
            FontSize = 11.5,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var progress = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Minimum = 0,
            Maximum = 1,
        };

        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var action = new Button { Width = 100, Height = 28, VerticalAlignment = VerticalAlignment.Top };

        var text = new StackPanel();
        text.Children.Add(name);
        text.Children.Add(detail);
        text.Children.Add(progress);
        text.Children.Add(status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(action, 1);
        grid.Children.Add(text);
        grid.Children.Add(action);

        ConfigureModelAction(model, installed, action, progress, status);

        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE2, 0xE2, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private void ConfigureModelAction(DownloadableModel model, bool installed,
        Button action, ProgressBar progress, TextBlock status)
    {
        if (installed)
        {
            action.Content = "Remove";
            action.Click += (_, _) => RemoveModel(model);
            return;
        }

        action.Content = "Download";
        action.Click += (_, _) => StartDownload(model, action, progress, status);
    }

    private void RemoveModel(DownloadableModel model)
    {
        var confirm = MessageBox.Show(
            $"Remove {model.Name}?\n\nYou can download it again later.",
            "Remove model", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        // If the model being removed is the active one, fall back to the bundled model
        // rather than leaving a path pointing at nothing.
        var path = _models.PathFor(model.ResolvedFilename);
        if (_draft.SelectedWhisperModelPath == path) _draft.SelectedWhisperModelPath = null;

        _models.Delete(model.ResolvedFilename);

        BuildActiveModelList();
        BuildModelList();
        UpdateDiskSpace();
    }

    private async void StartDownload(DownloadableModel model, Button action,
        ProgressBar progress, TextBlock status)
    {
        // A second click cancels rather than starting a duplicate download.
        if (_downloads.TryGetValue(model.ResolvedFilename, out var running))
        {
            running.Cancel();
            return;
        }

        try
        {
            DiskSpace.EnsureEnough(_models.ModelsDirectory);
        }
        catch (InsufficientDiskSpaceException ex)
        {
            MessageBox.Show(ex.Message, "Not enough disk space",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cts = new CancellationTokenSource();
        _downloads[model.ResolvedFilename] = cts;

        action.Content = "Cancel";
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = false;
        status.Visibility = Visibility.Visible;
        status.Text = "Startingâ€¦";

        var reporter = new Progress<DownloadProgress>(p =>
        {
            if (p.Fraction is { } fraction)
            {
                progress.IsIndeterminate = false;
                progress.Value = fraction;
                status.Text = $"{p.BytesReceived / 1_000_000:N0} MB of {p.TotalBytes / 1_000_000:N0} MB  ({fraction:P0})";
            }
            else
            {
                // Some servers omit Content-Length; showing a made-up percentage would
                // be worse than admitting the total is unknown.
                progress.IsIndeterminate = true;
                status.Text = $"{p.BytesReceived / 1_000_000:N0} MB downloaded";
            }
        });

        try
        {
            StatusText.Text = $"Downloading {model.Name}â€¦";
            await _models.DownloadAsync(model, reporter, cts.Token);

            StatusText.Text = $"{model.Name} downloaded.";
            BuildActiveModelList();
            BuildModelList();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{model.Name} download cancelled.";
            ResetModelRow(action, progress, status);
        }
        catch (Exception ex)
        {
            Log.Error($"download failed for {model.Name}", ex);
            StatusText.Text = string.Empty;
            MessageBox.Show($"Could not download {model.Name}.\n\n{ex.Message}",
                "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
            ResetModelRow(action, progress, status);
        }
        finally
        {
            _downloads.Remove(model.ResolvedFilename);
            cts.Dispose();
            UpdateDiskSpace();
        }
    }

    private static void ResetModelRow(Button action, ProgressBar progress, TextBlock status)
    {
        action.Content = "Download";
        progress.Visibility = Visibility.Collapsed;
        status.Visibility = Visibility.Collapsed;
    }

    private void UpdateDiskSpace()
    {
        var free = DiskSpace.AvailableBytes(_models.ModelsDirectory);

        DiskSpaceText.Text = free == long.MaxValue
            ? string.Empty
            : $"{free / 1_000_000_000.0:0.#} GB free. Downloads need at least "
              + $"{DiskSpace.RequiredFreeBytes / 1_000_000_000.0:0.#} GB available.";
    }

    // =====================================================================
    // Save
    // =====================================================================

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var trigger = TagOf(TriggerCombo);
        if (trigger.StartsWith("mouse:", StringComparison.Ordinal))
        {
            _draft.MouseButtonHotkey = trigger["mouse:".Length..];
        }
        else
        {
            _draft.MouseButtonHotkey = nameof(MouseButton.None);
            var key = trigger.StartsWith("key:", StringComparison.Ordinal)
                ? trigger["key:".Length..]
                : nameof(ModifierKey.RightControl);

            _draft.ModifierOnlyHotkey = key;
            _draft.LastModifierOnlyHotkey = key;
        }

        var microphone = TagOf(MicrophoneCombo);
        _draft.SelectedMicrophoneId = string.IsNullOrEmpty(microphone) ? null : microphone;

        var model = TagOf(ActiveModelCombo);
        _draft.SelectedWhisperModelPath = string.IsNullOrEmpty(model) ? null : model;

        var language = TagOf(LanguageCombo);
        if (!string.IsNullOrEmpty(language)) _draft.WhisperLanguage = language;

        // A language-specific model requires its language to be set explicitly; leaving
        // it on auto-detect measurably degrades its output.
        if (!string.IsNullOrEmpty(model)
            && ModelCatalog.PreferredLanguageFor(Path.GetFileName(model)) is { } forced)
        {
            _draft.WhisperLanguage = forced;
        }

        _draft.HoldToRecord = HoldToRecordCheck.IsChecked == true;
        _draft.DoublePressToTrigger = DoubleTapCheck.IsChecked == true;
        _draft.PlaySoundOnRecordStart = PlaySoundCheck.IsChecked == true;
        _draft.EscCancelWithoutConfirmation = EscNoConfirmCheck.IsChecked == true;

        _draft.AutoPasteTranscription = AutoPasteCheck.IsChecked == true;
        _draft.AutoCopyToClipboard = AutoCopyCheck.IsChecked == true;
        _draft.UseUnicodeTyping = UnicodeTypingCheck.IsChecked == true;
        _draft.AddSpaceAfterSentence = TrailingSpaceCheck.IsChecked == true;

        _draft.UseBeamSearch = BeamSearchCheck.IsChecked == true;
        _draft.InitialPrompt = InitialPromptBox.Text;
        _draft.UseAsianAutocorrect = AsianAutocorrectCheck.IsChecked == true;
        _draft.ShowTimestamps = TimestampsCheck.IsChecked == true;

        _draft.StartHiddenInTray = StartHiddenCheck.IsChecked == true;
        _draft.SaveTranscriptionHistory = SaveHistoryCheck.IsChecked == true;
        _draft.AutoDeleteRecordingsEnabled = AutoDeleteCheck.IsChecked == true;

        // Clamp rather than reject: a nonsensical retention value should not block
        // saving every other preference on the page.
        _draft.AutoDeleteRecordingsAfterDays = int.TryParse(RetentionDaysBox.Text, out var days)
            ? Math.Clamp(days, 1, 3650)
            : 30;

        _store.Replace(_draft);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Cancel anything still downloading, or the task keeps writing after the window
        // that owns its progress bar is gone.
        foreach (var download in _downloads.Values) download.Cancel();

        base.OnClosed(e);
    }

    // =====================================================================
    // Shortcuts
    // =====================================================================

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => OpenInShell(AppPaths.Root);

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (Log.Path is { } path && File.Exists(path)) OpenInShell(path);
    }

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not open {path}", ex);
        }
    }
}
