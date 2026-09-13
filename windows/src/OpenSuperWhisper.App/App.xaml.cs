using System.IO;
using System.Reflection;
using System.Windows;
using H.NotifyIcon;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Core.History;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Core.Settings;
using OpenSuperWhisper.Core.Text;

namespace OpenSuperWhisper.App;

public partial class App : Application
{
    /// <summary>
    /// Guards against a second instance.
    /// </summary>
    /// <remarks>
    /// Two instances would install two sets of input hooks, so one hotkey press would
    /// start two recordings competing for the microphone. Global scope so it holds
    /// across sessions on a shared machine.
    /// </remarks>
    private const string InstanceMutexName = @"Global\OpenSuperWhisper.SingleInstance";

    private Mutex? _instanceMutex;
    private SingleInstanceChannel? _channel;
    private TaskbarIcon? _tray;
    private IndicatorWindow? _indicator;
    private DictationController? _controller;
    private SettingsStore? _settings;
    private ModelManager? _models;
    private RecordingStore? _history;
    private AudioFileImporter? _importer;

    /// <summary>
    /// The model to load: the user's choice when it still exists, otherwise the bundled
    /// one.
    /// </summary>
    /// <remarks>
    /// The fallback matters — a model can be deleted between sessions, and starting with
    /// no working engine is a much worse outcome than silently reverting to tiny.en.
    /// </remarks>
    private string ResolveModelPath()
    {
        var chosen = _settings!.Current.SelectedWhisperModelPath;

        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen)) return chosen;

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            Log.Write($"selected model missing ({chosen}), falling back to the bundled model");
        }

        var bundled = _models!.PathFor(ModelCatalog.BundledModelFilename);
        return File.Exists(bundled) ? bundled : Meta("BundledModelPath");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            HandOffToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        Log.Start(AppPaths.Root);
        Log.Write($"starting â€” Windows build {Environment.OSVersion.Version.Build}");

        // Rev. 3 sets the floor at Windows 11. Say so rather than failing later in a
        // way that looks like a bug.
        if (Environment.OSVersion.Version.Build < 22000)
        {
            MessageBox.Show(
                "OpenSuperWhisper requires Windows 11 (build 22000) or later.",
                "Unsupported Windows version", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Anything unhandled on the UI thread would otherwise vanish with the process
        // and leave nothing to diagnose.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"OpenSuperWhisper hit an error.\n\n{args.Exception.Message}\n\nLog: {Log.Path}",
                "OpenSuperWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            StartDictation();

            // Diagnostic target, opened alongside the normal app so a dictation can be
            // aimed at something we can read back.
            if (e.Args.Contains("--paste-target"))
            {
                new PasteTargetWindow().Show();
                Log.Write("paste target window opened");
            }

            if (e.Args.Contains("--test-insert")) RunInsertionProbe();

            // Opening settings directly matters more than it looks: Windows 11 hides
            // new tray icons in the overflow flyout, so the menu that reaches this
            // dialog can be genuinely hard to find.
            if (e.Args.Contains("--settings")) ShowSettings();
            if (e.Args.Contains("--history")) ShowHistory();

            // Files given on the command line — an audio file opened from Explorer,
            // or dropped on the executable.
            QueueFiles(e.Args);

            // First run, or --onboarding to see it again. Last, so a launch that also
            // carries work gets the work started first.
            if (!_settings!.Current.HasCompletedOnboarding || e.Args.Contains("--onboarding"))
            {
                ShowOnboarding();
            }

            Log.Write("startup complete");
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show(
                $"OpenSuperWhisper could not start.\n\n{ex.Message}\n\nLog: {Log.Path}",
                "OpenSuperWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void StartDictation()
    {
        AppPaths.EnsureCreated();
        TempRecordingSweeper.Sweep();

        Interop.WhisperNative.SilenceNativeLogging();

        _settings = new SettingsStore();
        Log.Write($"settings: {_settings.Path}");

        // Install the bundled model if the models directory does not have it. Checked
        // every launch, not just the first: a user who clears that directory would
        // otherwise be left with an app that cannot transcribe at all.
        _models = new ModelManager();
        _models.EnsureBundledModelPresent(Meta("BundledModelPath"));

        var modelPath = ResolveModelPath();
        var vadPath = Meta("VadModelPath");
        Log.Write($"model: {modelPath} (exists: {File.Exists(modelPath)})");
        Log.Write($"vad:   {vadPath} (exists: {File.Exists(vadPath)})");

        _indicator = new IndicatorWindow();

        // Realise the window now so the first hotkey press does not pay for window
        // creation on top of everything else it is already doing.
        _indicator.Show();
        _indicator.Hide();
        Log.Write("indicator window created");

        _history = new RecordingStore();
        ApplyRetention();

        _controller = new DictationController(Dispatcher, _indicator, modelPath, vadPath, _history);
        Log.Write("engine loaded");

        var microphone = _controller.Microphones.ActiveDevice;
        Log.Write($"microphone: {microphone?.ToString() ?? "NONE"}");

        // Bind the trigger explicitly once, then let ApplySettings handle later edits —
        // it only rebinds when the binding actually changed, to avoid reinstalling
        // hooks on every unrelated preference change.
        _controller.ApplyTriggerBinding(_settings.Current);
        _controller.ApplySettings(_settings.Current);

        // A settings edit takes effect immediately rather than at next launch.
        _settings.Changed += (_, updated) => Dispatcher.Invoke(() => _controller.ApplySettings(updated));

        _controller.Start();
        Log.Write($"hooks installed, trigger = {_controller.Triggers.Mode} "
                + $"(suppressed so it cannot reach other apps)");

        _importer = new AudioFileImporter(_controller.TranscribeFileAsync, _history, _settings);

        // Accept files opened while the app is already running. Started after the
        // pipeline is up, so a forwarded file never arrives before there is an engine
        // to give it to.
        _channel = new SingleInstanceChannel();
        _channel.MessageReceived += (_, args) => Dispatcher.Invoke(() => HandleForwardedLaunch(args));
        _channel.Listen();

        BuildTray();
        Log.Write("tray icon created");
    }

    /// <summary>
    /// Forwards a second launch's arguments to the instance already running.
    /// </summary>
    /// <remarks>
    /// Opening an audio file from Explorer starts a new process; this is what turns
    /// that into "transcribe it in the app that is already up". A launch with no files
    /// shows history, because the closest thing this app has to a main window is more
    /// useful than a dialog saying it is already running.
    /// </remarks>
    private static void HandOffToRunningInstance(string[] args)
    {
        var files = AudioFileImporter.AudioFilesIn(args);

        // "--history" is a request the running instance already understands, so a bare
        // relaunch reuses it rather than inventing a second verb.
        var message = files.Count > 0 ? files.ToArray() : ["--history"];

        if (SingleInstanceChannel.Send(message)) return;

        // The other instance is up — it holds the mutex — but not listening: it may be
        // mid-startup or mid-shutdown. Saying so beats appearing to do nothing.
        MessageBox.Show(
            "OpenSuperWhisper is already running, but is not responding yet. "
            + "Look for it in the notification area.",
            "OpenSuperWhisper", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Acts on a command line forwarded from a second launch, as if it were our own.
    /// </summary>
    private void HandleForwardedLaunch(string[] args)
    {
        Log.Write($"forwarded launch: {string.Join(' ', args)}");

        if (args.Contains("--settings")) ShowSettings();
        if (args.Contains("--history")) ShowHistory();

        QueueFiles(args);
    }

    /// <summary>Transcribes any audio files among the given arguments.</summary>
    private void QueueFiles(string[] args)
    {
        var files = AudioFileImporter.AudioFilesIn(args);
        if (files.Count == 0 || _importer is null) return;

        Log.Write($"queued {files.Count} file(s) from the command line");

        // Deliberately not opened in a window: per the plan's module 09, files arrive
        // as work to do, not as documents to look at. The tray notification is the
        // feedback, and the transcript lands in history.
        _ = ImportQueuedAsync(files);
    }

    private async Task ImportQueuedAsync(IReadOnlyList<string> files)
    {
        try
        {
            ReportImport(await _importer!.ImportAsync(files));
        }
        catch (Exception ex)
        {
            // Per-file failures are already reported in the results; reaching here means
            // the queue itself broke, which must not take the app down with it.
            Log.Error("queued import failed", ex);
        }
    }

    private void ReportImport(IReadOnlyList<ImportResult> results)
    {
        var failed = results.Count(r => r.Error is not null);
        var transcribed = results.Count(r => r.Succeeded);

        var message = failed > 0
            ? $"{transcribed} transcribed, {failed} failed. See the log for details."
            : $"{transcribed} file(s) transcribed. Open History to read them.";

        Log.Write($"import finished: {message}");

        try
        {
            _tray?.ShowNotification("OpenSuperWhisper", message);
        }
        catch (Exception ex)
        {
            // Notifications are suppressible by policy and by focus-assist. Losing one
            // is not worth an error dialog.
            Log.Error("could not show the import notification", ex);
        }
    }

    /// <summary>
    /// Runs an insertion under the same conditions as a real dictation.
    /// </summary>
    /// <remarks>
    /// The CLI's `osw insert` proves the mechanism from a separate process, which is
    /// not the same thing: in the app the call runs on the WPF dispatcher thread while
    /// the indicator is visible and this process owns the input hooks. Any of those
    /// could matter, so this reproduces them exactly, without needing speech.
    /// </remarks>
    private void RunInsertionProbe()
    {
        Log.Write("insertion probe: showing indicator, inserting in 6s");

        _ = Dispatcher.InvokeAsync(async () =>
        {
            // Match the dictation flow: indicator up before the insertion happens.
            _indicator!.Render(Core.Indicator.IndicatorState.Recording, TimeSpan.FromSeconds(3), false);
            _indicator.Show();
            _indicator.MoveToAnchor(Core.Indicator.CaretLocator.MouseAnchor());

            await Task.Delay(TimeSpan.FromSeconds(6));

            Log.Write("insertion probe: inserting now");
            var outcome = TextInjector.Insert("OSW-INAPP-PROBE", new InsertionOptions());
            Log.Write($"insertion probe: {outcome}");

            _indicator.Hide();
        });
    }



    private void BuildTray()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var status = new System.Windows.Controls.MenuItem
        {
            Header = "Hold Right Ctrl to dictate",
            IsEnabled = false,
        };
        menu.Items.Add(status);
        menu.Items.Add(new System.Windows.Controls.Separator());

        menu.Items.Add(BuildMicrophoneMenu());
        menu.Items.Add(BuildLanguageMenu());

        var history = new System.Windows.Controls.MenuItem { Header = "History…" };
        history.Click += (_, _) => ShowHistory();
        menu.Items.Add(history);


        var settings = new System.Windows.Controls.MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quit = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Shutdown();
        menu.Items.Add(quit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "OpenSuperWhisper â€” Hold Right Ctrl to dictate",
            ContextMenu = menu,
            Icon = System.Drawing.SystemIcons.Application,
        };

        _tray.ForceCreate();
    }

    /// <summary>Removes recordings past the retention window, when retention is on.</summary>
    /// <remarks>
    /// Runs at startup rather than on a timer: a dictation tool is often left running
    /// for days, but it is also restarted often enough for this to be timely, and a
    /// background deleter is a surprising thing to have running against your own data.
    /// </remarks>
    private void ApplyRetention()
    {
        var settings = _settings!.Current;
        if (!settings.AutoDeleteRecordingsEnabled) return;

        var cutoff = RetentionPolicy.CutoffDate(settings.AutoDeleteRecordingsAfterDays, DateTimeOffset.Now);
        if (cutoff is null) return;

        var removed = _history!.DeleteOlderThan(cutoff.Value);
        if (removed > 0) Log.Write($"retention removed {removed} recording(s) older than {cutoff:d}");
    }    private HistoryWindow? _historyWindow;

    /// <summary>Opens history, or focuses it if already open.</summary>
    private void ShowHistory()
    {
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }

        _historyWindow = new HistoryWindow(_history!, _settings!, _importer!);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }


    private OnboardingWindow? _onboardingWindow;

    /// <summary>
    /// First-run setup.
    /// </summary>
    /// <remarks>
    /// Shown even when the app is set to start hidden: someone who has just installed it
    /// needs to be told where it went, and that is exactly what the last page says.
    /// </remarks>
    private void ShowOnboarding()
    {
        if (_onboardingWindow is { IsLoaded: true })
        {
            _onboardingWindow.Activate();
            return;
        }

        _onboardingWindow = new OnboardingWindow(_settings!, _controller!.Microphones, _controller);
        _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
        _onboardingWindow.Show();
        _onboardingWindow.Activate();

        Log.Write("onboarding shown");
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>Opens settings, or focuses the window if it is already open.</summary>
    /// <remarks>
    /// Two settings windows would each hold their own draft, and whichever saved last
    /// would silently discard the other's edits.
    /// </remarks>
    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        // Refresh devices first: the list is cached, and an unplugged microphone still
        // showing in a settings dialog is worse than a moment's delay.
        _ = Task.Run(() => _controller!.Microphones.Refresh());

        _settingsWindow = new SettingsWindow(_settings!, _models!, _controller!.Microphones, _history);
        _settingsWindow.OpenHistoryRequested += (_, _) => ShowHistory();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private System.Windows.Controls.MenuItem BuildMicrophoneMenu()
    {
        var root = new System.Windows.Controls.MenuItem { Header = "Microphone" };

        // Rebuilt on open rather than cached: devices appear and disappear, and a stale
        // list that still offers an unplugged headset is worse than a brief pause.
        root.SubmenuOpened += (_, _) =>
        {
            root.Items.Clear();

            var microphones = _controller?.Microphones;
            if (microphones is null) return;

            var active = microphones.ActiveDevice;

            foreach (var device in microphones.Devices)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = device.Name,
                    IsCheckable = true,
                    IsChecked = device.Id == active?.Id,
                };

                var id = device.Id;
                item.Click += (_, _) => microphones.SetPreferredDevice(id);
                root.Items.Add(item);
            }

            if (root.Items.Count == 0)
            {
                root.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "No microphones found",
                    IsEnabled = false,
                });
            }
        };

        return root;
    }

    /// <summary>
    /// Transcription language, in the tray so it can be changed mid-task.
    /// </summary>
    /// <remarks>
    /// Carried over from the mac app's status menu, and it earns its place: switching
    /// language is the one setting people change without changing anything else, and
    /// routing that through a settings dialog is three clicks too many.
    /// <para>
    /// Unlike the settings dialog this applies immediately — there is no draft to
    /// cancel, and a menu that needed a Save button would be a strange menu.
    /// </para>
    /// </remarks>
    private System.Windows.Controls.MenuItem BuildLanguageMenu()
    {
        var root = new System.Windows.Controls.MenuItem { Header = "Language" };

        root.SubmenuOpened += (_, _) =>
        {
            root.Items.Clear();

            var active = _settings?.Current.WhisperLanguage ?? LanguageCatalog.AutoDetect;

            foreach (var (code, name) in LanguageCatalog.ForDisplay())
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = name,
                    IsCheckable = true,
                    IsChecked = code == active,
                };

                var chosen = code;
                item.Click += (_, _) => _settings?.Update(s => s.WhisperLanguage = chosen);

                root.Items.Add(item);
            }
        };

        return root;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _channel?.Dispose();
        _controller?.Dispose();
        _tray?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }

    private static string Meta(string key) =>
        typeof(App).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' missing.");
}
