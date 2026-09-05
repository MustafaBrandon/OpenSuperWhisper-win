using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Records the microphone to a 16 kHz mono 16-bit WAV file.
/// Port of the mac app's <c>AudioRecorder.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// Capture is written raw in the device's native format, then converted on stop. That
/// keeps the capture callback doing nothing but a buffer copy — resampling on the
/// audio thread risks dropouts — and reuses <see cref="AudioDecoder"/>, which has to
/// exist anyway for dropped files.
/// </para>
/// <para>
/// 16-bit output rather than float: half the disk and I/O, with no quality cost for
/// speech recognition, since whisper consumes 16 kHz mono regardless.
/// </para>
/// </remarks>
public sealed class AudioRecorder : IDisposable
{
    /// <summary>Recordings shorter than this are discarded as accidental taps.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// Extra audio captured after a stop request.
    /// </summary>
    /// <remarks>
    /// The hotkey is usually released while the last word is still being spoken, so
    /// stopping immediately clips it. Inherited from the mac app.
    /// </remarks>
    public static readonly TimeSpan StopTail = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private string? _rawPath;
    private DateTime _startedUtc;
    private bool _disposed;

    public bool IsRecording
    {
        get { lock (_gate) return _capture is not null; }
    }

    /// <summary>Starts capture from <paramref name="device"/>.</summary>
    /// <exception cref="InvalidOperationException">Already recording, or the device is unusable.</exception>
    public void Start(AudioDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_capture is not null)
                throw new InvalidOperationException("Already recording.");

            Directory.CreateDirectory(AppPaths.TempRecordings);
            _rawPath = Path.Combine(
                AppPaths.TempRecordings,
                $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}.raw.wav");

            using var enumerator = new MMDeviceEnumerator();
            var mmDevice = enumerator.GetDevice(device.Id);

            var capture = new WasapiCapture(mmDevice);
            var writer = new WaveFileWriter(_rawPath, capture.WaveFormat);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            _capture = capture;
            _writer = writer;
            _startedUtc = DateTime.UtcNow;

            capture.StartRecording();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Capture thread: copy and return. Anything slower risks dropped buffers.
        lock (_gate)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    /// <summary>
    /// Stops recording and returns the finished 16 kHz mono WAV, or null when the
    /// recording was too short to be intentional.
    /// </summary>
    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? rawPath;
        TimeSpan elapsed;

        lock (_gate)
        {
            if (_capture is null) return null;
            elapsed = DateTime.UtcNow - _startedUtc;
        }

        // Keep capturing briefly so the tail of the last word survives.
        try
        {
            await Task.Delay(StopTail, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation still needs an orderly teardown below.
        }

        lock (_gate)
        {
            rawPath = _rawPath;
            elapsed = DateTime.UtcNow - _startedUtc;
            TeardownLocked();
        }

        if (rawPath is null || !File.Exists(rawPath)) return null;

        if (elapsed < MinimumDuration)
        {
            TryDelete(rawPath);
            return null;
        }

        try
        {
            return await Task.Run(() => Convert(rawPath), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(rawPath);
        }
    }

    /// <summary>Stops and discards, with no tail wait.</summary>
    public void Cancel()
    {
        string? rawPath;

        lock (_gate)
        {
            rawPath = _rawPath;
            TeardownLocked();
        }

        if (rawPath is not null) TryDelete(rawPath);
    }

    private void TeardownLocked()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // The device may already be gone - unplugged mid-recording is normal.
            }

            _capture.Dispose();
            _capture = null;
        }

        _writer?.Dispose();
        _writer = null;
        _rawPath = null;
    }

    /// <summary>Converts the raw capture to whisper's format and writes the final WAV.</summary>
    private static string Convert(string rawPath)
    {
        var samples = AudioDecoder.DecodeToWhisperFormat(rawPath);

        var finalPath = Path.Combine(
            AppPaths.TempRecordings,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}.wav");

        WriteMono16(finalPath, samples, AudioDecoder.WhisperSampleRate);
        return finalPath;
    }

    /// <summary>Writes mono float samples as 16-bit PCM WAV.</summary>
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));

        var buffer = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            // Clamp before scaling: values slightly outside [-1, 1] would otherwise
            // wrap to the opposite sign and read as loud clicks.
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)(clamped * short.MaxValue);
            buffer[i * 2] = (byte)(value & 0xFF);
            buffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        writer.Write(buffer, 0, buffer.Length);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is swept later; failing here helps no one.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            var raw = _rawPath;
            TeardownLocked();
            if (raw is not null) TryDelete(raw);
        }
    }
}
