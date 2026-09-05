using System.Runtime.InteropServices;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Verifies the hand-written struct layouts in WhisperStructs.cs against the
/// native library.
/// </summary>
/// <remarks>
/// <para>
/// There is no exported <c>sizeof</c> to compare against, so this checks the layout
/// <i>behaviourally</i>: it asks whisper for its default params and asserts the
/// values land in the fields we expect. The expected values are transcribed from
/// <c>whisper_full_default_params</c> and <c>whisper_vad_default_params</c> in
/// src/whisper.cpp at revision f049fff9.
/// </para>
/// <para>
/// The assertions are deliberately spread across the whole struct — an early int, a
/// middle float block, a pointer, and the trailing nested VAD struct. A layout error
/// anywhere shifts everything after it, so a misaligned field cannot slip through by
/// coincidentally matching: it would have to corrupt every later field into exactly
/// the right value.
/// </para>
/// <para>
/// If these fail after a submodule bump, the fix is to re-read whisper.h and correct
/// the struct — not to relax the assertions.
/// </para>
/// </remarks>
public class WhisperLayoutTests
{
    [Fact]
    public void FullParams_EarlyIntegerFields_MatchNativeDefaults()
    {
        var p = WhisperNative.FullDefaultParams(WhisperSamplingStrategy.Greedy);

        Assert.Equal(WhisperSamplingStrategy.Greedy, p.Strategy);
        Assert.Equal(16384, p.NMaxTextCtx);
        Assert.Equal(0, p.OffsetMs);
        Assert.Equal(0, p.DurationMs);
        Assert.InRange(p.NThreads, 1, 64);
    }

    [Fact]
    public void FullParams_BoolFields_AreSingleBytes()
    {
        // The trap this catches: C# bool marshals as a 4-byte Win32 BOOL, which would
        // shift every field after the first bool. If that happened, these would not
        // all read back correctly.
        var p = WhisperNative.FullDefaultParams(WhisperSamplingStrategy.Greedy);

        Assert.False(p.Translate != 0);
        Assert.True(p.NoContextFlag);          // no_context defaults true
        Assert.False(p.NoTimestampsFlag);
        Assert.False(p.SingleSegment != 0);
        Assert.False(p.PrintSpecialFlag);
        Assert.True(p.PrintProgressFlag);      // print_progress defaults true
        Assert.False(p.PrintRealtimeFlag);
        Assert.True(p.PrintTimestampsFlag);    // print_timestamps defaults true
        Assert.True(p.SuppressBlankFlag);      // suppress_blank defaults true
        Assert.False(p.SuppressNst != 0);
    }

    [Fact]
    public void FullParams_FloatBlock_MatchesNativeDefaults()
    {
        var p = WhisperNative.FullDefaultParams(WhisperSamplingStrategy.Greedy);

        Assert.Equal(0.01f, p.TholdPt, 5);
        Assert.Equal(0.01f, p.TholdPtsum, 5);
        Assert.Equal(0.0f, p.Temperature, 5);
        Assert.Equal(1.0f, p.MaxInitialTs, 5);
        Assert.Equal(-1.0f, p.LengthPenalty, 5);
        Assert.Equal(0.2f, p.TemperatureInc, 5);
        Assert.Equal(2.4f, p.EntropyThold, 5);
        Assert.Equal(-1.0f, p.LogprobThold, 5);
        Assert.Equal(0.6f, p.NoSpeechThold, 5);
    }

    [Theory]
    [InlineData(WhisperSamplingStrategy.Greedy, 5, -1)]
    [InlineData(WhisperSamplingStrategy.BeamSearch, -1, 5)]
    public void FullParams_StrategyBlock_MatchesNativeDefaults(
        WhisperSamplingStrategy strategy, int expectedBestOf, int expectedBeamSize)
    {
        // whisper_full_default_params sets one of these per strategy after building
        // the base struct, so this also confirms the two flattened anonymous structs
        // sit at the right offsets.
        var p = WhisperNative.FullDefaultParams(strategy);

        Assert.Equal(expectedBestOf, p.GreedyBestOf);
        Assert.Equal(expectedBeamSize, p.BeamSearchBeamSize);
    }

    [Fact]
    public void FullParams_PointerFields_AreNullExceptLanguage()
    {
        var p = WhisperNative.FullDefaultParams(WhisperSamplingStrategy.Greedy);

        Assert.Equal(IntPtr.Zero, p.SuppressRegex);
        Assert.Equal(IntPtr.Zero, p.InitialPrompt);
        Assert.Equal(IntPtr.Zero, p.PromptTokens);
        Assert.Equal(0, p.PromptNTokens);

        // language defaults to the literal "en"
        Assert.NotEqual(IntPtr.Zero, p.Language);
        Assert.Equal("en", Marshal.PtrToStringUTF8(p.Language));

        Assert.Equal(IntPtr.Zero, p.AbortCallback);
        Assert.Equal(IntPtr.Zero, p.ProgressCallback);
        Assert.Equal(IntPtr.Zero, p.GrammarRules);
    }

    [Fact]
    public void FullParams_TailFields_MatchNativeDefaults()
    {
        // The most valuable assertions in this file. grammar_penalty and the nested
        // vad_params sit at the very end of the struct, so if ANY field before them
        // is the wrong width these read garbage.
        var p = WhisperNative.FullDefaultParams(WhisperSamplingStrategy.Greedy);

        Assert.Equal((nuint)0, p.NGrammarRules);
        Assert.Equal((nuint)0, p.IStartRule);
        Assert.Equal(100.0f, p.GrammarPenalty, 5);

        Assert.False(p.Vad != 0);
        Assert.Equal(IntPtr.Zero, p.VadModelPath);

        Assert.Equal(0.5f, p.VadParams.Threshold, 5);
        Assert.Equal(250, p.VadParams.MinSpeechDurationMs);
        Assert.Equal(100, p.VadParams.MinSilenceDurationMs);
        Assert.Equal(30, p.VadParams.SpeechPadMs);
        Assert.Equal(0.1f, p.VadParams.SamplesOverlap, 5);
        Assert.Equal(float.MaxValue, p.VadParams.MaxSpeechDurationS);
    }

    [Fact]
    public void VadParams_StandaloneDefaults_MatchNative()
    {
        var v = WhisperNative.VadDefaultParams();

        Assert.Equal(0.5f, v.Threshold, 5);
        Assert.Equal(250, v.MinSpeechDurationMs);
        Assert.Equal(100, v.MinSilenceDurationMs);
        Assert.Equal(float.MaxValue, v.MaxSpeechDurationS);
        Assert.Equal(30, v.SpeechPadMs);
        Assert.Equal(0.1f, v.SamplesOverlap, 5);
    }

    [Fact]
    public void ContextParams_Defaults_MatchNative()
    {
        var c = WhisperNative.ContextDefaultParams();

        Assert.True(c.UseGpuFlag);                  // use_gpu defaults true
        Assert.True(c.FlashAttn != 0);              // flash_attn defaults true
        Assert.Equal(0, c.GpuDevice);

        Assert.False(c.DtwTokenTimestamps != 0);
        Assert.Equal(-1, c.DtwNTop);

        // Nested whisper_aheads, then the trailing size_t. Reading both correctly
        // means every preceding field is the right width.
        Assert.Equal((nuint)0, c.DtwAheads.NHeads);
        Assert.Equal(IntPtr.Zero, c.DtwAheads.Heads);
        Assert.Equal((nuint)(1024 * 1024 * 128), c.DtwMemSize);
    }
}
