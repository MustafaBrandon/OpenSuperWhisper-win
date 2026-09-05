using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Transcription;
using Xunit;
using Xunit.Abstractions;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// M1 exit criteria: the transcription core produces correct text, and the VAD gate
/// behaves the way the mac app's does.
/// </summary>
public class TranscriptionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Golden transcript for jfk.wav.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <i>observed output</i> of ggml-tiny.en.bin through this build, not
    /// the canonical wording of the speech — tiny.en punctuates it with periods rather
    /// than the commas a human would write. Recording what the model actually produces
    /// is the point: this is a regression net for engine, parameter and submodule
    /// changes, so it must reflect reality rather than an ideal.
    /// </para>
    /// <para>
    /// Whitespace and case are normalised before comparison. Punctuation is not — a
    /// change there would be a genuine behavioural regression worth failing on.
    /// </para>
    /// <para>
    /// NOT YET verified against the macOS build. Same model, same whisper.cpp revision
    /// and same parameters should give identical text, but that comparison needs a Mac
    /// and has not been run. Until it is, this pins Windows-to-Windows consistency
    /// only. See docs/windows-port.md, M1 exit criteria.
    /// </para>
    /// </remarks>
    private const string ExpectedJfk =
        "And so my fellow Americans ask not what your country can do for you. "
        + "Ask what you can do for your country.";

    private static string Normalise(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void WavReader_ReadsJfkFixture_AsMono16k()
    {
        Assert.True(TestFixtures.ModelsAvailable, "Model/fixture files are missing.");

        var (samples, sampleRate) = WavReader.Read(TestFixtures.SampleAudio);

        Assert.Equal(16000, sampleRate);
        Assert.NotEmpty(samples);

        // ~11 s of audio; a wildly different length means the header walk is wrong.
        var seconds = samples.Length / (double)sampleRate;
        Assert.InRange(seconds, 10.0, 12.0);

        // Real audio, not silence or clipping.
        Assert.All(samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.Contains(samples, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public void Transcribe_JfkFixture_MatchesExpectedText()
    {
        // The headline M1 assertion: model loads, VAD gates, decoder runs, text comes out.
        Assert.True(TestFixtures.ModelsAvailable, "Model/fixture files are missing.");

        var (samples, _) = WavReader.Read(TestFixtures.SampleAudio);

        using var engine = new WhisperEngine(TestFixtures.WhisperModel, TestFixtures.VadModel);
        output.WriteLine($"whisper: {engine.SystemInfo}");

        var text = engine.Transcribe(samples);
        output.WriteLine($"transcript: {text}");

        Assert.Equal(Normalise(ExpectedJfk), Normalise(text), ignoreCase: true);
    }

    [Fact]
    public void Transcribe_Silence_ReturnsEmpty()
    {
        // The VAD gate's whole purpose. Without it, whisper hallucinates confident
        // sentences out of silence — and callers rely on the empty string to discard
        // the recording rather than storing a phantom transcript.
        Assert.True(TestFixtures.ModelsAvailable, "Model/fixture files are missing.");

        var silence = new float[16000 * 3];

        using var engine = new WhisperEngine(TestFixtures.WhisperModel, TestFixtures.VadModel);
        var text = engine.Transcribe(silence);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Transcribe_TwiceOnSameEngine_ProducesIdenticalText()
    {
        // Proves the fresh-state-per-recording rule. If decoding state leaked between
        // calls, the second transcript would be conditioned by the first and could
        // drift. This is the isolation guarantee that stops a hallucination on silence
        // poisoning the next recording.
        Assert.True(TestFixtures.ModelsAvailable, "Model/fixture files are missing.");

        var (samples, _) = WavReader.Read(TestFixtures.SampleAudio);

        using var engine = new WhisperEngine(TestFixtures.WhisperModel, TestFixtures.VadModel);

        var first = engine.Transcribe(samples);
        var second = engine.Transcribe(samples);

        Assert.Equal(Normalise(first), Normalise(second));
        Assert.NotEmpty(first);
    }

    [Fact]
    public void Transcribe_EmptyInput_ReturnsEmpty()
    {
        Assert.True(TestFixtures.ModelsAvailable, "Model/fixture files are missing.");

        using var engine = new WhisperEngine(TestFixtures.WhisperModel, TestFixtures.VadModel);

        Assert.Equal(string.Empty, engine.Transcribe([]));
    }
}

/// <summary>
/// VAD segment stitching. Pure logic, so it runs without models — Tier 1 in the
/// test plan, unlike the transcription tests above.
/// </summary>
public class SpeechStitchingTests
{
    private const int Cs = 160;      // samples per centisecond at 16 kHz
    private const int Overlap = 1600; // 0.1 s
    private const int Gap = 1600;     // 0.1 s

    [Fact]
    public void NoSegments_ProducesEmpty()
    {
        var samples = new float[16000];

        Assert.Empty(WhisperEngine.SpeechOnlySamples(samples, []));
    }

    [Fact]
    public void SingleSegment_HasNoOverlapOrGap()
    {
        // A lone segment is also the last one, so it gets neither the trailing
        // overlap nor a following silence gap.
        var samples = new float[16000];
        var segments = new[] { new SpeechSegment(10, 20) };   // 0.10 s .. 0.20 s

        var result = WhisperEngine.SpeechOnlySamples(samples, segments);

        Assert.Equal((20 - 10) * Cs, result.Length);
    }

    [Fact]
    public void TwoSegments_GetOverlapAndGap()
    {
        var samples = new float[16000 * 4];
        var segments = new[]
        {
            new SpeechSegment(10, 20),
            new SpeechSegment(100, 120),
        };

        var result = WhisperEngine.SpeechOnlySamples(samples, segments);

        var expected = ((20 - 10) * Cs + Overlap)   // first segment + overlap
                     + Gap                           // inserted silence
                     + (120 - 100) * Cs;             // last segment, no overlap
        Assert.Equal(expected, result.Length);
    }

    [Fact]
    public void SegmentBeyondBuffer_IsClamped()
    {
        // VAD reports centiseconds derived from the sample count; rounding can put the
        // final segment a few samples past the end. Clamping rather than throwing
        // matters because this is the common case on the last segment of a recording.
        var samples = new float[16000];              // 1.0 s
        var segments = new[] { new SpeechSegment(50, 500) }; // claims 0.5 s .. 5.0 s

        var result = WhisperEngine.SpeechOnlySamples(samples, segments);

        Assert.Equal(16000 - 50 * Cs, result.Length);
    }

    [Fact]
    public void InvertedSegment_IsSkipped()
    {
        var samples = new float[16000];
        var segments = new[] { new SpeechSegment(200, 100) };

        Assert.Empty(WhisperEngine.SpeechOnlySamples(samples, segments));
    }

    [Fact]
    public void GapContentIsSilence()
    {
        var samples = new float[16000 * 4];
        Array.Fill(samples, 0.5f);

        var segments = new[]
        {
            new SpeechSegment(10, 20),
            new SpeechSegment(100, 120),
        };

        var result = WhisperEngine.SpeechOnlySamples(samples, segments);

        // The gap sits right after the first segment plus its overlap.
        var gapStart = (20 - 10) * Cs + Overlap;
        for (var i = gapStart; i < gapStart + Gap; i++)
        {
            Assert.Equal(0f, result[i]);
        }
    }
}

public class CleanTranscriptTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData("[MUSIC]", "")]
    [InlineData("  hello  ", "hello")]
    [InlineData("[MUSIC] hello [BLANK_AUDIO]", "hello")]
    [InlineData("hello world", "hello world")]
    public void RemovesMarkersAndTrims(string input, string expected)
    {
        Assert.Equal(expected, WhisperEngine.CleanTranscript(input));
    }
}
