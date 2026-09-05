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
