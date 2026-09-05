using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Enumerates audio inputs, remembers the user's choice, and reacts to hotplug.
/// Port of the mac app's <c>MicrophoneService.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ActiveDevice"/> is a cached read and never touches WASAPI. The mac app
/// learned this the hard way: querying CoreAudio on the UI thread cost 20–35 ms of
/// HAL round trips right when the recording indicator was trying to animate. The same
/// applies here — enumeration happens on hotplug notifications, not on access.
/// </para>
/// <para>
/// This class is thread-safe. Hotplug notifications arrive on an arbitrary WASAPI
/// thread while the UI reads <see cref="ActiveDevice"/>.
/// </para>
/// </remarks>
public sealed class MicrophoneService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient? _notificationClient;
    private readonly Lock _gate = new();

    private List<AudioDevice> _devices = [];
    private AudioDevice? _active;
    private string? _preferredId;
    private bool _disposed;

    /// <summary>Raised when devices appear, disappear, or the default changes.</summary>
    public event EventHandler? DevicesChanged;

    public MicrophoneService()
    {
        _enumerator = new MMDeviceEnumerator();

        Refresh();

        try
        {
            _notificationClient = new NotificationClient(OnDeviceChangeNotification);
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (Exception)
        {
            // Without notifications the device list simply goes stale until the next
            // explicit Refresh. Degraded, but not worth failing construction over.
            _notificationClient = null;
        }
    }

    /// <summary>Known input devices. Cached; safe to call from the UI thread.</summary>
    public IReadOnlyList<AudioDevice> Devices
    {
        get { lock (_gate) return _devices; }
    }

    /// <summary>
    /// The device recording will use: the user's preference when present, otherwise
    /// the system default. Null when the machine has no input at all.
    /// </summary>
    public AudioDevice? ActiveDevice
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>
    /// Sets the preferred device by WASAPI endpoint ID.
    /// </summary>
    /// <remarks>
    /// The preference survives the device disappearing: if the user unplugs their
    /// interface, recording falls back to the default, and the moment it returns the
    /// original choice is honoured again. Storing the ID rather than an index or a
    /// name is what makes that work.
    /// </remarks>
    public void SetPreferredDevice(string? endpointId)
    {
        lock (_gate)
        {
            _preferredId = endpointId;
            ResolveActiveLocked();
        }
    }

    public string? PreferredDeviceId
    {
        get { lock (_gate) return _preferredId; }
    }

    /// <summary>Re-enumerates. Blocking; call off the UI thread.</summary>
    public void Refresh()
    {
        var discovered = new List<AudioDevice>();
        string? defaultId = null;

        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                using var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = def.ID;
            }
        }
        catch (Exception)
        {
            // No default endpoint; every device is simply non-default.
        }

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    discovered.Add(new AudioDevice(
                        device.ID,
                        SafeFriendlyName(device),
                        device.ID == defaultId,
                        DetectTransport(device)));
                }
            }
        }
        catch (Exception)
        {
            // Leave the list empty rather than throwing: "no microphone" is a state the
            // app already handles, and a broken audio stack should not crash it.
        }

        lock (_gate)
        {
            _devices = discovered;
            ResolveActiveLocked();
        }
    }

    private void ResolveActiveLocked()
    {
        if (_preferredId is not null)
        {
            foreach (var device in _devices)
            {
                if (device.Id == _preferredId)
                {
                    _active = device;
                    return;
                }
            }
        }

        // Preference missing or unplugged: fall back to the default, then to anything.
        foreach (var device in _devices)
        {
            if (device.IsDefault)
            {
                _active = device;
                return;
            }
        }

        _active = _devices.Count > 0 ? _devices[0] : null;
    }

    private void OnDeviceChangeNotification()
    {
        if (_disposed) return;

        // Notifications arrive on a WASAPI thread and re-entering the enumerator from
        // it can deadlock, so the refresh is punted to the thread pool.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed) return;

            try
            {
                Refresh();
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A notification storm during sleep/resume must not take the app down.
            }
        });
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (Exception)
        {
            return "Unknown device";
        }
    }

    /// <summary>
    /// <c>PKEY_Device_EnumeratorName</c> — the bus a device sits on ("HDAUDIO",
    /// "BTHENUM", "USB").
    /// </summary>
    /// <remarks>
    /// Declared by hand because NAudio does not expose it. Do NOT substitute NAudio's
    /// <c>PKEY_Device_InstanceId</c>: that resolves to the MMDEVAPI software endpoint
    /// (<c>SWD\MMDEVAPI\{0.0.1...}</c>), which is identical in shape for every device
    /// and carries no bus information at all — every microphone classifies as Unknown
    /// and Bluetooth never gets its warm-up state.
    /// </remarks>
    private static readonly PropertyKey PkeyDeviceEnumeratorName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>
    /// Classifies how a device is attached.
    /// </summary>
    /// <remarks>
    /// The friendly name is only a fallback: a Bluetooth headset called "USB Audio"
    /// would otherwise be misclassified and skip the warm-up state, leaving the user
    /// talking into a microphone that is not yet live.
    /// </remarks>
    internal static AudioTransport DetectTransport(MMDevice device)
    {
        try
        {
            var busName = device.Properties.Contains(PkeyDeviceEnumeratorName)
                ? device.Properties[PkeyDeviceEnumeratorName].Value as string
                : null;

            if (!string.IsNullOrEmpty(busName))
            {
                return ClassifyInstanceId(busName);
            }
        }
        catch (Exception)
        {
            // Property store unavailable; fall through to the name heuristic.
        }

        return ClassifyName(SafeFriendlyName(device));
    }

    internal static AudioTransport ClassifyInstanceId(string instanceId) =>
        instanceId.ToUpperInvariant() switch
        {
            var s when s.Contains("BTH") || s.Contains("BLUETOOTH") => AudioTransport.Bluetooth,
            var s when s.Contains("USB") => AudioTransport.Usb,
            var s when s.Contains("HDAUDIO") || s.Contains("ACPI") || s.Contains("INTELAUDIO")
                => AudioTransport.Builtin,
            _ => AudioTransport.Unknown,
        };

    internal static AudioTransport ClassifyName(string name) =>
        name.ToUpperInvariant() switch
        {
            var s when s.Contains("BLUETOOTH") || s.Contains("HANDS-FREE") || s.Contains("HEADSET")
                => AudioTransport.Bluetooth,
            var s when s.Contains("USB") => AudioTransport.Usb,
            _ => AudioTransport.Unknown,
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_notificationClient is not null)
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down.
        }

        _enumerator.Dispose();
    }

    /// <summary>Bridges WASAPI endpoint notifications to a single callback.</summary>
    private sealed class NotificationClient(Action onChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => onChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => onChanged();
        public void OnDeviceRemoved(string deviceId) => onChanged();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture) onChanged();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Volume and format changes are noise for our purposes.
        }
    }
}
