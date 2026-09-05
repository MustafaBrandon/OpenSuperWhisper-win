namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Channel mixing for multi-channel capture sources.
/// </summary>
public static class AudioMixing
{
    /// <summary>
    /// RMS below which a channel is treated as carrying no signal.
    /// </summary>
    /// <remarks>
    /// Inherited from the mac app. The case it exists for: a multi-input audio
    /// interface presents 6 or 8 channels with a single microphone plugged into one
    /// of them. A naive average divides by the channel count and the speech comes out
    /// at a fraction of its real amplitude — quiet enough to hurt recognition.
    /// </remarks>
    public const float ActivityThreshold = 0.0001f;

    /// <summary>
    /// Mixes interleaved multi-channel samples to mono, averaging only the channels
    /// that actually carry signal.
    /// </summary>
    /// <remarks>
    /// Channel activity is measured across the WHOLE signal rather than per buffer,
    /// which is a deliberate deviation from the mac implementation. Measuring per
    /// buffer means a channel that falls silent for a moment changes the divisor, so
    /// the output gain drifts within a single recording. Deciding once keeps gain
    /// constant, which is both more correct and easier to reason about.
    /// </remarks>
    public static float[] MixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        if (channels == 1) return interleaved.ToArray();
        if (interleaved.IsEmpty) return [];

        var frames = interleaved.Length / channels;
        if (frames == 0) return [];

        var active = FindActiveChannels(interleaved, channels, frames);
        var scale = 1f / active.Count;
        var result = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * channels;
            var sum = 0f;

            foreach (var channel in active)
            {
                sum += interleaved[baseIndex + channel];
            }

            result[frame] = sum * scale;
        }

        return result;
    }

    /// <summary>
    /// Channels whose RMS exceeds <see cref="ActivityThreshold"/>. Falls back to all
    /// channels when none qualify, so silent audio still mixes rather than throwing.
    /// </summary>
    public static List<int> FindActiveChannels(ReadOnlySpan<float> interleaved, int channels, int frames)
    {
        var active = new List<int>(channels);

        for (var channel = 0; channel < channels; channel++)
        {
            double energy = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = interleaved[frame * channels + channel];
                energy += (double)sample * sample;
            }

            if (Math.Sqrt(energy / frames) > ActivityThreshold)
            {
                active.Add(channel);
            }
        }

        if (active.Count == 0)
        {
            for (var channel = 0; channel < channels; channel++) active.Add(channel);
        }

        return active;
    }
}
