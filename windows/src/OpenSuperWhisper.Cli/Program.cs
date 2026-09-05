using System.Diagnostics;
using System.Reflection;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Transcription;

namespace OpenSuperWhisper.Cli;

/// <summary>
/// Headless transcription driver — the M1 deliverable.
/// </summary>
/// <remarks>
/// Exists so the transcription core can be exercised and compared against the mac
/// build without any UI, audio capture or input handling in the way. It is a
/// development tool, not a shipped surface.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var audioPath = args[0];
        var modelPath = GetOption(args, "--model") ?? Meta("DefaultModelPath");
        var vadPath = GetOption(args, "--vad-model") ?? Meta("DefaultVadModelPath");
        var verbose = args.Contains("--verbose") || args.Contains("-v");

        var settings = new TranscriptionSettings
        {
            Language = GetOption(args, "--language") ?? "en",
            ShowTimestamps = args.Contains("--timestamps"),
            UseBeamSearch = args.Contains("--beam"),
        };

        if (!File.Exists(audioPath))
        {
            Console.Error.WriteLine($"error: audio file not found: {audioPath}");
            return 1;
        }

        var (samples, sampleRate) = WavReader.Read(audioPath);

        if (sampleRate != WavReader.WhisperSampleRate)
        {
            // Resampling belongs to the audio pipeline (module 02), which arrives with
            // M2. Failing loudly is better than transcribing at the wrong rate and
            // quietly producing nonsense.
            Console.Error.WriteLine(
                $"error: {audioPath} is {sampleRate} Hz; this build requires " +
                $"{WavReader.WhisperSampleRate} Hz mono. Resampling arrives with M2.");
            return 1;
        }

        // whisper.cpp logs model dimensions, VAD dumps and buffer sizes to stderr
        // unconditionally. Keep that behind --verbose rather than inflicting it on
        // every run.
        if (!verbose) Interop.WhisperNative.SilenceNativeLogging();

        if (verbose)
        {
            Console.Error.WriteLine($"audio   {samples.Length / (double)sampleRate:F2}s @ {sampleRate} Hz");
            Console.Error.WriteLine($"model   {modelPath}");
            Console.Error.WriteLine($"vad     {vadPath}");
        }

        var sw = Stopwatch.StartNew();
        using var engine = new WhisperEngine(modelPath, vadPath);
        var loadMs = sw.ElapsedMilliseconds;

        if (verbose) Console.Error.WriteLine($"caps    {engine.SystemInfo.Trim()}");

        sw.Restart();
        var text = engine.Transcribe(samples, settings);
        var decodeMs = sw.ElapsedMilliseconds;

        if (verbose)
        {
            var audioSeconds = samples.Length / (double)sampleRate;
            Console.Error.WriteLine($"load    {loadMs} ms");
            Console.Error.WriteLine($"decode  {decodeMs} ms ({audioSeconds / (decodeMs / 1000.0):F1}x realtime)");
        }

        if (string.IsNullOrEmpty(text))
        {
            // Not an error: the VAD gate found no speech. The app uses this to discard
            // a recording rather than store an empty one.
            Console.Error.WriteLine("no speech detected");
            return 0;
        }

        Console.WriteLine(text);
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Meta(string key) =>
        typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"Assembly metadata '{key}' missing.");

    private static void PrintUsage()
    {
        Console.WriteLine("""
            osw - headless whisper transcription (M1)

            usage:
              osw <audio.wav> [options]

            options:
              --model <path>       whisper model (.bin)      default: bundled tiny.en
              --vad-model <path>   Silero VAD model (.bin)   default: bundled silero
              --language <code>    language code or "auto"   default: en
              --timestamps         emit segment timestamps (disables VAD trimming)
              --beam               use beam search instead of greedy
              -v, --verbose        timings and capabilities on stderr
              -h, --help           this message

            Input must be 16 kHz mono WAV. Transcript goes to stdout; everything
            else to stderr, so `osw file.wav > out.txt` captures only the text.
            """);
    }
}
