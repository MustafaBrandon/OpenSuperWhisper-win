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
