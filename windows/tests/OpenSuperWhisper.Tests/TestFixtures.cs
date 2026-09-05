using System.Reflection;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Locations of models and audio fixtures, supplied by MSBuild as assembly metadata.
/// </summary>
/// <remarks>
/// These live outside windows/, so they are resolved through Directory.Build.props
/// rather than hardcoded here — see the "Repository layout" rule in
/// docs/windows-port.md. At the subtree split, only that file changes.
/// </remarks>
internal static class TestFixtures
{
    private static string Meta(string key) =>
        typeof(TestFixtures).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException(
            $"Assembly metadata '{key}' is missing. Check the AssemblyMetadata items in the test csproj.");

    public static string WhisperModel => Meta("BundledModelPath");
    public static string VadModel => Meta("VadModelPath");
    public static string SampleAudio => Meta("SampleAudioPath");

    public static bool ModelsAvailable =>
        File.Exists(WhisperModel) && File.Exists(VadModel) && File.Exists(SampleAudio);
}
