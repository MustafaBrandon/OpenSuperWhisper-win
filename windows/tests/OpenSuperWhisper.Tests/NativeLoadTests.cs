using System.Reflection;
using System.Runtime.InteropServices;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// M0 exit criteria for the native layer.
/// </summary>
/// <remarks>
/// These are the cheapest possible checks that the Rev. 3 interop decision actually
/// holds: that we can build and load our own whisper.dll, and that it exports the
/// VAD entry points the transcription core depends on. Everything here uses either
/// no arguments or raw symbol lookup, so none of it depends on struct layouts —
/// those arrive with M1 and carry their own risk.
/// </remarks>
public class NativeLoadTests
{
    private static string NativeDirectory =>
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    private static IntPtr LoadWhisper()
    {
        var path = Path.Combine(NativeDirectory, "whisper.dll");
        Assert.True(File.Exists(path),
            $"whisper.dll not found at {path}. Run windows\\native\\build-native.ps1 first.");

        return NativeLibrary.Load(path);
    }

    [Fact]
    public void WhisperLibrary_Loads()
    {
        var handle = LoadWhisper();
        try
        {
            Assert.NotEqual(IntPtr.Zero, handle);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void SystemInfo_IsReadable()
    {
        // Proves the calling convention and string marshalling work end to end,
        // without passing a struct across the boundary.
        var info = WhisperNative.GetSystemInfo();

        Assert.False(string.IsNullOrWhiteSpace(info));
    }

    [Fact]
    public void SystemInfo_ReportsAvx2()
    {
        // The CMake driver pins an explicit AVX2 baseline rather than GGML_NATIVE.
        // If this fails, the native build picked up different flags than intended
        // and the resulting DLL is tuned to whichever machine produced it.
        var info = WhisperNative.GetSystemInfo();

        Assert.Contains("AVX2 = 1", info);
    }

    [Theory]
    [MemberData(nameof(RequiredVadExports))]
    public void VadEntryPoint_IsExported(string export)
    {
        // The whole argument for building whisper.dll ourselves instead of taking a
        // prebuilt binding is that VAD is guaranteed present. Assert it rather than
        // assume it — a CMake option change could silently drop these.
        var handle = LoadWhisper();
        try
        {
            Assert.True(
                NativeLibrary.TryGetExport(handle, export, out var address) && address != IntPtr.Zero,
                $"whisper.dll does not export {export}. VAD gating cannot work without it.");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    public static TheoryData<string> RequiredVadExports()
    {
        var data = new TheoryData<string>();
        foreach (var export in WhisperNative.RequiredVadExports)
        {
            data.Add(export);
        }
        return data;
    }
}
