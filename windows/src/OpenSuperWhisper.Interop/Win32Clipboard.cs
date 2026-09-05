using System.Runtime.InteropServices;

namespace OpenSuperWhisper.Interop;

/// <summary>
/// Raw Win32 clipboard access.
/// </summary>
/// <remarks>
/// Raw rather than <c>System.Windows.Clipboard</c> because of
/// <see cref="GetClipboardSequenceNumber"/>: the restore guard needs to know whether
/// anyone touched the clipboard since we wrote to it, and no managed API exposes
/// that. Without it, restoring the previous contents can silently clobber something
/// the user copied while the transcript was being pasted.
/// </remarks>
public static partial class Win32Clipboard
{
    public const uint CF_TEXT = 1;
    public const uint CF_UNICODETEXT = 13;

    public const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll")]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// Increments whenever the clipboard content changes, by anyone.
    /// </summary>
    /// <remarks>
    /// The restore guard's entire basis: capture it after writing our text, compare
    /// before restoring, and abandon the restore if it moved.
    /// </remarks>
    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(IntPtr hMem);
}
