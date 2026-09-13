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
    private readonly AudioFileImporter _importer;

    private WaveOutEvent? _player;
    private AudioFileReader? _playerReader;

    public HistoryWindow(RecordingStore store, SettingsStore settings, AudioFileImporter importer)
    {
        InitializeComponent();

        _store = store;
        _settings = settings;
        _importer = importer;

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
            // Imported files were never copied into the library, so their audio is
            // wherever the user left it — playable and re-readable from there.
            r.PlayablePath is not null)).ToList();

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
        if (recording.PlayablePath is not { } audio) return;

        StopPlayback();

        try
        {
            _playerReader = new AudioFileReader(audio);
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

    /// <summary>
    /// Transcribes a stored recording again with today's model and language.
    /// </summary>
    /// <remarks>
    /// The reason this exists: the bundled Tiny model is what a new install dictates
    /// with, and after downloading a real one there is no other way to recover the
    /// transcripts it got wrong — the audio is still there, so the work is not lost,
    /// it is just one button away.
    /// <para>
    /// An empty result does not overwrite. Whisper returning nothing means "I heard no
    /// speech", and trading a transcript the user already has for that is destroying
    /// data on the strength of a worse answer.
    /// </para>
    /// </remarks>
    private async void OnRetranscribeOne(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id) return;
        if (_store.Find(id) is not { } recording) return;

        if (recording.PlayablePath is not { } audio)
        {
            MessageBox.Show(
                "The audio for this recording is no longer on disk, so it cannot be transcribed again.",
                "Audio missing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Playback holds the file open, and the decoder needs to read it.
        StopPlayback();

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;

        var previousCount = CountText.Text;
        CountText.Text = $"Transcribing {Path.GetFileName(audio)}…";

        try
        {
            var text = await _importer.TranscribeAsync(audio);

            if (string.IsNullOrWhiteSpace(text))
            {
                Log.Write($"re-transcription of {recording.Id} produced no speech; keeping the existing transcript");
                CountText.Text = previousCount;

                MessageBox.Show(
                    "No speech was found this time, so the existing transcript has been kept.",
                    "Nothing to change", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _store.UpdateTranscription(recording.Id, text);
            Log.Write($"re-transcribed {recording.Id}");
        }
        catch (Exception ex)
        {
            Log.Error($"could not re-transcribe {recording.Id}", ex);
            MessageBox.Show($"Could not transcribe this recording again.\n\n{ex.Message}",
                "Transcription failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Rebuilds the row, which replaces the button this handler disabled.
            Refresh();
        }
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedAudioFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static List<string> DroppedAudioFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return [];

        return AudioFileImporter.AudioFilesIn(paths);
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        var files = DroppedAudioFiles(e);
        if (files.Count == 0) return;

        // The importer queues, so a drop during a still-running Explorer open waits its
        // turn rather than contending for the one whisper context.
        void OnStarted(object? _, string name) => CountText.Text = $"Transcribing {name}…";

        _importer.Started += OnStarted;

        try
        {
            var results = await _importer.ImportAsync(files);

            foreach (var failure in results.Where(r => r.Error is not null))
            {
                MessageBox.Show(
                    $"Could not transcribe {Path.GetFileName(failure.Path)}.\n\n{failure.Error}",
                    "Transcription failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _importer.Started -= OnStarted;
            Refresh();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }
}
