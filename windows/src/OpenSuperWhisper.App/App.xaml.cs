using System.Reflection;
using System.Windows;
using H.NotifyIcon;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Input;
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
    private TaskbarIcon? _tray;
    private IndicatorWindow? _indicator;
    private DictationController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "OpenSuperWhisper is already running. Look for it in the notification area.",
                "OpenSuperWhisper", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (!Environment.OSVersion.Version.Build.Equals(0) && Environment.OSVersion.Version.Build < 22000)
        {
            // Rev. 3 sets the floor at Windows 11. Say so rather than failing later in
            // a way that looks like a bug.
            MessageBox.Show(
                "OpenSuperWhisper requires Windows 11 (build 22000) or later.",
                "Unsupported Windows version", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        try
        {
            StartDictation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"OpenSuperWhisper could not start.\n\n{ex.Message}",
                "OpenSuperWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void StartDictation()
    {
        AppPaths.EnsureCreated();
        TempRecordingSweeper.Sweep();

        Interop.WhisperNative.SilenceNativeLogging();

        var modelPath = Meta("BundledModelPath");
        var vadPath = Meta("VadModelPath");

        _indicator = new IndicatorWindow();

        // Realise the window now so the first hotkey press does not pay for window
        // creation on top of everything else it is already doing.
        _indicator.Show();
        _indicator.Hide();

        _controller = new DictationController(Dispatcher, _indicator, modelPath, vadPath)
        {
            Insertion = new InsertionOptions(Paste: true, CopyToClipboard: false),
        };

        _controller.Triggers.HoldToRecord = true;
        _controller.Triggers.UseModifierKey(ModifierKey.RightAlt);
        _controller.Start();

        BuildTray();
    }

    private void BuildTray()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var status = new System.Windows.Controls.MenuItem
        {
            Header = "Hold Right Alt to dictate",
            IsEnabled = false,
        };
        menu.Items.Add(status);
        menu.Items.Add(new System.Windows.Controls.Separator());

        menu.Items.Add(BuildMicrophoneMenu());
        menu.Items.Add(new System.Windows.Controls.Separator());

        var quit = new System.Windows.Controls.MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Shutdown();
        menu.Items.Add(quit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "OpenSuperWhisper — hold Right Alt to dictate",
            ContextMenu = menu,
            Icon = System.Drawing.SystemIcons.Application,
        };

        _tray.ForceCreate();
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

    protected override void OnExit(ExitEventArgs e)
    {
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
