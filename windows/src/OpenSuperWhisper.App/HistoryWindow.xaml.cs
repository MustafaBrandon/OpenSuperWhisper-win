using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Settings;
using OpenSuperWhisper.Core.Text;

namespace OpenSuperWhisper.App;

/// <summary>One row in the history list.</summary>
public sealed record HistoryRow(Guid Id, string Transcript, string Detail, bool HasAudio);

/// <summary>
/// Recording history: review, copy, play back, and delete.
/// </summary>
/// <remarks>
/// Also the drop target for audio files, since "things I have transcribed" and "things
/// I want transcribed" belong in the same place.
/// </remarks>
public partial class HistoryWindow : Window
{
    private readonly RecordingStore _store;
    private readonly SettingsStore _settings;
    private readonly Func<string, Task<string>> _transcribeFile;

    private WaveOutEvent? _player;
    private AudioFileReader? _playerReader;

    public HistoryWindow(RecordingStore store, SettingsStore settings,
        Func<string, Task<string>> transcribeFile)
    {
        InitializeComponent();

        _store = store;
        _settings = settings;
        _transcribeFile = transcribeFile;

        Refresh();
    }

    private void Refresh()
    {
        var recordings = _store.All();

        HistoryList.ItemsSource = recordings.Select(r => new HistoryRow(
            r.Id,
            string.IsNullOrWhiteSpace(r.Transcription) ? "(no text)" : r.Transcription,
            $"{r.Timestamp.LocalDateTime:g}  ·  {FormatDuration(r.DurationSeconds)}"
                + (r.Status == RecordingStatus.Completed ? string.Empty : $"  ·  {r.Status}"),
            File.Exists(r.AudioPath))).ToList();

        var count = recordings.Count;
        CountText.Text = count == 1 ? "1 recording" : $"{count} recordings";

        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Surfaced here as well as in Settings: someone looking at an empty history
        // deserves to know it is empty by choice rather than by failure.
        DisabledBanner.Visibility = _settings.Current.SaveTranscriptionHistory
            ? Visibility.Collapsed
            : Visibility.Visible;

        var days = _settings.Current.AutoDeleteRecordingsAfterDays;
        DeleteOlderButton.Content = $"Delete older than {days} days";
        DeleteOlderButton.IsEnabled = count > 0;
        ClearAllButton.IsEnabled = count > 0;
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
            : $"{span.Seconds}s";
    }

    private static Guid? IdOf(object sender) =>
        (sender as FrameworkElement)?.Tag as Guid?;

    // =====================================================================
    // Per-row actions
    // =====================================================================

    private void OnCopyOne(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id) return;
        if (_store.Find(id) is not { } recording) return;

        ClipboardService.SetText(recording.Transcription);
    }

    private void OnPlayOne(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id) return;
        if (_store.Find(id) is not { } recording) return;
        if (!File.Exists(recording.AudioPath)) return;

        StopPlayback();

        try
        {
            _playerReader = new AudioFileReader(recording.AudioPath);
            _player = new WaveOutEvent();
            _player.Init(_playerReader);

            // Release the file handle when playback ends, or the audio cannot be
            // deleted afterwards.
            _player.PlaybackStopped += (_, _) => Dispatcher.Invoke(StopPlayback);
            _player.Play();
        }
        catch (Exception ex)
        {
            Log.Error("playback failed", ex);
            StopPlayback();
        }
    }

    private void StopPlayback()
    {
        _player?.Dispose();
        _player = null;

        _playerReader?.Dispose();
        _playerReader = null;
    }

    private void OnDeleteOne(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id) return;

        // Playback holds the file open, and deleting the audio underneath it fails.
        StopPlayback();

        _store.Delete(id);
        Refresh();
    }

    // =====================================================================
    // Bulk actions
    // =====================================================================

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var count = _store.Count();

        var confirm = MessageBox.Show(
            $"Delete all {count} recording(s) and their audio?\n\nThis cannot be undone.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        StopPlayback();
        var removed = _store.DeleteAll();
        Log.Write($"history cleared: {removed} recording(s) removed");

        Refresh();
    }

    private void OnDeleteOlder(object sender, RoutedEventArgs e)
    {
        var days = _settings.Current.AutoDeleteRecordingsAfterDays;
        var cutoff = RetentionPolicy.CutoffDate(days, DateTimeOffset.Now);
        if (cutoff is null) return;

        var doomed = _store.All(int.MaxValue).Count(r => r.Timestamp < cutoff.Value);
        if (doomed == 0)
        {
            MessageBox.Show($"Nothing is older than {days} days.", "Nothing to delete",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete {doomed} recording(s) older than {days} days?\n\nThis cannot be undone.",
            "Delete old recordings", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        StopPlayback();
        _store.DeleteOlderThan(cutoff.Value);
        Refresh();
    }

    // =====================================================================
    // Drag and drop
    // =====================================================================

    private static readonly string[] AudioExtensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".mp4"];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedAudioFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static List<string> DroppedAudioFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return [];

        return [.. paths.Where(p =>
            AudioExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))];
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        var files = DroppedAudioFiles(e);
        if (files.Count == 0) return;

        // Sequential rather than parallel: one whisper context cannot decode two files
        // at once, and queueing them keeps the order the user dropped them in.
        foreach (var file in files)
        {
            CountText.Text = $"Transcribing {Path.GetFileName(file)}…";

            try
            {
                var text = await _transcribeFile(file);

                if (string.IsNullOrWhiteSpace(text))
                {
                    Log.Write($"dropped file produced no speech: {file}");
                    continue;
                }

                // Imported files are recorded with their source path, and their audio is
                // NOT moved into the library - the original stays where the user left it.
                if (_settings.Current.SaveTranscriptionHistory)
                {
                    _store.Add(new Recording
                    {
                        Id = Guid.NewGuid(),
                        Timestamp = DateTimeOffset.Now,
                        FileName = Path.GetFileName(file),
                        Transcription = text,
                        DurationSeconds = 0,
                        Status = RecordingStatus.Completed,
                        Progress = 1.0,
                        SourceFilePath = file,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"could not transcribe {file}", ex);
                MessageBox.Show($"Could not transcribe {Path.GetFileName(file)}.\n\n{ex.Message}",
                    "Transcription failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }
}
