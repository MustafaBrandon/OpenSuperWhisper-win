using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>A raw trigger event from a low-level hook.</summary>
/// <param name="Modifiers">
/// Which modifiers were held when the event arrived. Keyboard events only; always
/// <see cref="ShortcutModifiers.None"/> for the mouse hook, which has no use for it.
/// The snapshot is taken in the callback rather than read later, because by the time a
/// consumer thread looks the user has often let go.
/// </param>
public readonly record struct HookEvent(
    int Message,
    uint Data,
    bool Injected,
    ShortcutModifiers Modifiers = ShortcutModifiers.None);

/// <summary>
/// Hosts a low-level input hook on a dedicated thread with its own message pump.
/// </summary>
/// <remarks>
/// <para>
/// <b>The callback has a hard latency budget.</b> Windows silently removes a
/// low-level hook whose callback exceeds <c>LowLevelHooksTimeout</c> (300 ms by
/// default) — no exception, no notification, the app simply stops responding to its
/// hotkey. So the callback here does exactly two things: push onto a lock-free queue
/// and return. No allocation, no logging, no locks, no user code. Everything real
/// happens on the consumer thread.
/// </para>
/// <para>
/// The hook needs a message pump on its own thread, which is why this owns a thread
/// rather than borrowing the UI one — a busy UI thread would starve the callback and
/// trip exactly the timeout above.
/// </para>
/// <para>
/// A watchdog reinstalls the hook periodically. Windows offers no "you were unhooked"
/// signal, so there is nothing to react to; reinstalling on a timer bounds how long a
/// silently-dead hook can stay dead. The new hook is installed before the old one is
/// removed, so no events fall through the gap.
/// </para>
/// </remarks>
public abstract class LowLevelHook : IDisposable
{
    /// <summary>How often the hook is reinstalled as a liveness guarantee.</summary>
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private static readonly IntPtr WatchdogTimerId = 1;

    private readonly ConcurrentQueue<HookEvent> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _started = new(false);

    private Thread? _pumpThread;
    private Thread? _consumerThread;
    private uint _pumpThreadId;
    private IntPtr _hookHandle;
    private volatile bool _running;
    private long _eventCount;

    /// <summary>Raised on a background thread for each hook event.</summary>
    public event EventHandler<HookEvent>? Event;

    /// <summary>Total events seen. Used by tests and diagnostics to prove liveness.</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>WH_KEYBOARD_LL or WH_MOUSE_LL.</summary>
    protected abstract int HookType { get; }

    /// <summary>
    /// Extracts the payload this hook cares about, or null to ignore the event.
    /// Runs on the hook callback, so it must stay allocation-free and fast.
    /// </summary>
    protected abstract HookEvent? Translate(int message, IntPtr lParam);

    public void Start()
    {
        if (_running) return;
        _running = true;

        _consumerThread = new Thread(ConsumeLoop)
        {
            IsBackground = true,
            Name = $"osw-hook-consumer-{HookType}",
        };
        _consumerThread.Start();

        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"osw-hook-pump-{HookType}",
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();

        // Surface installation failures to the caller rather than failing silently
        // later, when the user presses a hotkey that does nothing.
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Hook thread did not start.");
        }

        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed for hook type {HookType}. " +
                "Another process may be interfering, or the session is not interactive.");
        }
    }

    private void PumpLoop()
    {
        try
        {
            _pumpThreadId = Win32Input.GetCurrentThreadId();
            Install();
        }
        finally
        {
            _started.Set();
        }

        if (_hookHandle == IntPtr.Zero) return;

        var timer = Win32Input.SetTimer(
            IntPtr.Zero, WatchdogTimerId, (uint)WatchdogInterval.TotalMilliseconds, IntPtr.Zero);

        while (_running && Win32Input.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32Input.WM_TIMER) Reinstall();
        }

        if (timer != IntPtr.Zero) Win32Input.KillTimer(IntPtr.Zero, WatchdogTimerId);
        Uninstall();
    }

    private unsafe void Install()
    {
        // hMod must be a real module handle for a global low-level hook; passing the
        // executing module is what the documentation asks for.
        var module = Win32Input.GetModuleHandle(null);
        _hookHandle = Win32Input.SetWindowsHookEx(HookType, HookDispatcher.Callback, module, 0);

        if (_hookHandle != IntPtr.Zero) HookDispatcher.Register(HookType, this);
    }

    /// <summary>Installs a fresh hook, then removes the old one, leaving no gap.</summary>
    private unsafe void Reinstall()
    {
        var module = Win32Input.GetModuleHandle(null);
        var fresh = Win32Input.SetWindowsHookEx(HookType, HookDispatcher.Callback, module, 0);

        if (fresh == IntPtr.Zero) return;   // keep the existing one rather than losing both

        var old = Interlocked.Exchange(ref _hookHandle, fresh);
        if (old != IntPtr.Zero) Win32Input.UnhookWindowsHookEx(old);
    }

    private void Uninstall()
    {
        var handle = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero) Win32Input.UnhookWindowsHookEx(handle);

        HookDispatcher.Unregister(HookType);
    }

    /// <summary>
    /// The key or button to withhold from other applications, or 0 for none.
    /// </summary>
    /// <remarks>
    /// A bound trigger must not also reach the focused application. The case that
    /// forced this: tapping Alt on its own puts a window into menu mode, so the
    /// paste that follows a dictation goes to the menu bar instead of the text
    /// field — the transcript is correct, SendInput reports success, and nothing
    /// appears. Withholding the key avoids that, and equally stops a bound Shift
    /// from capitalising or a bound button from clicking.
    /// <para>
    /// The trade is that the bound key does nothing else while it is bound, which is
    /// what binding a dedicated trigger means.
    /// </para>
    /// </remarks>
    private volatile uint _suppressedData;

    public uint SuppressedData
    {
        get => _suppressedData;
        set => _suppressedData = value;
    }

    /// <summary>
    /// Called from the hook callback. Must stay trivially cheap — see the class remarks.
    /// </summary>
    /// <returns>True to withhold the event from other applications.</returns>
    internal bool Enqueue(int message, IntPtr lParam)
    {
        var translated = Translate(message, lParam);
        if (translated is null) return false;

        _queue.Enqueue(translated.Value);
        _signal.Set();

        return SuppressMatches(translated.Value);
    }

    /// <summary>
    /// Whether this particular event is the bound trigger. Overridden where matching
    /// takes more than the key code.
    /// </summary>
    protected virtual bool SuppressMatches(in HookEvent evt) =>
        ShouldSuppress(evt, _suppressedData);

    /// <summary>Whether an event should be withheld from other applications.</summary>
    /// <remarks>
    /// Pure so the rule can be tested directly. Two clauses matter beyond the obvious
    /// match: nothing is withheld when no trigger is bound, and <b>injected events are
    /// never withheld</b> — our own synthesised paste keystroke has to reach the
    /// application it is aimed at, and swallowing it would break insertion entirely.
    /// </remarks>
    internal static bool ShouldSuppress(HookEvent evt, uint suppressedData) =>
        !evt.Injected && suppressedData != 0 && evt.Data == suppressedData;

    private void ConsumeLoop()
    {
        while (_running)
        {
            _signal.WaitOne(250);

            while (_queue.TryDequeue(out var evt))
            {
                Interlocked.Increment(ref _eventCount);

                try
                {
                    Event?.Invoke(this, evt);
                }
                catch (Exception)
                {
                    // A handler throwing must not kill the consumer, or every later
                    // hotkey press is silently lost.
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;

        if (_pumpThreadId != 0)
        {
            Win32Input.PostThreadMessage(_pumpThreadId, Win32Input.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _signal.Set();
        _pumpThread?.Join(TimeSpan.FromSeconds(2));
        _consumerThread?.Join(TimeSpan.FromSeconds(2));

        _signal.Dispose();
        _started.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Routes the unmanaged hook callback to the owning instance.
/// </summary>
/// <remarks>
/// <c>UnmanagedCallersOnly</c> methods must be static, so the active hook is held in
/// static fields. That is not a limitation in practice — there is at most one keyboard
/// and one mouse hook — and it buys a real guarantee: the function pointer handed to
/// Windows can never be collected, which is a failure mode that delegate-based hooks
/// hit and that presents as the hotkey mysteriously dying after a while.
/// </remarks>
internal static unsafe class HookDispatcher
{
    private static LowLevelHook? _keyboard;
    private static LowLevelHook? _mouse;

    public static delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> Callback => &OnHook;

    public static void Register(int hookType, LowLevelHook hook)
    {
        if (hookType == Win32Input.WH_KEYBOARD_LL) _keyboard = hook;
        else _mouse = hook;
    }

    public static void Unregister(int hookType)
    {
        if (hookType == Win32Input.WH_KEYBOARD_LL) _keyboard = null;
        else _mouse = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 means "pass it on without inspecting", per the hook contract.
        if (nCode >= 0)
        {
            var message = (int)wParam;

            // Which hook fired is inferred from the message, since both share this
            // callback. Keyboard messages and mouse button messages do not overlap.
            var hook = message switch
            {
                Win32Input.WM_KEYDOWN or Win32Input.WM_KEYUP
                    or Win32Input.WM_SYSKEYDOWN or Win32Input.WM_SYSKEYUP => _keyboard,
                _ => _mouse,
            };

            // A non-zero return withholds the event from every application below us in
            // the chain. Only the bound trigger is ever withheld; everything else is
            // passed on untouched, because a low-level hook that swallows input
            // indiscriminately would break the whole desktop.
            if (hook?.Enqueue(message, lParam) == true) return 1;
        }

        return Win32Input.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
