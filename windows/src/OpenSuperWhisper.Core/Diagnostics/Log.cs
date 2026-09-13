using System.Diagnostics;

namespace OpenSuperWhisper.Core.Diagnostics;

/// <summary>
/// Append-only diagnostic log.
/// </summary>
/// <remarks>
/// A windowed app has no console, so when something goes wrong at startup — a hook
/// that will not install, a model that will not load — there is otherwise nowhere for
/// the reason to go. Writes to <c>%LOCALAPPDATA%\OpenSuperWhisper\log.txt</c>.
/// </remarks>
public static class Log
{
    private static readonly Lock Gate = new();
    private static string? _path;

    /// <summary>Where the log is written. Null until <see cref="Start"/> runs.</summary>
    public static string? Path => _path;

    /// <summary>
    /// Whether <see cref="Detail"/> writes anything. Driven by the <c>debugMode</c>
    /// preference.
    /// </summary>
    /// <remarks>
    /// Off by default because the detailed lines are per-dictation and would bury the
    /// handful of lines that matter when something is actually broken. On, the log
    /// answers "what settings was it using and what did it hear" without a debugger.
    /// </remarks>
    public static bool Verbose { get; set; }

    /// <summary>
    /// Begins a fresh log for this run.
    /// </summary>
    /// <param name="directory">Where to write.</param>
    /// <param name="name">
    /// Log file name. The app and the CLI use different ones: they can run at the same
    /// time, and sharing a file meant whichever started second truncated the other's
    /// output — which is exactly the evidence you want when diagnosing an interaction
    /// between them.
    /// </param>
    public static void Start(string directory, string name = "log.txt")
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, name);

                KeepPreviousRun(_path);

                File.WriteAllText(_path,
                    $"OpenSuperWhisper log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }
            catch (Exception)
            {
                _path = null;
            }
        }
    }

    /// <summary>Where the previous run's log is kept.</summary>
    public static string PreviousPathFor(string path) =>
        System.IO.Path.ChangeExtension(path, null) + "-previous"
        + System.IO.Path.GetExtension(path);

    /// <summary>
    /// Moves the last run's log aside before this one truncates it.
    /// </summary>
    /// <remarks>
    /// One generation, not a rotation scheme. The reason it exists at all: a crash is
    /// almost always followed immediately by the user restarting the app, and without
    /// this that restart destroys the only record of what happened. Asking someone to
    /// reproduce a crash before you can read about it is a poor way to run a bug
    /// report.
    /// </remarks>
    private static void KeepPreviousRun(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            File.Move(path, PreviousPathFor(path), overwrite: true);
        }
        catch (IOException)
        {
            // Another instance may hold it open. Losing the previous log is not worth
            // failing to start a new one.
        }
    }

    public static void Write(string message)
    {
        Debug.WriteLine(message);

        lock (Gate)
        {
            if (_path is null) return;

            try
            {
                File.AppendAllText(_path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Losing a log line must never take the app down with it.
            }
        }
    }

    /// <summary>Writes only when <see cref="Verbose"/> is on.</summary>
    /// <remarks>
    /// Takes the message as a factory so the string is never built when verbose logging
    /// is off — these sit on the dictation path, and the cheapest version of a line
    /// nobody will read is one that was never formatted.
    /// </remarks>
    public static void Detail(Func<string> message)
    {
        if (!Verbose) return;

        ArgumentNullException.ThrowIfNull(message);
        Write(message());
    }

    public static void Error(string message, Exception ex) =>
        Write($"ERROR {message}: {ex.GetType().Name}: {ex.Message}");
}
