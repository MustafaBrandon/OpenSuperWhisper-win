using System.Runtime.InteropServices;
using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Transcription;

/// <summary>
/// Whisper transcription engine. Port of the mac app's <c>WhisperEngine.swift</c>.
/// </summary>
/// <remarks>
/// The behaviours here are inherited deliberately, not reinvented — see
/// docs/windows-port.md §4 module 01 and §5. In particular the VAD gate and the
/// fresh-state-per-recording rule are what keep silence from producing hallucinated
/// text and stop one recording conditioning the next.
/// </remarks>
public sealed class WhisperEngine : IDisposable
{
    /// <summary>16 kHz / 100 — samples per centisecond, the unit VAD reports in.</summary>
    private const int SamplesPerCentisecond = 160;

    /// <summary>0.1 s of the following audio appended to each speech segment.</summary>
    private const int OverlapSamples = 1600;

    /// <summary>0.1 s of silence inserted between segments.</summary>
    private const int GapSamples = 1600;

    private IntPtr _context;
    private IntPtr _vadContext;
    private readonly string _vadModelPath;
    private bool _disposed;

    public WhisperEngine(string modelPath, string vadModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vadModelPath);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Whisper model not found.", modelPath);
        if (!File.Exists(vadModelPath))
            throw new FileNotFoundException("Silero VAD model not found.", vadModelPath);

        _vadModelPath = vadModelPath;

        var contextParams = WhisperNative.ContextDefaultParams();

        // CPU-only build for now: asking for GPU here would be silently ignored, but
        // being explicit keeps the intent visible when the Vulkan backend arrives.
        contextParams.UseGpuFlag = false;

        // Load the weights WITHOUT a decoding state. Each transcription then creates
        // its own state, so recordings share the model but never share prompt_past.
        _context = WhisperNative.InitFromFileNoState(modelPath, contextParams);

        if (_context == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load whisper model: {modelPath}");
    }

    public string SystemInfo => WhisperNative.GetSystemInfo();

    /// <summary>
    /// Transcribes 16 kHz mono samples.
    /// </summary>
    /// <returns>
    /// The transcript, or an empty string when VAD finds no speech. Callers rely on
    /// the empty result to discard a recording rather than storing silence.
    /// </returns>
    public string Transcribe(float[] samples, TranscriptionSettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(samples);

        settings ??= new TranscriptionSettings();

        if (samples.Length == 0) return string.Empty;

        // ---- VAD gate -------------------------------------------------------
        // whisper never sees non-speech audio: silence cannot produce hallucinated
        // text, and long pauses are not decoded at all.
        var speech = DetectSpeech(samples);
        if (speech.Count == 0) return string.Empty;

        // Trimming shifts every timestamp relative to the original file, so it is
        // skipped entirely when the caller asked for timestamps.
        var input = settings.ShowTimestamps
            ? samples
            : SpeechOnlySamples(samples, speech);

        if (input.Length == 0) return string.Empty;

        // ---- decode ---------------------------------------------------------
        var strategy = settings.UseBeamSearch
            ? WhisperSamplingStrategy.BeamSearch
            : WhisperSamplingStrategy.Greedy;

        var p = WhisperNative.FullDefaultParams(strategy);

        p.NThreads = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));

        // whisper.cpp's own default. With best_of = 1 the temperature fallback
        // degenerates to a single random sample on hard audio.
        p.GreedyBestOf = 5;
        if (settings.UseBeamSearch) p.BeamSearchBeamSize = settings.BeamSize;

        // Text context flows between 30 s windows inside one recording (better
        // coherence) but cannot leak into the next, because the state is per-call.
        p.NoContextFlag = false;
        p.NoTimestampsFlag = !settings.ShowTimestamps;
        p.SuppressBlankFlag = settings.SuppressBlankAudio;
        p.Temperature = settings.Temperature;
        p.NoSpeechThold = settings.NoSpeechThreshold;

        // Keep whisper.cpp off stdout; progress is reported through callbacks.
        p.PrintProgressFlag = false;
        p.PrintRealtimeFlag = false;
        p.PrintTimestampsFlag = false;
        p.PrintSpecialFlag = false;

        // Native strings must outlive the call, so they are pinned for its duration.
        var languageHandle = IntPtr.Zero;
        var promptHandle = IntPtr.Zero;

        try
        {
            var isAutoDetect = string.Equals(settings.Language, "auto", StringComparison.OrdinalIgnoreCase);
            if (!isAutoDetect)
            {
                languageHandle = Marshal.StringToCoTaskMemUTF8(settings.Language);
                p.Language = languageHandle;
            }
            else
            {
                p.Language = IntPtr.Zero;
            }

            // detect_language means "identify the language and stop", not "auto-detect
            // then transcribe" — leaving it false is what the mac app does.
            p.DetectLanguageFlag = false;

            if (!string.IsNullOrEmpty(settings.InitialPrompt))
            {
                promptHandle = Marshal.StringToCoTaskMemUTF8(settings.InitialPrompt);
                p.InitialPrompt = promptHandle;
                // With no_context = false the prompt would otherwise condition only the
                // first 30 s window; carrying it keeps the user's vocabulary effective
                // for the whole recording.
                p.CarryInitialPromptFlag = true;
            }

            return RunDecode(p, input, settings);
        }
        finally
        {
            if (languageHandle != IntPtr.Zero) Marshal.FreeCoTaskMem(languageHandle);
            if (promptHandle != IntPtr.Zero) Marshal.FreeCoTaskMem(promptHandle);
        }
    }

    private unsafe string RunDecode(WhisperFullParams p, float[] input, TranscriptionSettings settings)
    {
        // Fresh decoding state per recording — the isolation guarantee.
        var state = WhisperNative.InitState(_context);
        if (state == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create whisper decoding state.");

        try
        {
            int rc;
            fixed (float* ptr = input)
            {
                rc = WhisperNative.FullWithState(_context, state, p, ptr, input.Length);
            }

            if (rc != 0)
                throw new InvalidOperationException($"whisper_full_with_state failed with code {rc}.");

            var segmentCount = WhisperNative.FullNSegmentsFromState(state);
            var text = new System.Text.StringBuilder();

            for (var i = 0; i < segmentCount; i++)
            {
                if (settings.ShowTimestamps)
                {
                    var t0 = WhisperNative.FullGetSegmentT0FromState(state, i);
                    var t1 = WhisperNative.FullGetSegmentT1FromState(state, i);
                    text.Append($"[{t0 / 100.0:F1}->{t1 / 100.0:F1}] ");
                }

                text.Append(WhisperNative.FullGetSegmentText(state, i));
                text.Append('\n');
            }

            return CleanTranscript(text.ToString());
        }
        finally
        {
            WhisperNative.FreeState(state);
        }
    }

    /// <summary>Strips whisper's non-speech markers and trims.</summary>
    public static string CleanTranscript(string text) =>
        text.Replace("[MUSIC]", string.Empty)
            .Replace("[BLANK_AUDIO]", string.Empty)
            .Trim();

    // =========================================================================
    // VAD
    // =========================================================================

    private unsafe List<SpeechSegment> DetectSpeech(float[] samples)
    {
        if (_vadContext == IntPtr.Zero)
        {
            var ctxParams = WhisperNative.VadDefaultContextParams();
            _vadContext = WhisperNative.VadInitFromFile(_vadModelPath, ctxParams);

            if (_vadContext == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to load VAD model: {_vadModelPath}");
        }

        var vadParams = WhisperNative.VadDefaultParams();

        IntPtr segments;
        fixed (float* ptr = samples)
        {
            segments = WhisperNative.VadSegmentsFromSamples(_vadContext, vadParams, ptr, samples.Length);
        }

        if (segments == IntPtr.Zero)
            throw new InvalidOperationException("VAD segmentation failed.");

        try
        {
            var count = WhisperNative.VadSegmentsNSegments(segments);
            var result = new List<SpeechSegment>(count);

            for (var i = 0; i < count; i++)
            {
                result.Add(new SpeechSegment(
                    (long)WhisperNative.VadSegmentsGetSegmentT0(segments, i),
                    (long)WhisperNative.VadSegmentsGetSegmentT1(segments, i)));
            }

            return result;
        }
        finally
        {
            WhisperNative.VadFreeSegments(segments);
        }
    }

    /// <summary>
    /// Keeps only speech, mirroring upstream whisper_full's VAD stitching: each
    /// segment gets 0.1 s of the following audio as overlap, and segments are
    /// separated by 0.1 s of silence so the decoder still hears natural pauses
    /// between phrases.
    /// </summary>
    public static float[] SpeechOnlySamples(float[] samples, IReadOnlyList<SpeechSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0) return [];

        var result = new List<float>(samples.Length);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Count - 1;

            var start = Math.Clamp((int)(segment.StartCentiseconds * SamplesPerCentisecond), 0, samples.Length);
            var end = Math.Min((int)(segment.EndCentiseconds * SamplesPerCentisecond), samples.Length);

            if (!isLast) end = Math.Min(end + OverlapSamples, samples.Length);
            if (end <= start) continue;

            result.AddRange(samples.AsSpan(start, end - start));

            if (!isLast)
            {
                for (var g = 0; g < GapSamples; g++) result.Add(0f);
            }
        }

        return [.. result];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_vadContext != IntPtr.Zero)
        {
            WhisperNative.VadFree(_vadContext);
            _vadContext = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            WhisperNative.Free(_context);
            _context = IntPtr.Zero;
        }
    }
}

/// <summary>A VAD-detected speech span, in centiseconds of the original audio.</summary>
public readonly record struct SpeechSegment(long StartCentiseconds, long EndCentiseconds);
