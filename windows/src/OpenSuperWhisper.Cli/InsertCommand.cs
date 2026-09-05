using System.Diagnostics;
using OpenSuperWhisper.Core.Text;

namespace OpenSuperWhisper.Cli;

/// <summary>
/// Injects text into whatever window is focused after a countdown.
/// </summary>
/// <remarks>
/// The verification tool for M4. Insertion cannot be tested automatically end to end:
/// the only way to know text really landed in Word, Chrome or a terminal is to put it
/// there and look. The countdown exists so the operator can focus the target before
/// anything is sent.
/// </remarks>
internal static class InsertCommand
{
    public static int Run(string[] args)
    {
        var text = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : "The quick brown fox jumps over the lazy dog.";

        var method = args.Contains("--type")
            ? InsertionMethod.UnicodeTyping
            : InsertionMethod.ClipboardPaste;

        var keepClipboard = args.Contains("--keep-clipboard");
        var delay = int.TryParse(GetOption(args, "--delay"), out var d) ? d : 5;

        Console.Error.WriteLine($"text    : {text}");
        Console.Error.WriteLine($"method  : {method}");
        Console.Error.WriteLine($"clipboard: {(keepClipboard ? "keep transcript" : "restore previous")}");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Focus the target window. Inserting in {delay}s...");

        for (var remaining = delay; remaining > 0; remaining--)
        {
            Console.Error.Write($"\r{remaining}... ");
            Thread.Sleep(1000);
        }

        Console.Error.WriteLine("\rsending!   ");

        var elevated = TextInjector.IsForegroundElevated();
        if (elevated)
        {
            Console.Error.WriteLine(
                "warning: the focused window is elevated - Windows will block this.");
        }

        var options = new InsertionOptions(
            Paste: true, CopyToClipboard: keepClipboard, Method: method);

        var sw = Stopwatch.StartNew();
        var result = TextInjector.Insert(text, options);
        sw.Stop();

        Console.Error.WriteLine($"result  : {result}");
        Console.Error.WriteLine($"call    : {sw.Elapsed.TotalMilliseconds:F1} ms for {text.Length} chars");

        if (result == InsertionResult.BlockedByElevation)
        {
            Console.Error.WriteLine(
                "The focused window belongs to a higher-integrity process. This is UIPI, " +
                "not a bug: a normal process cannot synthesise input into an elevated one.");
            return 1;
        }

        // The measured time is the cost of OUR call, not of the text arriving. Unicode
        // typing queues one event per character and the receiving app drains them at
        // its own pace, so a slow target can lag well behind this number.
        if (method == InsertionMethod.UnicodeTyping)
        {
            Console.Error.WriteLine(
                "note: this measures the SendInput call, not delivery. The target app " +
                "drains the queue at its own speed.");
        }

        return result == InsertionResult.Success ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
