using System.Runtime.InteropServices;

namespace OpenSuperWhisper.Interop;

// =============================================================================
// Struct layouts mirroring libwhisper/whisper.cpp/include/whisper.h
// at submodule revision f049fff9 (v1.8.5-95).
//
// READ BEFORE EDITING
//
// These are hand-written. A wrong field order, a wrong type width, or a missing
// field is SILENT MEMORY CORRUPTION at the P/Invoke boundary - not a compile
// error, and often not an immediate crash either. Bumping the whisper.cpp
// submodule therefore requires re-checking every field here against the header.
//
// Two rules that keep this tractable:
//
//  1. C++ `bool` is ONE byte. C# `bool` marshals as a 4-byte Win32 BOOL by
//     default, which would shift every subsequent field. Every C++ bool is
//     declared here as `byte`, with a named property for readable access.
//
//  2. Layout is LayoutKind.Sequential with DEFAULT packing, never Pack = 1.
//     MSVC aligns these structs naturally; forcing Pack = 1 would remove the
//     padding the native side actually inserts.
//
// WhisperLayoutTests asserts the layout behaviourally: it reads the defaults
// back from whisper_full_default_params() and checks values that span the whole
// struct. If the layout drifts, those assertions fail rather than the app
// corrupting memory in production.
// =============================================================================

public enum WhisperSamplingStrategy
{
    Greedy = 0,
    BeamSearch = 1,
}

/// <summary>Mirrors <c>whisper_vad_params</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WhisperVadParams
{
    public float Threshold;
    public int MinSpeechDurationMs;
    public int MinSilenceDurationMs;
    public float MaxSpeechDurationS;
    public int SpeechPadMs;
    public float SamplesOverlap;
}

/// <summary>Mirrors <c>whisper_vad_context_params</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WhisperVadContextParams
{
    public int NThreads;
    public byte UseGpu;
    public int GpuDevice;
}

/// <summary>Mirrors <c>whisper_aheads</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WhisperAheads
{
    public nuint NHeads;
    public IntPtr Heads;
}

/// <summary>Mirrors <c>whisper_context_params</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WhisperContextParams
{
    public byte UseGpu;
    public byte FlashAttn;
    public int GpuDevice;

    public byte DtwTokenTimestamps;
    public int DtwAheadsPreset;
    public int DtwNTop;
    public WhisperAheads DtwAheads;
    public nuint DtwMemSize;

    public bool UseGpuFlag
    {
        readonly get => UseGpu != 0;
        set => UseGpu = value ? (byte)1 : (byte)0;
    }
}

/// <summary>
/// Mirrors <c>whisper_full_params</c>. Field order is exactly the header's.
/// </summary>
/// <remarks>
/// The anonymous <c>greedy</c> and <c>beam_search</c> structs in the header are
/// flattened here into <see cref="GreedyBestOf"/>, <see cref="BeamSearchBeamSize"/>
/// and <see cref="BeamSearchPatience"/>. That is layout-identical: both are
/// 4-byte-aligned aggregates of 4-byte members, so inlining them occupies the
/// same bytes. It is also easier to read than nested types.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct WhisperFullParams
{
    public WhisperSamplingStrategy Strategy;

    public int NThreads;
    public int NMaxTextCtx;
    public int OffsetMs;
    public int DurationMs;

    public byte Translate;
    public byte NoContext;
    public byte NoTimestamps;
    public byte SingleSegment;
    public byte PrintSpecial;
    public byte PrintProgress;
    public byte PrintRealtime;
    public byte PrintTimestamps;

    public byte TokenTimestamps;
    public float TholdPt;
    public float TholdPtsum;
    public int MaxLen;
    public byte SplitOnWord;
    public int MaxTokens;

    public byte DebugMode;
    public int AudioCtx;

    public byte TdrzEnable;

    public IntPtr SuppressRegex;

    public IntPtr InitialPrompt;
    public byte CarryInitialPrompt;
    public IntPtr PromptTokens;
    public int PromptNTokens;

    public IntPtr Language;
    public byte DetectLanguage;

    public byte SuppressBlank;
    public byte SuppressNst;

    public float Temperature;
    public float MaxInitialTs;
    public float LengthPenalty;

    public float TemperatureInc;
    public float EntropyThold;
    public float LogprobThold;
    public float NoSpeechThold;

    // struct { int best_of; } greedy;
    public int GreedyBestOf;

    // struct { int beam_size; float patience; } beam_search;
    public int BeamSearchBeamSize;
    public float BeamSearchPatience;

    public IntPtr NewSegmentCallback;
    public IntPtr NewSegmentCallbackUserData;

    public IntPtr ProgressCallback;
    public IntPtr ProgressCallbackUserData;

    public IntPtr EncoderBeginCallback;
    public IntPtr EncoderBeginCallbackUserData;

    public IntPtr AbortCallback;
    public IntPtr AbortCallbackUserData;

    public IntPtr LogitsFilterCallback;
    public IntPtr LogitsFilterCallbackUserData;

    public IntPtr GrammarRules;
    public nuint NGrammarRules;
    public nuint IStartRule;
    public float GrammarPenalty;

    public byte Vad;
    public IntPtr VadModelPath;
    public WhisperVadParams VadParams;

    // ---- readable accessors for the one-byte bools ----

    public bool NoContextFlag
    {
        readonly get => NoContext != 0;
        set => NoContext = value ? (byte)1 : (byte)0;
    }

    public bool NoTimestampsFlag
    {
        readonly get => NoTimestamps != 0;
        set => NoTimestamps = value ? (byte)1 : (byte)0;
    }

    public bool SuppressBlankFlag
    {
        readonly get => SuppressBlank != 0;
        set => SuppressBlank = value ? (byte)1 : (byte)0;
    }

    public bool PrintProgressFlag
    {
        readonly get => PrintProgress != 0;
        set => PrintProgress = value ? (byte)1 : (byte)0;
    }

    public bool PrintRealtimeFlag
    {
        readonly get => PrintRealtime != 0;
        set => PrintRealtime = value ? (byte)1 : (byte)0;
    }

    public bool PrintTimestampsFlag
    {
        readonly get => PrintTimestamps != 0;
        set => PrintTimestamps = value ? (byte)1 : (byte)0;
    }

    public bool PrintSpecialFlag
    {
        readonly get => PrintSpecial != 0;
        set => PrintSpecial = value ? (byte)1 : (byte)0;
    }

    public bool CarryInitialPromptFlag
    {
        readonly get => CarryInitialPrompt != 0;
        set => CarryInitialPrompt = value ? (byte)1 : (byte)0;
    }

    public bool DetectLanguageFlag
    {
        readonly get => DetectLanguage != 0;
        set => DetectLanguage = value ? (byte)1 : (byte)0;
    }
}
