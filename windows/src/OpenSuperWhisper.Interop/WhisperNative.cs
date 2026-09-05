using System.Runtime.InteropServices;

namespace OpenSuperWhisper.Interop;

/// <summary>
/// P/Invoke surface for whisper.dll.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>libwhisper/whisper.cpp/include/whisper.h</c> at submodule revision
/// <c>f049fff9</c> (v1.8.5-95). <b>Struct layouts and signatures in this file are
/// verified by hand against that header.</b> A mismatch is silent memory corruption,
/// not a compile error, so a submodule bump is a reviewed change: re-check every
/// declaration here and require the Tier 2 suite to pass.
/// </para>
/// <para>
/// Only the M0 surface is present so far — enough to prove the DLL loads and that
/// the VAD entry points we depend on are exported. Struct marshalling arrives with
/// the M1 transcription core; until then this file deliberately calls nothing that
/// passes a struct across the boundary.
/// </para>
/// </remarks>
public static partial class WhisperNative
{
    /// <summary>Import name; resolves to whisper.dll beside the managed assembly.</summary>
    public const string LibraryName = "whisper";

    /// <summary>whisper.cpp revision this file was written against.</summary>
    public const string PinnedRevision = "f049fff9";

    /// <summary>
    /// Returns a description of the CPU features the loaded build was compiled with
    /// (AVX, AVX2, F16C, …). Takes no arguments and returns a static C string, so it
    /// is the safest possible call to prove the library loads and dispatches.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "whisper_print_system_info")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial IntPtr whisper_print_system_info();

    /// <summary>
    /// CPU features the loaded whisper build was compiled with.
    /// </summary>
    public static string GetSystemInfo() =>
        Marshal.PtrToStringAnsi(whisper_print_system_info()) ?? string.Empty;

    // =========================================================================
    // Logging
    // =========================================================================

    [LibraryImport(LibraryName, EntryPoint = "whisper_log_set")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe partial void whisper_log_set(
        delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> callback, IntPtr userData);

    [System.Runtime.InteropServices.UnmanagedCallersOnly(
        CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void SwallowLog(int level, IntPtr text, IntPtr userData)
    {
        // Intentionally empty.
    }

    /// <summary>
    /// Silences whisper.cpp's own stderr chatter (model dimensions, VAD segment
    /// dumps, buffer sizes).
    /// </summary>
    /// <remarks>
    /// It logs unconditionally by default, which would make every transcription noisy
    /// and, once there is a UI, pollute a console the user never asked for. Using a
    /// function pointer rather than a delegate means there is no managed object for
    /// the GC to collect out from under native code.
    /// </remarks>
    public static unsafe void SilenceNativeLogging() =>
        whisper_log_set(&SwallowLog, IntPtr.Zero);

    // =========================================================================
    // Context lifecycle
    // =========================================================================

    [LibraryImport(LibraryName, EntryPoint = "whisper_context_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial WhisperContextParams ContextDefaultParams();

    /// <remarks>
    /// The "no_state" variant is deliberate: it loads the weights without a decoding
    /// state, so recordings can share the model while each gets its own state. The
    /// mac app does the same, and the reason matters — a hallucination on silence in
    /// one recording must not be able to condition the next one.
    /// </remarks>
    [LibraryImport(LibraryName, EntryPoint = "whisper_init_from_file_with_params_no_state",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial IntPtr InitFromFileNoState(string pathModel, WhisperContextParams params_);

    [LibraryImport(LibraryName, EntryPoint = "whisper_init_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial IntPtr InitState(IntPtr ctx);

    [LibraryImport(LibraryName, EntryPoint = "whisper_free_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void FreeState(IntPtr state);

    [LibraryImport(LibraryName, EntryPoint = "whisper_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void Free(IntPtr ctx);

    // =========================================================================
    // Transcription
    // =========================================================================

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial WhisperFullParams FullDefaultParams(WhisperSamplingStrategy strategy);

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_with_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static unsafe partial int FullWithState(
        IntPtr ctx, IntPtr state, WhisperFullParams params_, float* samples, int nSamples);

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_n_segments_from_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial int FullNSegmentsFromState(IntPtr state);

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_get_segment_text_from_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial IntPtr whisper_full_get_segment_text_from_state(IntPtr state, int iSegment);

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_get_segment_t0_from_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial long FullGetSegmentT0FromState(IntPtr state, int iSegment);

    [LibraryImport(LibraryName, EntryPoint = "whisper_full_get_segment_t1_from_state")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial long FullGetSegmentT1FromState(IntPtr state, int iSegment);

    /// <summary>Segment text as UTF-8. whisper owns the buffer; do not free it.</summary>
    public static string FullGetSegmentText(IntPtr state, int iSegment) =>
        Marshal.PtrToStringUTF8(whisper_full_get_segment_text_from_state(state, iSegment)) ?? string.Empty;

    // =========================================================================
    // VAD
    // =========================================================================

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_default_context_params")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial WhisperVadContextParams VadDefaultContextParams();

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_default_params")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial WhisperVadParams VadDefaultParams();

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_init_from_file_with_params",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial IntPtr VadInitFromFile(string pathModel, WhisperVadContextParams params_);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_segments_from_samples")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static unsafe partial IntPtr VadSegmentsFromSamples(
        IntPtr vctx, WhisperVadParams params_, float* samples, int nSamples);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_segments_n_segments")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial int VadSegmentsNSegments(IntPtr segments);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_segments_get_segment_t0")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial float VadSegmentsGetSegmentT0(IntPtr segments, int iSegment);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_segments_get_segment_t1")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial float VadSegmentsGetSegmentT1(IntPtr segments, int iSegment);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_free_segments")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void VadFreeSegments(IntPtr segments);

    [LibraryImport(LibraryName, EntryPoint = "whisper_vad_free")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    public static partial void VadFree(IntPtr vctx);

    /// <summary>
    /// VAD entry points this app depends on.
    /// </summary>
    /// <remarks>
    /// VAD gating is not optional — it is what keeps silence from producing
    /// hallucinated text (see docs/windows-port.md §5). Building whisper.dll
    /// ourselves is supposed to guarantee these are exported; this list exists so a
    /// test can assert that rather than assume it.
    /// </remarks>
    public static readonly string[] RequiredVadExports =
    [
        "whisper_vad_init_from_file_with_params",
        "whisper_vad_default_context_params",
        "whisper_vad_default_params",
        "whisper_vad_segments_from_samples",
        "whisper_vad_segments_n_segments",
        "whisper_vad_segments_get_segment_t0",
        "whisper_vad_segments_get_segment_t1",
        "whisper_vad_free_segments",
        "whisper_vad_free",
    ];
}
