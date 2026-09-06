using OpenSuperWhisper.Interop;

namespace OpenSuperWhisper.Core.Input;

/// <summary>Which input the user has bound to recording.</summary>
public enum TriggerMode
{
    /// <summary>A bare modifier key, e.g. right Alt on its own.</summary>
    ModifierKey,

    /// <summary>A mouse button, e.g. the thumb button.</summary>
    MouseButton,
}

/// <summary>
/// Owns the input hooks and turns raw events into recording commands.
/// Port of the mac app's <c>ShortcutManager</c> wiring.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one trigger is live at a time, with a fixed precedence inherited from the
/// mac app: a configured mouse button beats a modifier key. Both hooks are torn down
/// before either is installed, so a rebind can never leave two live.
/// </para>
/// <para>
/// All decisions come from <see cref="TriggerStateMachine"/>. This class only
/// translates and serialises — which is what keeps the interesting behaviour testable
/// without a keyboard.
/// </para>
/// </remarks>
public sealed class TriggerCoordinator : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TriggerStateMachine _machine = new();

    private KeyboardHook? _keyboardHook;
    private MouseHook? _mouseHook;

    private TriggerMode _mode = TriggerMode.ModifierKey;

    /// <summary>
    /// Default trigger: right Ctrl.
    /// </summary>
    /// <remarks>
    /// Not Alt, deliberately. Alt carries menu-activation semantics on Windows, and
    /// while suppression now prevents that, right Alt is also AltGr on many layouts —
    /// binding it would cost those users their accented characters. Right Ctrl has no
    /// standalone behaviour to lose, and left Ctrl still covers every shortcut.
    /// </remarks>
    private ModifierKey _modifierKey = ModifierKey.RightControl;
    private MouseButton _mouseButton = MouseButton.None;

    // Auto-repeat suppression. Holding a key produces a stream of WM_KEYDOWN with no
    // intervening WM_KEYUP; without this, every repeat would toggle recording.
    private bool _triggerIsDown;

    private bool _disposed;

    /// <summary>Raised when the trigger asks to start recording.</summary>
    public event EventHandler? StartRequested;

    /// <summary>Raised when the trigger asks to stop recording.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Raised when Escape is pressed while recording.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>
    /// When set, synthesised input is treated as real.
    /// </summary>
    /// <remarks>
    /// Debug affordance, off by default and gated on <c>OSW_ACCEPT_INJECTED=1</c>.
    /// Injected events are normally dropped so the app cannot trigger itself with the
    /// keystrokes it sends to paste — which also means no automated test can ever
    /// drive the trigger, since SendInput always marks its events injected. This makes
    /// the path testable without weakening the shipped behaviour.
    /// </remarks>
    public bool AcceptInjectedInput { get; set; } =
        Environment.GetEnvironmentVariable("OSW_ACCEPT_INJECTED") == "1";

    public TriggerCoordinator()
    {
        // Follow the user's own double-click speed rather than inventing a value.
        var systemDoubleClick = Win32Input.GetDoubleClickTime();
        if (systemDoubleClick > 0)
        {
            _machine.DoubleTapWindow = TimeSpan.FromMilliseconds(systemDoubleClick);
        }
    }

    public bool HoldToRecord
    {
        get { lock (_gate) return _machine.HoldToRecord; }
        set { lock (_gate) _machine.HoldToRecord = value; }
    }

    public bool DoublePressToTrigger
    {
        get { lock (_gate) return _machine.DoublePressToTrigger; }
        set { lock (_gate) _machine.DoublePressToTrigger = value; }
    }

    public bool IsRecording
    {
        get { lock (_gate) return _machine.IsRecording; }
    }

    public TriggerMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    /// <summary>Binds a bare modifier key as the trigger.</summary>
    public void UseModifierKey(ModifierKey key)
    {
        ArgumentOutOfRangeException.ThrowIfEqual((int)key, (int)ModifierKey.None);

        lock (_gate)
        {
            _mode = TriggerMode.ModifierKey;
            _modifierKey = key;
            _mouseButton = MouseButton.None;
            RebindLocked();
        }
    }

    /// <summary>Binds a mouse button as the trigger. Takes precedence over a modifier.</summary>
    public void UseMouseButton(MouseButton button)
    {
        ArgumentOutOfRangeException.ThrowIfEqual((int)button, (int)MouseButton.None);

        lock (_gate)
        {
            _mode = TriggerMode.MouseButton;
            _mouseButton = button;
            RebindLocked();
        }
    }

    /// <summary>Installs hooks for the current binding.</summary>
    public void Start()
    {
        lock (_gate) RebindLocked();
    }

    private void RebindLocked()
    {
        // Tear both down first. Enabling the new one before removing the old would
        // briefly leave two triggers live, and a press during that window fires twice.
        TeardownHooksLocked();

        _machine.Reset();
        _triggerIsDown = false;

        // The keyboard hook is always installed: even in mouse mode it carries Escape,
        // which cancels an in-flight recording.
        _keyboardHook = new KeyboardHook();
        _keyboardHook.Event += OnKeyboardEvent;

        // Withhold the trigger key from other applications. Without this, a bare Alt
        // tap opens the focused window's menu bar and the paste that follows the
        // dictation lands in the menu instead of the text field. Escape is never
        // withheld — it has to keep working everywhere.
        if (_mode == TriggerMode.ModifierKey && _modifierKey != ModifierKey.None)
        {
            _keyboardHook.SuppressedData = (uint)_modifierKey.ToVirtualKey();
        }

        _keyboardHook.Start();

        if (_mode == TriggerMode.MouseButton && _mouseButton != MouseButton.None)
        {
            _mouseHook = new MouseHook();
            _mouseHook.Event += OnMouseEvent;
            _mouseHook.SuppressedData = (uint)_mouseButton;
            _mouseHook.Start();
        }
    }

    private void TeardownHooksLocked()
    {
        if (_keyboardHook is not null)
        {
            _keyboardHook.Event -= OnKeyboardEvent;
            _keyboardHook.Dispose();
            _keyboardHook = null;
        }

        if (_mouseHook is not null)
        {
            _mouseHook.Event -= OnMouseEvent;
            _mouseHook.Dispose();
            _mouseHook = null;
        }
    }

    private void OnKeyboardEvent(object? sender, HookEvent e)
    {
        // Our own synthesised keystrokes must never be read as user input, or pasting
        // a transcript could start another recording.
        if (e.Injected && !AcceptInjectedInput) return;

        var isDown = e.Message is Win32Input.WM_KEYDOWN or Win32Input.WM_SYSKEYDOWN;
        var isUp = e.Message is Win32Input.WM_KEYUP or Win32Input.WM_SYSKEYUP;

        if (e.Data == Win32Input.VK_ESCAPE)
        {
            if (isDown && IsRecording) CancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        lock (_gate)
        {
            if (_mode != TriggerMode.ModifierKey) return;
            if (e.Data != (uint)_modifierKey.ToVirtualKey()) return;
        }

        if (isDown) HandlePressDown();
        else if (isUp) HandlePressUp();
    }

    private void OnMouseEvent(object? sender, HookEvent e)
    {
        if (e.Injected && !AcceptInjectedInput) return;

        lock (_gate)
        {
            if (_mode != TriggerMode.MouseButton) return;
            if (e.Data != (uint)_mouseButton) return;
        }

        var isDown = e.Message is Win32Input.WM_MBUTTONDOWN or Win32Input.WM_XBUTTONDOWN;

        if (isDown) HandlePressDown();
        else HandlePressUp();
    }

    private void HandlePressDown()
    {
        TriggerAction action;

        lock (_gate)
        {
            // Auto-repeat: a held key emits WM_KEYDOWN repeatedly with no key-up
            // between. Only the first counts as a press.
            if (_triggerIsDown) return;
            _triggerIsDown = true;

            action = _machine.OnPressDown(DateTime.UtcNow);
        }

        Dispatch(action);
    }

    private void HandlePressUp()
    {
        TriggerAction action;

        lock (_gate)
        {
            _triggerIsDown = false;
            action = _machine.OnPressUp(DateTime.UtcNow);
        }

        Dispatch(action);
    }

    private void Dispatch(TriggerAction action)
    {
        switch (action)
        {
            case TriggerAction.StartRecording:
                StartRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TriggerAction.StopRecording:
                StopRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Tells the coordinator recording ended by another route — Escape, an error, or
    /// the transcription finishing.
    /// </summary>
    public void NotifyRecordingStopped()
    {
        lock (_gate) _machine.NotifyRecordingStopped();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate) TeardownHooksLocked();
    }
}
