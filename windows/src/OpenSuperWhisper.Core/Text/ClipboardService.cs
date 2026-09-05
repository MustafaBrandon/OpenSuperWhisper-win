using System.Runtime.InteropServices;
using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Text;

/// <summary>A captured clipboard, for restoring after a paste.</summary>
/// <param name="Formats">Format id to raw bytes, for every format we could round-trip.</param>
/// <param name="SequenceNumber">Clipboard sequence number when the snapshot was taken.</param>
public sealed record ClipboardSnapshot(
    IReadOnlyList<(uint Format, byte[] Data)> Formats,
    uint SequenceNumber);

/// <summary>
/// Clipboard read, write, snapshot and restore.
/// </summary>
/// <remarks>
/// The clipboard is a single global resource that any process can hold open, so every
/// operation here retries briefly rather than failing on the first collision — losing
/// a transcript because Explorer happened to be mid-copy would be unacceptable.
/// </remarks>
public static class ClipboardService
{
    private const int OpenRetries = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Formats that cannot be round-tripped as a flat memory block.</summary>
    /// <remarks>
    /// These are handle-based (bitmaps, palettes, metafiles) rather than HGLOBAL, so
    /// copying their bytes would restore a dangling handle. Skipping them means an
    /// image on the clipboard is not preserved across a paste — a real limitation,
    /// and a much better outcome than restoring something corrupt.
    /// </remarks>
    private static readonly HashSet<uint> NonGlobalFormats =
    [
        2,   // CF_BITMAP
        3,   // CF_METAFILEPICT
        9,   // CF_PALETTE
        14,  // CF_ENHMETAFILE
        15,  // CF_HDROP is HGLOBAL, but its contents reference paths - kept, see below
    ];

    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (Win32Clipboard.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(RetryDelay);
        }

        return false;
    }

    /// <summary>Reads the clipboard as Unicode text, or null when it holds none.</summary>
    public static string? GetText()
    {
        if (!Win32Clipboard.IsClipboardFormatAvailable(Win32Clipboard.CF_UNICODETEXT)) return null;
        if (!TryOpen()) return null;

        try
        {
            var handle = Win32Clipboard.GetClipboardData(Win32Clipboard.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;

            var pointer = Win32Clipboard.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Win32Clipboard.GlobalUnlock(handle);
            }
        }
        finally
        {
            Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>Replaces the clipboard with <paramref name="text"/>.</summary>
    /// <returns>The sequence number after writing, or null if the write failed.</returns>
    public static uint? SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!TryOpen()) return null;

        try
        {
            if (!Win32Clipboard.EmptyClipboard()) return null;

            var handle = AllocateUnicode(text);
            if (handle == IntPtr.Zero) return null;

            if (Win32Clipboard.SetClipboardData(Win32Clipboard.CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                // Ownership only transfers on success; on failure the block is still ours.
                Win32Clipboard.GlobalFree(handle);
                return null;
            }
        }
        finally
        {
            Win32Clipboard.CloseClipboard();
        }

        return Win32Clipboard.GetClipboardSequenceNumber();
    }

    /// <summary>Captures every format that can be safely restored.</summary>
    public static ClipboardSnapshot? Capture()
    {
        if (!TryOpen()) return null;

        try
        {
            var captured = new List<(uint, byte[])>();

            uint format = 0;
            while ((format = Win32Clipboard.EnumClipboardFormats(format)) != 0)
            {
                if (NonGlobalFormats.Contains(format)) continue;

                var handle = Win32Clipboard.GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;

                var size = Win32Clipboard.GlobalSize(handle);
                if (size == 0 || size > int.MaxValue) continue;

                var pointer = Win32Clipboard.GlobalLock(handle);
                if (pointer == IntPtr.Zero) continue;

                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    captured.Add((format, bytes));
                }
                finally
                {
                    Win32Clipboard.GlobalUnlock(handle);
                }
            }

            return new ClipboardSnapshot(captured, Win32Clipboard.GetClipboardSequenceNumber());
        }
        finally
        {
            Win32Clipboard.CloseClipboard();
        }
    }

    /// <summary>
    /// Restores a snapshot, but only if the clipboard still holds what we last wrote.
    /// </summary>
    /// <param name="snapshot">What to put back.</param>
    /// <param name="expectedSequence">
    /// The sequence number from our own write. If the clipboard has moved past it, the
    /// user (or another app) took the clipboard over in the meantime, and restoring
    /// would destroy their data.
    /// </param>
    /// <returns>True if the restore happened.</returns>
    public static bool RestoreIfUnchanged(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Win32Clipboard.GetClipboardSequenceNumber() != expectedSequence) return false;
        if (!TryOpen()) return false;

        try
        {
            if (!Win32Clipboard.EmptyClipboard()) return false;

            foreach (var (format, data) in snapshot.Formats)
            {
                var handle = AllocateBytes(data);
                if (handle == IntPtr.Zero) continue;

                if (Win32Clipboard.SetClipboardData(format, handle) == IntPtr.Zero)
                {
                    Win32Clipboard.GlobalFree(handle);
                }
            }

            return true;
        }
        finally
        {
            Win32Clipboard.CloseClipboard();
        }
    }

    private static IntPtr AllocateUnicode(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        return AllocateBytes(bytes);
    }

    /// <summary>
    /// Copies bytes into a moveable global block suitable for SetClipboardData, which
    /// takes ownership of it.
    /// </summary>
    private static IntPtr AllocateBytes(byte[] data)
    {
        var handle = Win32Clipboard.GlobalAlloc(Win32Clipboard.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var pointer = Win32Clipboard.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            Win32Clipboard.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            Win32Clipboard.GlobalUnlock(handle);
        }

        return handle;
    }
}
