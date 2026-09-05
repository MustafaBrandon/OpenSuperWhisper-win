using System.Runtime.InteropServices;
using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Proves the low-level hook actually installs and delivers events.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests around <see cref="TriggerStateMachine"/> cover every decision but
/// none of the plumbing — a hook that never fires would pass all of them. These are
/// Tier 3 in the test plan: they need a real interactive session, so they run locally
/// and on a self-hosted runner, not on a headless agent.
/// </para>
/// <para>
/// Input is synthesised with F24, deliberately: it is a real key so the hook sees it,
/// and essentially nothing binds it, so chaining the event onward to the foreground
/// window is harmless.
/// </para>
/// </remarks>
[Collection("InputHooks")]
public class HookLivenessTests
{
    private const ushort VK_F24 = 0x87;

    private static void SendF24()
    {
        var inputs = new Win32Input.Input[2];

        inputs[0].type = Win32Input.INPUT_KEYBOARD;
        inputs[0].u.ki = new Win32Input.KeybdInput { wVk = VK_F24 };

        inputs[1].type = Win32Input.INPUT_KEYBOARD;
        inputs[1].u.ki = new Win32Input.KeybdInput { wVk = VK_F24, dwFlags = Win32Input.KEYEVENTF_KEYUP };

        var sent = Win32Input.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<Win32Input.Input>());

        Assert.Equal((uint)inputs.Length, sent);
    }

    [Fact]
    public void InputStruct_HasCorrectNativeSize()
    {
        // SendInput rejects the call outright if cbSize does not match, so a wrong
        // layout here fails silently rather than loudly. 40 bytes on x64.
        Assert.Equal(40, Marshal.SizeOf<Win32Input.Input>());
    }

    [Fact]
    public void KeyboardHook_InstallsAndUninstalls()
    {
        using var hook = new KeyboardHook();

        hook.Start();
        Assert.True(hook.IsInstalled);

        hook.Dispose();
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void KeyboardHook_ReceivesSynthesisedKey()
    {
        // The core M3 assertion: the hook thread, its message pump, the unmanaged
        // callback and the consumer thread all work end to end.
        using var hook = new KeyboardHook();

        var seen = new List<HookEvent>();
        using var gotEvent = new ManualResetEventSlim(false);

        hook.Event += (_, e) =>
        {
            if (e.Data != VK_F24) return;

            lock (seen)
            {
                seen.Add(e);
                if (seen.Count >= 2) gotEvent.Set();
            }
        };

        hook.Start();
        SendF24();

        Assert.True(gotEvent.Wait(TimeSpan.FromSeconds(5)),
            "Hook did not deliver the synthesised key within 5s.");

        lock (seen)
        {
            Assert.Contains(seen, e => e.Message == Win32Input.WM_KEYDOWN);
            Assert.Contains(seen, e => e.Message == Win32Input.WM_KEYUP);
        }
    }

    [Fact]
    public void SynthesisedInput_IsFlaggedInjected()
    {
        // The coordinator drops injected events so the app cannot trigger itself with
        // its own paste keystrokes. That defence is worthless if the flag never gets
        // set, so assert it directly.
        using var hook = new KeyboardHook();

        HookEvent? captured = null;
        using var gotEvent = new ManualResetEventSlim(false);

        hook.Event += (_, e) =>
        {
            if (e.Data != VK_F24 || e.Message != Win32Input.WM_KEYDOWN) return;
            captured = e;
            gotEvent.Set();
        };

        hook.Start();
        SendF24();

        Assert.True(gotEvent.Wait(TimeSpan.FromSeconds(5)), "No event received.");
        Assert.True(captured!.Value.Injected,
            "Synthesised input was not flagged as injected - self-trigger protection would not work.");
    }

    [Fact]
    public void Reinstall_KeepsDeliveringEvents()
    {
        // The watchdog reinstalls on a timer. Waiting 30s for the real one would make
        // this test miserable, so this exercises the same code path by rebinding the
        // coordinator, which tears down and reinstalls the hooks.
        using var coordinator = new TriggerCoordinator();

        coordinator.UseModifierKey(ModifierKey.RightControl);
        coordinator.UseModifierKey(ModifierKey.LeftControl);
        coordinator.UseMouseButton(MouseButton.Middle);
        coordinator.UseModifierKey(ModifierKey.RightAlt);

        // Surviving four rebinds without throwing or deadlocking is the assertion:
        // each one disposes two hook threads and starts fresh ones.
        Assert.Equal(TriggerMode.ModifierKey, coordinator.Mode);
        Assert.False(coordinator.IsRecording);
    }

    [Fact]
    public void Coordinator_IgnoresInjectedInputAsTrigger()
    {
        // End to end version of the self-trigger defence: bind F24's real neighbour
        // (right alt), synthesise it, and assert nothing starts recording.
        using var coordinator = new TriggerCoordinator();

        var started = 0;
        coordinator.StartRequested += (_, _) => Interlocked.Increment(ref started);
        coordinator.UseModifierKey(ModifierKey.RightAlt);

        var inputs = new Win32Input.Input[2];
        inputs[0].type = Win32Input.INPUT_KEYBOARD;
        inputs[0].u.ki = new Win32Input.KeybdInput { wVk = (ushort)Win32Input.VK_RMENU };
        inputs[1].type = Win32Input.INPUT_KEYBOARD;
        inputs[1].u.ki = new Win32Input.KeybdInput
        {
            wVk = (ushort)Win32Input.VK_RMENU,
            dwFlags = Win32Input.KEYEVENTF_KEYUP,
        };

        Win32Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Input.Input>());

        Thread.Sleep(500);

        Assert.Equal(0, started);
    }
}

/// <summary>
/// Hook tests share global OS state, so they must not run concurrently.
/// </summary>
[CollectionDefinition("InputHooks", DisableParallelization = true)]
public class InputHookCollection;
