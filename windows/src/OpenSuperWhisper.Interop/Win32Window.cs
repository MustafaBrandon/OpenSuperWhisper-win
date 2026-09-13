using System.Runtime.InteropServices;

namespace OpenSuperWhisper.Interop;

/// <summary>
/// Foreground-window inspection: keyboard layout and integrity level.
/// </summary>
public static partial class Win32Window
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // char[] is not blittable, so LibraryImport cannot marshal it without disabling
    // runtime marshalling assembly-wide. Pointers keep the source generator happy and
    // the call unambiguous.
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static unsafe partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    /// <summary>Title, class and owning process of a window, for diagnostics.</summary>
    public static unsafe string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "(none)";

        var title = stackalloc char[256];
        var titleLength = GetWindowText(hWnd, title, 256);

        var className = stackalloc char[256];
        var classLength = GetClassName(hWnd, className, 256);

        GetWindowThreadProcessId(hWnd, out var processId);

        var name = titleLength > 0 ? new string(title, 0, titleLength) : "(untitled)";
        var cls = classLength > 0 ? new string(className, 0, classLength) : "?";

        return $"0x{hWnd:X} '{name}' [{cls}] pid={processId}";
    }

    /// <summary>
    /// The keyboard layout of a specific thread, or of the calling thread when 0.
    /// </summary>
    /// <remarks>
    /// The target thread's layout is the one that matters. Resolving the paste key
    /// against our own layout is the bug the mac app's Text Input Services dance
    /// exists to avoid: on Dvorak, "V" is not where QWERTY thinks it is, and pasting
    /// silently types the wrong character.
    /// </remarks>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>
    /// Maps a character to a virtual key plus shift state for a given layout.
    /// </summary>
    /// <returns>
    /// Low byte is the virtual key; high byte is the shift state. -1 when the
    /// character has no key on that layout — the case for "v" on Cyrillic or Greek.
    /// </returns>
    /// <remarks>
    /// Takes <see cref="ushort"/> rather than <see cref="char"/>: LibraryImport refuses
    /// to marshal char because its native width is ambiguous, and the W entry point is
    /// unambiguously a UTF-16 code unit anyway.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExW")]
    public static partial short VkKeyScanEx(ushort ch, IntPtr dwhkl);

    /// <summary>Translate a virtual key to a scan code, keeping the extended prefix.</summary>
    public const uint MAPVK_VK_TO_VSC_EX = 4;

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    public static partial uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    /// <summary>
    /// The name printed on a key, in the user's own language and layout.
    /// </summary>
    /// <remarks>
    /// The lParam is a packed scan code, not a virtual key: bits 16–23 carry the scan
    /// code and bit 24 marks an extended key. Getting bit 24 wrong is how a shortcut
    /// recorder ends up telling a user to press "Num 7" when the key they pressed was
    /// Home.
    /// </remarks>
    /// <remarks>
    /// Takes a raw buffer pointer: source-generated interop will not marshal a
    /// <c>char[]</c> without disabling runtime marshalling assembly-wide, and a caller
    /// with a stack buffer is both simpler and cheaper than that trade.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "GetKeyNameTextW", SetLastError = true)]
    public static unsafe partial int GetKeyNameText(int lParam, char* lpString, int cchSize);

    // =========================================================================
    // Caret and screen geometry
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public Rect rcCaret;
    }

    /// <summary>
    /// Reports the caret rectangle for a thread, in client coordinates of
    /// <c>hwndCaret</c>.
    /// </summary>
    /// <remarks>
    /// Cheap and synchronous, unlike UI Automation — but it only knows about classic
    /// Win32 carets. WPF, Chromium, Electron and UWP surfaces report nothing here, which
    /// is exactly why there is a fallback chain rather than a single strategy.
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point lpPoint);

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;

        /// <summary>Excludes the taskbar — what the indicator must stay inside.</summary>
        public Rect rcWork;

        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>
    /// Working area of the monitor containing <paramref name="point"/>, in physical
    /// pixels. Falls back to a generous rectangle if the query fails, so callers never
    /// have to handle a null screen.
    /// </summary>
    public static Rect WorkAreaForPoint(Point point)
    {
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) return info.rcWork;
        }

        return new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    // =========================================================================
    // Integrity level
    // =========================================================================

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>TokenIntegrityLevel in the TOKEN_INFORMATION_CLASS enum.</summary>
    public const int TokenIntegrityLevel = 25;

    public const uint SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    public const uint SECURITY_MANDATORY_HIGH_RID = 0x3000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess,
        out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    public static partial IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [LibraryImport("advapi32.dll")]
    public static partial IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }
}
