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

    /// <summary>Begins a fresh log for this run.</summary>
    public static void Start(string directory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, "log.txt");

                // Truncate per run rather than rotating: this is a debugging aid, and
                // the interesting content is always the most recent launch.
                File.WriteAllText(_path,
                    $"OpenSuperWhisper log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }
            catch (Exception)
            {
                _path = null;
            }
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

    public static void Error(string message, Exception ex) =>
        Write($"ERROR {message}: {ex.GetType().Name}: {ex.Message}");
}
