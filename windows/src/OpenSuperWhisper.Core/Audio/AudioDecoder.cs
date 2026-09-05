using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Decodes any audio file Media Foundation can read into the 16 kHz mono float
/// samples whisper expects.
/// </summary>
/// <remarks>
/// Replaces the mac app's AVFoundation conversion path. Media Foundation covers wav,
/// mp3, m4a/aac, wma and flac out of the box, which is the set the mac app accepts by
/// drag and drop.
/// </remarks>
public static class AudioDecoder
{
    public const int WhisperSampleRate = 16000;

    /// <summary>
    /// Decodes <paramref name="path"/> to 16 kHz mono float samples in [-1, 1].
    /// </summary>
    public static float[] DecodeToWhisperFormat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        using var reader = OpenReader(path);
        return Resample(reader);
    }

    /// <summary>
    /// Opens a reader for the file.
    /// </summary>
    /// <remarks>
    /// WAV goes through NAudio's own reader rather than Media Foundation: it is the
    /// format our recorder produces, and avoiding the MF round trip keeps the common
    /// path free of COM initialisation and codec negotiation.
    /// </remarks>
    private static WaveStream OpenReader(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new WaveFileReader(path);
            }
            catch (FormatException)
            {
                // Some .wav files carry compressed payloads (ADPCM, mu-law) that
                // WaveFileReader rejects. Media Foundation handles those.
            }
        }

        return new MediaFoundationReader(path);
    }

    private static float[] Resample(WaveStream reader)
    {
        var sourceChannels = reader.WaveFormat.Channels;

        ISampleProvider samples = reader.ToSampleProvider();

        // Downmix before resampling: fewer channels through the resampler, and the
        // activity-weighted mix needs the original channel layout to be meaningful.
        if (sourceChannels > 1)
        {
            samples = new ActivityWeightedMonoProvider(samples, sourceChannels);
        }

        if (samples.WaveFormat.SampleRate != WhisperSampleRate)
        {
            // WDL resampler: no MF dependency, and it flushes its own tail rather than
            // silently dropping the last few milliseconds the way an unflushed
            // converter does.
            samples = new WdlResamplingSampleProvider(samples, WhisperSampleRate);
        }

        return ReadAll(samples);
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var result = new List<float>(WhisperSampleRate * 16);
        var buffer = new float[WhisperSampleRate];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.AddRange(buffer.AsSpan(0, read));
        }

        return [.. result];
    }
}

/// <summary>
/// Mixes a multi-channel provider to mono, weighting by which channels carry signal.
/// </summary>
/// <remarks>
/// NAudio's built-in <c>StereoToMonoSampleProvider</c> handles only two channels and
/// averages unconditionally. This applies the activity rule from
/// <see cref="AudioMixing"/>, which is what keeps a single live mic on a
/// multi-channel interface at full amplitude.
/// <para>
/// Channel activity is decided from the first buffer that contains signal, then held
/// for the rest of the stream, so gain stays constant. Deciding per buffer would make
/// the divisor wobble whenever a channel briefly went quiet.
/// </para>
/// </remarks>
internal sealed class ActivityWeightedMonoProvider(ISampleProvider source, int channels) : ISampleProvider
{
    private readonly ISampleProvider _source = source;
    private readonly int _channels = channels;
    private List<int>? _activeChannels;
    private float[] _sourceBuffer = [];

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_sourceBuffer.Length < needed) _sourceBuffer = new float[needed];

        var read = _source.Read(_sourceBuffer, 0, needed);
        if (read == 0) return 0;

        var frames = read / _channels;
        var span = _sourceBuffer.AsSpan(0, frames * _channels);

        // Latch the active-channel set on the first buffer that is not silent, so a
        // leading moment of silence does not decide it for the whole recording.
        if (_activeChannels is null)
        {
            var candidate = AudioMixing.FindActiveChannels(span, _channels, frames);
            if (candidate.Count != _channels) _activeChannels = candidate;
        }

        var active = _activeChannels;
        var scale = 1f / (active?.Count ?? _channels);

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;
            var sum = 0f;

            if (active is null)
            {
                for (var ch = 0; ch < _channels; ch++) sum += span[baseIndex + ch];
            }
            else
            {
                foreach (var ch in active) sum += span[baseIndex + ch];
            }

            buffer[offset + frame] = sum * scale;
        }

        return frames;
    }
}
