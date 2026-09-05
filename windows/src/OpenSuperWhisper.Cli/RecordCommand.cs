using System.Diagnostics;
using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Transcription;

namespace OpenSuperWhisper.Cli;

/// <summary>
/// Records from the microphone and transcribes — the M2 exit criterion, closing the
/// loop from capture through the M1 engine.
/// </summary>
internal static class RecordCommand
{
    public static int ListDevices(bool verbose)
    {
        using var mics = new MicrophoneService();

        if (mics.Devices.Count == 0)
        {
            Console.Error.WriteLine("no audio input devices found");
            return 1;
        }

        var active = mics.ActiveDevice;

        foreach (var device in mics.Devices)
        {
            var marker = device.Id == active?.Id ? "*" : " ";
            var warmUp = device.RequiresWarmUp ? "  (needs warm-up)" : string.Empty;
            Console.WriteLine($" {marker} {device.Name}{warmUp}");
            Console.WriteLine($"     {device.Transport}  {device.Id}");

            if (verbose) DumpProperties(device.Id);
        }

        Console.WriteLine();
        Console.WriteLine("* = will be used for recording");
        return 0;
    }

    /// <summary>
    /// Dumps a device's WASAPI property store.
    /// </summary>
    /// <remarks>
    /// Transport classification depends on which properties a given driver actually
    /// publishes, and that varies by vendor. When a device is misclassified — a
    /// Bluetooth headset not getting the warm-up state, say — this shows what the
    /// driver exposes rather than requiring a guess.
    /// </remarks>
    private static void DumpProperties(string endpointId)
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDevice(endpointId);
            var properties = device.Properties;

            Console.WriteLine($"     -- {properties.Count} properties --");

            for (var i = 0; i < properties.Count; i++)
            {
                try
                {
                    var key = properties.Get(i);
                    if (properties.GetValue(i).Value is not string value || value.Length == 0) continue;

                    Console.WriteLine($"     [{key.formatId},{key.propertyId}] {value}");
                }
                catch (Exception)
                {
                    // Individual properties can fail to marshal; skip them.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     (property store unavailable: {ex.Message})");
        }
    }

    public static int Record(string[] args, string modelPath, string vadPath, bool verbose)
    {
        var seconds = double.TryParse(GetOption(args, "--seconds"), out var s) ? s : 0;
        var deviceId = GetOption(args, "--device");
        var keepAudio = args.Contains("--keep");

        TempRecordingSweeper.Sweep();

        using var mics = new MicrophoneService();
        if (deviceId is not null) mics.SetPreferredDevice(deviceId);

        var device = mics.ActiveDevice;
        if (device is null)
        {
            Console.Error.WriteLine("error: no audio input device available");
            return 1;
        }

        Console.Error.WriteLine($"recording from: {device}");
        if (device.Value.RequiresWarmUp)
        {
            // Bluetooth links negotiate on first use. The real app shows a "connecting"
            // indicator here rather than letting the user talk into a dead mic.
            Console.Error.WriteLine("(bluetooth device - allow a moment to wake up)");
        }

        using var recorder = new AudioRecorder();

        try
        {
            recorder.Start(device.Value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not start capture: {ex.Message}");
            return 1;
        }

        if (seconds > 0)
        {
            Console.Error.WriteLine($"recording {seconds:F1}s...");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
        else
        {
            Console.Error.WriteLine("recording... press ENTER to stop");
            Console.ReadLine();
        }

        Console.Error.WriteLine("stopping...");
        var wavPath = recorder.StopAsync().GetAwaiter().GetResult();

        if (wavPath is null)
        {
            // Not an error: shorter than the minimum, so treated as an accidental tap.
            Console.Error.WriteLine(
                $"discarded - recording was under {AudioRecorder.MinimumDuration.TotalSeconds:F1}s");
            return 0;
        }

        try
        {
            var samples = AudioDecoder.DecodeToWhisperFormat(wavPath);

            if (verbose)
            {
                var duration = samples.Length / (double)AudioDecoder.WhisperSampleRate;
                Console.Error.WriteLine($"captured {duration:F2}s -> {wavPath}");
            }

            if (!verbose) Interop.WhisperNative.SilenceNativeLogging();

            var sw = Stopwatch.StartNew();
            using var engine = new WhisperEngine(modelPath, vadPath);
            var text = engine.Transcribe(samples);

            if (verbose) Console.Error.WriteLine($"transcribed in {sw.ElapsedMilliseconds} ms");

            if (string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("no speech detected");
                return 0;
            }

            Console.WriteLine(text);
            return 0;
        }
        finally
        {
            if (keepAudio)
            {
                Console.Error.WriteLine($"audio kept at: {wavPath}");
            }
            else
            {
                try { File.Delete(wavPath); } catch (IOException) { }
            }
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
