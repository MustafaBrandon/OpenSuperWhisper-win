using System.IO;
using System.IO.Pipes;
using System.Text;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.App;

/// <summary>
/// Carries a second launch's command line to the instance already running.
/// </summary>
/// <remarks>
/// Windows has no notion of "open this document in the app that is already up" for an
/// unpackaged app — it just starts another process. Without a channel like this, the
/// single-instance guard can only refuse, which means double-clicking an audio file
/// while the app is running does nothing at all.
/// <para>
/// A named pipe rather than a window message: it carries a path of any length without
/// the WM_COPYDATA marshalling dance, and it fails closed. If the pipe cannot be
/// created the app still runs — it just stops accepting forwarded files, which is
/// exactly the behaviour it had before this existed.
/// </para>
/// </remarks>
public sealed class SingleInstanceChannel : IDisposable
{
    /// <summary>
    /// Local to the session, unlike the instance mutex.
    /// </summary>
    /// <remarks>
    /// The mutex is Global so two users on one machine cannot both install hooks. The
    /// pipe deliberately is not: forwarding one user's file paths into another user's
    /// session would be both wrong and a small privacy leak.
    /// </remarks>
    private const string PipeName = "OpenSuperWhisper.Instance";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _listener;

    /// <summary>Raised on a background thread with the forwarded arguments.</summary>
    public event EventHandler<string[]>? MessageReceived;

    /// <summary>Begins accepting forwarded command lines.</summary>
    public void Listen()
    {
        _listener = Task.Run(() => ListenLoopAsync(_stopping.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // One server instance per connection: a forwarded launch is a single
                // short message, and re-creating the pipe is cheaper than keeping a
                // multi-client server correct.
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                var arguments = payload
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (arguments.Length > 0) MessageReceived?.Invoke(this, arguments);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("instance channel listener failed", ex);

                // Back off rather than spinning: whatever broke the pipe is unlikely to
                // be fixed within the same millisecond.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Hands arguments to the running instance.
    /// </summary>
    /// <returns>False when nothing was listening, which the caller should treat as
    /// "there is no running instance after all".</returns>
    public static bool Send(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(string.Join('\n', arguments));
            writer.Flush();

            return true;
        }
        catch (Exception ex)
        {
            // Timed out, or the other instance is shutting down. The caller falls back
            // to telling the user rather than silently doing nothing.
            Log.Error("could not reach the running instance", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        // The listener is blocked in WaitForConnectionAsync; connecting to ourselves is
        // the documented way to release it so the process can exit promptly.
        try
        {
            using var nudge = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            nudge.Connect(100);
        }
        catch (Exception)
        {
            // Already gone, which is the outcome this was trying to produce.
        }

        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutting down; a listener that will not stop must not hold the process.
        }

        _stopping.Dispose();
    }
}
