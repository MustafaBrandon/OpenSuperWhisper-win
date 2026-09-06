using OpenSuperWhisper.Core.Audio;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Text;
using OpenSuperWhisper.Core.Transcription;

namespace OpenSuperWhisper.Cli;

/// <summary>
/// Runs the full dictation loop from a global hotkey â€” the M3 exit criterion.
/// </summary>
/// <remarks>
/// The first point at which the app behaves like the product: press the trigger
/// anywhere, speak, release, get text. Only the paste step (M4) and the UI (M5) are
/// still missing.
/// </remarks>
internal static class ListenCommand
{
    public static int Run(string[] args, string modelPath, string vadPath, bool verbose)
    {
        var mode = GetOption(args, "--trigger") ?? "rightctrl";
        var holdToRecord = !args.Contains("--toggle");
        var doubleTap = args.Contains("--double-tap");

        var insertion = new InsertionOptions(
            Paste: !args.Contains("--no-paste"),
            CopyToClipboard: args.Contains("--keep-clipboard"),
            Method: args.Contains("--type")
                ? InsertionMethod.UnicodeTyping
                : InsertionMethod.ClipboardPaste,
            AddTrailingSpace: !args.Contains("--no-trailing-space"));

        TempRecordingSweeper.Sweep();

        using var mics = new MicrophoneService();
        if (mics.ActiveDevice is null)
        {
            Console.Error.WriteLine("error: no audio input device available");
            return 1;
        }

        if (!verbose) Interop.WhisperNative.SilenceNativeLogging();

        Console.Error.WriteLine("loading model...");
        using var engine = new WhisperEngine(modelPath, vadPath);

        using var recorder = new AudioRecorder();
        using var coordinator = new TriggerCoordinator
        {
            HoldToRecord = holdToRecord,
            DoublePressToTrigger = doubleTap,
        };

        // Serialises the recording lifecycle. Trigger events arrive on a hook consumer
        // thread while transcription runs on the thread pool, and a stop must never
        // overtake the start it belongs to.
        var busy = new SemaphoreSlim(1, 1);

        coordinator.StartRequested += (_, _) =>
        {
            if (!busy.Wait(0))
            {
                Console.Error.WriteLine("(busy - still transcribing)");
                coordinator.NotifyRecordingStopped();
                return;
            }

            try
            {
                var device = mics.ActiveDevice!.Value;
                recorder.Start(device);
                Console.Error.WriteLine($"â— recording from {device.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error starting capture: {ex.Message}");
                coordinator.NotifyRecordingStopped();
                busy.Release();
            }
        };

        coordinator.StopRequested += (_, _) =>
        {
            Console.Error.WriteLine("â–  stopped, transcribing...");

            Task.Run(async () =>
            {
                try
                {
                    var wav = await recorder.StopAsync().ConfigureAwait(false);

                    if (wav is null)
                    {
                        Console.Error.WriteLine(
                            $"(discarded - under {AudioRecorder.MinimumDuration.TotalSeconds:F1}s)");
                        return;
                    }

                    try
                    {
                        var samples = AudioDecoder.DecodeToWhisperFormat(wav);
                        var text = engine.Transcribe(samples);

                        if (string.IsNullOrEmpty(text))
                        {
                            Console.Error.WriteLine("(no speech detected)");
                        }
                        else
                        {
                            Console.WriteLine(text);

                            var outcome = TextInjector.Insert(text, insertion);
                            if (outcome == InsertionResult.BlockedByElevation)
                            {
                                Console.Error.WriteLine(
                                    "(not inserted: the focused window is running elevated. " +
                                    "Windows blocks synthesised input from a normal process to an " +
                                    "elevated one. The text is on your clipboard.)");
                                ClipboardService.SetText(text);
                            }
                            else if (outcome == InsertionResult.Failed)
                            {
                                Console.Error.WriteLine("(insertion failed)");
                            }
                        }
                    }
                    finally
                    {
                        try { File.Delete(wav); } catch (IOException) { }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                }
                finally
                {
                    coordinator.NotifyRecordingStopped();
                    busy.Release();
                }
            });
        };

        coordinator.CancelRequested += (_, _) =>
        {
            Console.Error.WriteLine("âœ• cancelled");
            recorder.Cancel();
            coordinator.NotifyRecordingStopped();
            if (busy.CurrentCount == 0) busy.Release();
        };

        try
        {
            ApplyTrigger(coordinator, mode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not install input hook: {ex.Message}");
            return 1;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"trigger : {mode}");
        Console.Error.WriteLine($"mode    : {(holdToRecord ? "hold to record" : "toggle")}"
            + (doubleTap ? " + double tap to start" : ""));
        Console.Error.WriteLine($"insert  : {DescribeInsertion(insertion)}");
        Console.Error.WriteLine("escape  : cancel an in-flight recording");
        Console.Error.WriteLine("ctrl+c  : quit");
        Console.Error.WriteLine();

        var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };

        quit.Wait();
        Console.Error.WriteLine("stopping...");
        return 0;
    }

    private static string DescribeInsertion(InsertionOptions options)
    {
        if (!options.Paste)
        {
            return options.CopyToClipboard ? "copy to clipboard only" : "nothing (print only)";
        }

        var method = options.Method == InsertionMethod.UnicodeTyping
            ? "type as unicode"
            : "clipboard paste";

        return options.CopyToClipboard
            ? $"{method}, keep on clipboard"
            : $"{method}, restore clipboard after {TextInjector.ClipboardRestoreDelay.TotalSeconds:F1}s";
    }

    private static void ApplyTrigger(TriggerCoordinator coordinator, string mode)
    {
        var normalised = mode.Replace("-", string.Empty).ToLowerInvariant();

        switch (normalised)
        {
            case "middle": coordinator.UseMouseButton(MouseButton.Middle); return;
            case "button4": coordinator.UseMouseButton(MouseButton.Button4); return;
            case "button5": coordinator.UseMouseButton(MouseButton.Button5); return;
        }

        var key = normalised switch
        {
            "leftalt" => ModifierKey.LeftAlt,
            "rightalt" => ModifierKey.RightAlt,
            "leftctrl" or "leftcontrol" => ModifierKey.LeftControl,
            "rightctrl" or "rightcontrol" => ModifierKey.RightControl,
            "leftshift" => ModifierKey.LeftShift,
            "rightshift" => ModifierKey.RightShift,
            "leftwin" => ModifierKey.LeftWin,
            "rightwin" => ModifierKey.RightWin,
            _ => throw new ArgumentException(
                $"unknown trigger '{mode}'. Try rightalt, leftctrl, middle, button4, button5."),
        };

        coordinator.UseModifierKey(key);
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
