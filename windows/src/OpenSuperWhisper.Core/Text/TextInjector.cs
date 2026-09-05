using System.Runtime.InteropServices;
using OpenSuperWhisper.Core.Diagnostics;
using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Text;

/// <summary>How a transcript reaches the focused application.</summary>
public enum InsertionMethod
{
    /// <summary>Clipboard plus a synthesised paste. Fast at any length, universal.</summary>
    ClipboardPaste,

    /// <summary>
    /// Typed directly as Unicode. Never touches the clipboard, so the save/restore
    /// dance disappears entirely — but it is slower and some applications mishandle it.
    /// </summary>
    UnicodeTyping,
}

/// <summary>What should happen to a finished transcript.</summary>
/// <param name="Paste">Insert it into the focused application.</param>
/// <param name="CopyToClipboard">Leave it on the clipboard afterwards.</param>
/// <param name="Method">How to insert, when <paramref name="Paste"/> is set.</param>
/// <param name="AddTrailingSpace">Append a space after trailing punctuation.</param>
public readonly record struct InsertionOptions(
    bool Paste = true,
    bool CopyToClipboard = false,
    InsertionMethod Method = InsertionMethod.ClipboardPaste,
    bool AddTrailingSpace = true);

/// <summary>Outcome of an insertion attempt.</summary>
public enum InsertionResult
{
    Success,
    NothingToDo,
    /// <summary>The focused window belongs to a higher-integrity process; UIPI blocks us.</summary>
    BlockedByElevation,
    Failed,
}

/// <summary>
/// Puts a transcript into whatever the user was typing in.
/// Port of the mac app's <c>ClipboardUtil</c>.
/// </summary>
public static class TextInjector
{
    /// <summary>
    /// How long to wait before restoring the previous clipboard.
    /// </summary>
    /// <remarks>
    /// Inherited from the mac app, and the reason is specific: slow consumers —
    /// browsers, Electron apps — service a synthesised paste well after the keystroke
    /// is posted. Restoring sooner makes them paste the <i>old</i> clipboard, so the
    /// user sees whatever they had copied before instead of their transcript.
    /// </remarks>
    public static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>QWERTY virtual key for V, used when the layout has no "v" at all.</summary>
    private const ushort QwertyVirtualKeyV = 0x56;

    private const int SendInputChunk = 256;

    /// <summary>
    /// Inserts <paramref name="text"/> according to <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Both options off means do nothing — the user has turned insertion off entirely,
    /// and the transcript still reaches the history.
    /// </remarks>
    public static InsertionResult Insert(string text, InsertionOptions options)
    {
        if (string.IsNullOrEmpty(text)) return InsertionResult.NothingToDo;

        var finalText = TextPostProcessor.ApplyTrailingSpace(text, options.AddTrailingSpace);

        if (!options.Paste)
        {
            if (!options.CopyToClipboard) return InsertionResult.NothingToDo;
            return ClipboardService.SetText(finalText) is not null
                ? InsertionResult.Success
                : InsertionResult.Failed;
        }

        if (IsForegroundElevated())
        {
            // Report rather than appear broken: SendInput to a higher-integrity window
            // succeeds at the API level and does nothing at all.
            return InsertionResult.BlockedByElevation;
        }

        return options.Method == InsertionMethod.UnicodeTyping
            ? TypeUnicode(finalText)
            : PasteViaClipboard(finalText, options.CopyToClipboard);
    }

    // =========================================================================
    // Clipboard paste
    // =========================================================================

    private static InsertionResult PasteViaClipboard(string text, bool keepInClipboard)
    {
        // Snapshot first: once we overwrite, the previous contents are gone.
        var snapshot = keepInClipboard ? null : ClipboardService.Capture();

        var sequence = ClipboardService.SetText(text);
        if (sequence is null)
        {
            Log.Write("  paste: clipboard write FAILED");
            return InsertionResult.Failed;
        }

        // Read back rather than trusting the write. SetClipboardData can report success
        // and leave nothing retrievable when the clipboard was opened without an owner
        // window, and a paste of stale content is worse than a reported failure.
        var readBack = ClipboardService.GetText();
        if (readBack != text)
        {
            Log.Write($"  paste: clipboard read-back mismatch (got {readBack?.Length.ToString() ?? "null"} chars, expected {text.Length})");
        }

        SendPasteKeystroke();

        if (snapshot is not null)
        {
            var expected = sequence.Value;

            // Restore off the calling thread so the caller is not blocked for 1.5 s
            // waiting on a courtesy.
            _ = Task.Run(async () =>
            {
                await Task.Delay(ClipboardRestoreDelay).ConfigureAwait(false);
                ClipboardService.RestoreIfUnchanged(snapshot, expected);
            });
        }

        return InsertionResult.Success;
    }

    /// <summary>Sends Ctrl+V using the focused application's own keyboard layout.</summary>
    private static void SendPasteKeystroke()
    {
        var virtualKey = ResolvePasteVirtualKey();

        var inputs = new Win32Input.Input[4];
        inputs[0] = KeyDown((ushort)Win32Input.VK_LCONTROL);
        inputs[1] = KeyDown(virtualKey);
        inputs[2] = KeyUp(virtualKey);
        inputs[3] = KeyUp((ushort)Win32Input.VK_LCONTROL);

        var sent = Win32Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Input.Input>());

        var foreground = Win32Window.GetForegroundWindow();
        Log.Write($"  paste: sent {sent}/4 events, vk=0x{virtualKey:X2}, foreground=0x{foreground:X}");

        if (sent != inputs.Length)
        {
            Log.Write($"  paste: SendInput rejected events, error {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// Finds the virtual key that produces "v" on the <i>focused application's</i>
    /// layout.
    /// </summary>
    /// <remarks>
    /// This is the Windows counterpart of the mac app's Text Input Services lookup, and
    /// it exists for the same reason: on Dvorak the physical key labelled V is not the
    /// key that sends "v", so a hardcoded keycode pastes nothing and types a stray
    /// character instead. The layout must come from the target thread, not ours.
    /// <para>
    /// Non-Latin layouts (Cyrillic, Greek, Hebrew) have no "v" at all, and VkKeyScanEx
    /// returns -1. Windows keeps Ctrl shortcuts on their Latin positions for those, so
    /// the QWERTY fallback is correct rather than merely a guess.
    /// </para>
    /// </remarks>
    public static ushort ResolvePasteVirtualKey()
    {
        try
        {
            var foreground = Win32Window.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return QwertyVirtualKeyV;

            var threadId = Win32Window.GetWindowThreadProcessId(foreground, out _);
            if (threadId == 0) return QwertyVirtualKeyV;

            var layout = Win32Window.GetKeyboardLayout(threadId);
            var scan = Win32Window.VkKeyScanEx('v', layout);


            if (scan == -1) return QwertyVirtualKeyV;

            return (ushort)(scan & 0xFF);
        }
        catch (Exception)
        {
            return QwertyVirtualKeyV;
        }
    }

    // =========================================================================
    // Unicode typing
    // =========================================================================

    /// <summary>
    /// Types text directly as Unicode, without touching the clipboard.
    /// </summary>
    /// <remarks>
    /// Each UTF-16 code unit becomes its own key event, so a surrogate pair — an emoji,
    /// say — is naturally sent as two events and reassembled by the receiver.
    /// </remarks>
    public static InsertionResult TypeUnicode(string text)
    {
        var units = new List<Win32Input.Input>(text.Length * 2);

        foreach (var unit in text)
        {
            units.Add(UnicodeKey(unit, up: false));
            units.Add(UnicodeKey(unit, up: true));
        }

        // Chunked because a single very large SendInput array is more likely to be
        // partially rejected, and a partial send is worse than several full ones.
        for (var offset = 0; offset < units.Count; offset += SendInputChunk)
        {
            var count = Math.Min(SendInputChunk, units.Count - offset);
            var chunk = units.GetRange(offset, count).ToArray();

            var sent = Win32Input.SendInput(
                (uint)chunk.Length, chunk, Marshal.SizeOf<Win32Input.Input>());

            if (sent != chunk.Length) return InsertionResult.Failed;
        }

        return InsertionResult.Success;
    }

    private static Win32Input.Input UnicodeKey(char unit, bool up)
    {
        var flags = Win32Input.KEYEVENTF_UNICODE | (up ? Win32Input.KEYEVENTF_KEYUP : 0);

        return new Win32Input.Input
        {
            type = Win32Input.INPUT_KEYBOARD,
            u = new Win32Input.InputUnion
            {
                ki = new Win32Input.KeybdInput { wVk = 0, wScan = unit, dwFlags = flags },
            },
        };
    }

    private static Win32Input.Input KeyDown(ushort virtualKey) => new()
    {
        type = Win32Input.INPUT_KEYBOARD,
        u = new Win32Input.InputUnion { ki = new Win32Input.KeybdInput { wVk = virtualKey } },
    };

    private static Win32Input.Input KeyUp(ushort virtualKey) => new()
    {
        type = Win32Input.INPUT_KEYBOARD,
        u = new Win32Input.InputUnion
        {
            ki = new Win32Input.KeybdInput { wVk = virtualKey, dwFlags = Win32Input.KEYEVENTF_KEYUP },
        },
    };

    // =========================================================================
    // Elevation
    // =========================================================================

    /// <summary>
    /// Whether the focused window belongs to a process we cannot send input to.
    /// </summary>
    /// <remarks>
    /// UIPI blocks synthesised input from a lower-integrity process to a higher one.
    /// The API still reports success, so without this check dictating into an elevated
    /// console looks like the app is broken.
    /// </remarks>
    public static bool IsForegroundElevated()
    {
        try
        {
            var foreground = Win32Window.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            Win32Window.GetWindowThreadProcessId(foreground, out var processId);
            if (processId == 0) return false;

            var target = Win32Window.OpenProcess(
                Win32Window.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

            if (target == IntPtr.Zero)
            {
                // Almost always "access denied because it is higher integrity". Treating
                // it as elevated gives the user an explanation instead of silence.
                return true;
            }

            try
            {
                var theirs = GetIntegrityLevel(target);
                var ours = GetIntegrityLevel(Win32Window.GetCurrentProcess());

                if (theirs is null || ours is null) return false;
                return theirs > ours;
            }
            finally
            {
                Win32Window.CloseHandle(target);
            }
        }
        catch (Exception)
        {
            // Never block an insertion because the check itself failed.
            return false;
        }
    }

    private static uint? GetIntegrityLevel(IntPtr processHandle)
    {
        if (!Win32Window.OpenProcessToken(processHandle, Win32Window.TOKEN_QUERY, out var token))
        {
            return null;
        }

        try
        {
            Win32Window.GetTokenInformation(
                token, Win32Window.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);

            if (needed == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Win32Window.GetTokenInformation(
                        token, Win32Window.TokenIntegrityLevel, buffer, needed, out _))
                {
                    return null;
                }

                var label = Marshal.PtrToStructure<Win32Window.TokenMandatoryLabel>(buffer);

                var countPointer = Win32Window.GetSidSubAuthorityCount(label.Label.Sid);
                if (countPointer == IntPtr.Zero) return null;

                var count = Marshal.ReadByte(countPointer);
                if (count == 0) return null;

                // The integrity RID is the last sub-authority.
                var ridPointer = Win32Window.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                if (ridPointer == IntPtr.Zero) return null;

                return (uint)Marshal.ReadInt32(ridPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Win32Window.CloseHandle(token);
        }
    }
}
