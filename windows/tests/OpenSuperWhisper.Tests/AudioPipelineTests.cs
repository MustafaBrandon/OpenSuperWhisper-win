using OpenSuperWhisper.Core.Audio;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Channel mixing. The rule that keeps one live mic on a multi-channel interface at
/// full amplitude instead of a fraction of it.
/// </summary>
public class AudioMixingTests
{
    [Fact]
    public void MonoInput_PassesThrough()
    {
        float[] input = [0.1f, -0.2f, 0.3f];

        Assert.Equal(input, AudioMixing.MixToMono(input, 1));
    }

    [Fact]
    public void AllChannelsActive_AveragesEvenly()
    {
        // Two live channels: 0.4 and 0.2 average to 0.3.
        float[] interleaved = [0.4f, 0.2f, 0.4f, 0.2f];

        var mono = AudioMixing.MixToMono(interleaved, 2);

        Assert.Equal(2, mono.Length);
        Assert.All(mono, s => Assert.Equal(0.3f, s, 5));
    }

    [Fact]
    public void SilentChannelsAreExcluded_SoSignalKeepsFullAmplitude()
    {
        // The case this whole rule exists for: a 6-channel interface with a mic in
        // channel 0. A naive average would divide by 6 and return 0.1 - quiet enough
        // to measurably hurt recognition.
        const int channels = 6;
        const int frames = 100;
        var interleaved = new float[channels * frames];

        for (var f = 0; f < frames; f++)
        {
            interleaved[f * channels] = 0.6f;
        }

        var mono = AudioMixing.MixToMono(interleaved, channels);

        Assert.All(mono, s => Assert.Equal(0.6f, s, 5));
    }

    [Fact]
    public void AllSilent_FallsBackToEveryChannel()
    {
        // No channel clears the threshold. Mixing all of them yields silence, which is
        // correct; returning nothing or dividing by zero would not be.
        var interleaved = new float[4 * 50];

        var mono = AudioMixing.MixToMono(interleaved, 4);

        Assert.Equal(50, mono.Length);
        Assert.All(mono, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AudioMixing.MixToMono([], 2));
    }

    [Fact]
    public void PartiallyActiveChannels_DivideByActiveCountOnly()
    {
        // Channels 0 and 2 live, 1 and 3 silent: (0.8 + 0.4) / 2 = 0.6.
        const int channels = 4;
        const int frames = 50;
        var interleaved = new float[channels * frames];

        for (var f = 0; f < frames; f++)
        {
            interleaved[f * channels + 0] = 0.8f;
            interleaved[f * channels + 2] = 0.4f;
        }

        var mono = AudioMixing.MixToMono(interleaved, channels);

        Assert.All(mono, s => Assert.Equal(0.6f, s, 5));
    }
}

/// <summary>
/// Temp sweep. Runs against a fixed clock so it does not depend on wall time.
/// </summary>
public class TempSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-sweep-tests", Guid.NewGuid().ToString("N"));

    public TempSweepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void RemovesOnlyFilesOlderThanMaxAge()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        var old = WriteFile("old.wav", now - TimeSpan.FromHours(25));
        var fresh = WriteFile("fresh.wav", now - TimeSpan.FromHours(23));

        var removed = TempRecordingSweeper.Sweep(_dir, TimeSpan.FromHours(24), now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void FileExactlyAtCutoff_IsKept()
    {
        // Boundary: "older than" is strict, so a file exactly at the cutoff survives.
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var borderline = WriteFile("edge.wav", now - TimeSpan.FromHours(24));

        var removed = TempRecordingSweeper.Sweep(_dir, TimeSpan.FromHours(24), now);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(borderline));
    }

    [Fact]
    public void MissingDirectory_DoesNotThrow()
    {
        // The normal first-run state.
        var missing = Path.Combine(_dir, "does-not-exist");

        Assert.Equal(0, TempRecordingSweeper.Sweep(missing, TimeSpan.FromHours(24), DateTime.UtcNow));
    }

    [Fact]
    public void EmptyDirectory_RemovesNothing()
    {
        Assert.Equal(0, TempRecordingSweeper.Sweep(_dir, TimeSpan.FromHours(24), DateTime.UtcNow));
    }
}

/// <summary>
/// Transport classification, which decides whether a device gets the warm-up
/// treatment before recording is considered live.
/// </summary>
public class TransportClassificationTests
{
    // These are PKEY_Device_EnumeratorName values, i.e. the bus name. Confirmed
    // against a real machine: a Realtek built-in array reports exactly "HDAUDIO".
    [Theory]
    [InlineData("HDAUDIO", AudioTransport.Builtin)]
    [InlineData("BTHENUM", AudioTransport.Bluetooth)]
    [InlineData("BTHHFENUM", AudioTransport.Bluetooth)]
    [InlineData("USB", AudioTransport.Usb)]
    [InlineData("INTELAUDIO", AudioTransport.Builtin)]
    [InlineData("SWD", AudioTransport.Unknown)]
    // Full instance-ID strings still classify, since matching is by substring.
    [InlineData(@"USB\VID_046D&PID_0A38&MI_00\7&2a5a1d4e&0&0000", AudioTransport.Usb)]
    [InlineData(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&0000", AudioTransport.Bluetooth)]
    public void EnumeratorName_ClassifiesTransport(string busName, AudioTransport expected)
    {
        Assert.Equal(expected, MicrophoneService.ClassifyInstanceId(busName));
    }

    [Fact]
    public void MmdevapiSoftwareEndpoint_ClassifiesAsUnknown()
    {
        // Regression guard. NAudio's PKEY_Device_InstanceId returns this shape for
        // EVERY endpoint, so reading that property instead of the enumerator name
        // silently classifies all devices as Unknown - and Bluetooth then never gets
        // its warm-up state. Caught on a real machine; see DetectTransport.
        const string softwareEndpoint = @"SWD\MMDEVAPI\{0.0.1.00000000}.{b8e89132-6717-4fb3-a447-9f6a1e2b3050}";

        Assert.Equal(AudioTransport.Unknown, MicrophoneService.ClassifyInstanceId(softwareEndpoint));
    }

    [Theory]
    [InlineData("Headset (WH-1000XM4 Hands-Free AG Audio)", AudioTransport.Bluetooth)]
    [InlineData("Microphone (Bluetooth Audio)", AudioTransport.Bluetooth)]
    [InlineData("Microphone (USB Audio Device)", AudioTransport.Usb)]
    [InlineData("Microphone Array (Realtek Audio)", AudioTransport.Unknown)]
    public void FriendlyName_ClassifiesTransport_AsFallback(string name, AudioTransport expected)
    {
        Assert.Equal(expected, MicrophoneService.ClassifyName(name));
    }

    [Fact]
    public void BluetoothDevices_RequireWarmUp()
    {
        var bluetooth = new AudioDevice("id", "Headset", false, AudioTransport.Bluetooth);
        var usb = new AudioDevice("id", "Yeti", false, AudioTransport.Usb);
        var builtin = new AudioDevice("id", "Array", true, AudioTransport.Builtin);

        Assert.True(bluetooth.RequiresWarmUp);
        Assert.False(usb.RequiresWarmUp);
        Assert.False(builtin.RequiresWarmUp);
    }
}

/// <summary>
/// Round trip through the WAV writer and the decoder — the path every recording takes.
/// </summary>
public class AudioRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "osw-audio-tests", Guid.NewGuid().ToString("N"));

    public AudioRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static float[] Tone(int sampleRate, double seconds, double frequency = 440.0)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }
        return samples;
    }

    [Fact]
    public void WriteThenDecode_PreservesDurationAndAmplitude()
    {
        var path = Path.Combine(_dir, "tone.wav");
        var original = Tone(16000, 1.0);

        AudioRecorder.WriteMono16(path, original, 16000);
        var decoded = AudioDecoder.DecodeToWhisperFormat(path);

        Assert.Equal(original.Length, decoded.Length);

        // 16-bit quantisation, so exact equality is not available; a per-sample
        // tolerance of one LSB is.
        for (var i = 0; i < original.Length; i++)
        {
            Assert.InRange(decoded[i], original[i] - 0.001f, original[i] + 0.001f);
        }
    }

    [Fact]
    public void Decode_ResamplesTo16k()
    {
        // A 48 kHz source is what real capture hardware produces; whisper needs 16 kHz.
        var path = Path.Combine(_dir, "48k.wav");
        AudioRecorder.WriteMono16(path, Tone(48000, 1.0), 48000);

        var decoded = AudioDecoder.DecodeToWhisperFormat(path);

        // One second at 16 kHz, allowing for resampler edge handling.
        Assert.InRange(decoded.Length, 15800, 16200);
    }

    [Fact]
    public void WriteMono16_ClampsOutOfRangeSamples()
    {
        // Values outside [-1, 1] would wrap to the opposite sign and read as clicks.
        var path = Path.Combine(_dir, "clip.wav");
        float[] hot = [2.0f, -2.0f, 0f];

        AudioRecorder.WriteMono16(path, hot, 16000);
        var decoded = AudioDecoder.DecodeToWhisperFormat(path);

        Assert.InRange(decoded[0], 0.99f, 1.01f);
        Assert.InRange(decoded[1], -1.01f, -0.99f);
    }

    [Fact]
    public void Decode_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => AudioDecoder.DecodeToWhisperFormat(Path.Combine(_dir, "nope.wav")));
    }
}
