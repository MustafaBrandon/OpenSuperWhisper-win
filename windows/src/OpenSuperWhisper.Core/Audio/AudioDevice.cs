namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// An audio input endpoint.
/// </summary>
/// <param name="Id">
/// WASAPI endpoint ID. Stable across unplug and replug, which is why it — not the
/// friendly name — is what gets persisted as the user's chosen microphone.
/// </param>
/// <param name="Name">Friendly name, for display only.</param>
/// <param name="IsDefault">Whether this is the system default capture device.</param>
/// <param name="Transport">How the device is attached.</param>
public readonly record struct AudioDevice(
    string Id,
    string Name,
    bool IsDefault,
    AudioTransport Transport)
{
    /// <summary>
    /// Whether this device needs a moment to wake up before it delivers audio.
    /// </summary>
    /// <remarks>
    /// Bluetooth links negotiate on first use and can take a second or more to start
    /// producing samples. The mac app surfaces this as a distinct "connecting"
    /// indicator state rather than letting the user speak into a dead microphone —
    /// see docs/windows-port.md §4 module 03.
    /// </remarks>
    public bool RequiresWarmUp => Transport == AudioTransport.Bluetooth;

    public override string ToString() =>
        $"{Name}{(IsDefault ? " (default)" : "")} [{Transport}]";
}

public enum AudioTransport
{
    Unknown,
    Builtin,
    Usb,
    Bluetooth,
}
