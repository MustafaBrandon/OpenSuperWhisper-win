using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
        Core.Diagnostics.Log.Start(Core.AppPaths.Root, "log-cli.txt");

        var modelPath = GetOption(args, "--model") ?? Meta("DefaultModelPath");
        var vadPath = GetOption(args, "--vad-model") ?? Meta("DefaultVadModelPath");
        var verbose = args.Contains("--verbose") || args.Contains("-v");

        switch (args[0])
        {
            case "devices":
                return RecordCommand.ListDevices(verbose);
            case "record":
                return RecordCommand.Record(args, modelPath, vadPath, verbose);
            case "listen":
                return ListenCommand.Run(args, modelPath, vadPath, verbose);
            case "insert":
                return InsertCommand.Run(args);
        }

        var audioPath = args[0];

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

        // Any format Media Foundation can read, resampled and downmixed to 16 kHz mono.
        float[] samples;
        try
        {
            samples = AudioDecoder.DecodeToWhisperFormat(audioPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or COMException)
        {
            Console.Error.WriteLine($"error: could not decode {audioPath}: {ex.Message}");
            return 1;
        }

        var sampleRate = AudioDecoder.WhisperSampleRate;

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
            osw - headless whisper transcription

            usage:
              osw <audio-file>          transcribe a file
              osw record [options]      record from the microphone, then transcribe
              osw listen [options]      global hotkey dictation loop
              osw insert [text]         inject text into the focused window
              osw devices               list audio input devices

            options:
              --model <path>       whisper model (.bin)      default: bundled tiny.en
              --vad-model <path>   Silero VAD model (.bin)   default: bundled silero
              --language <code>    language code or "auto"   default: en
              --timestamps         emit segment timestamps (disables VAD trimming)
              --beam               use beam search instead of greedy
              -v, --verbose        timings and capabilities on stderr
              -h, --help           this message

            record options:
              --device <id>        endpoint ID from `osw devices`; default is the
                                   system default capture device
              --seconds <n>        record for n seconds; default is until ENTER
              --keep               keep the captured wav instead of deleting it

            listen options:
              --trigger <name>     rightctrl (default), leftctrl, leftalt, rightalt,
                                   leftshift, rightshift, leftwin, rightwin,
                                   middle, button4, button5

                                   The bound key is withheld from other apps while
                                   bound, so it does nothing else. Alt is a poor
                                   choice: right Alt is AltGr on many layouts.
              --toggle             press to start, press again to stop
                                   (default is hold to record, release to stop)
              --double-tap         require a double tap to start recording

            insertion options (listen and insert):
              --type               type as unicode instead of pasting; never touches
                                   the clipboard, but slower and less universally
                                   supported
              --keep-clipboard     leave the transcript on the clipboard instead of
                                   restoring what was there before
              --no-paste           print only, do not insert (listen)
              --no-trailing-space  do not append a space after trailing punctuation
              --delay <n>          seconds to wait before inserting (insert, default 5)

            Any format Media Foundation can read is accepted for file input - wav,
            mp3, m4a, wma, flac - and is resampled to 16 kHz mono automatically.

            Transcript goes to stdout, everything else to stderr, so
            `osw file.mp3 > out.txt` captures only the text.
            """);
    }
}
