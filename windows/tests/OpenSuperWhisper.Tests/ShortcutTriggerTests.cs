using OpenSuperWhisper.Core.Input;
using OpenSuperWhisper.Core.Settings;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// The third trigger mode: a key combination, e.g. Alt+`.
/// </summary>
/// <remarks>
/// Stored as a hex virtual key rather than a key name, because names are
/// layout-dependent — see <see cref="ShortcutBinding"/>. These tests pin the round trip
/// and the rules that keep a shortcut from eating a key the user needs.
/// </remarks>
public class ShortcutBindingTests
{
    [Fact]
    public void RoundTripsThroughItsStoredForm()
    {
        var binding = new ShortcutBinding(0xC0, ShortcutModifiers.Alt | ShortcutModifiers.Control);

        Assert.Equal("Ctrl+Alt+0xC0", binding.ToString());
        Assert.Equal(binding, ShortcutBinding.Parse(binding.ToString()));
    }

    [Fact]
    public void SuggestedDefault_IsAltBacktick()
    {
        // The direct translation of the mac app's Option+`, and free in most Windows
        // applications.
        Assert.Equal(ShortcutBinding.VkOem3, ShortcutBinding.Suggested.VirtualKey);
        Assert.Equal(ShortcutModifiers.Alt, ShortcutBinding.Suggested.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Alt")]              // modifier with no key
    [InlineData("0xC0")]             // key with no modifier
    [InlineData("Ctrl+Alt+banana")]  // unparseable key
    [InlineData("Ctrl+0x1FF")]       // not a single-byte virtual key
    public void UnusableValues_ParseToNone(string? text)
    {
        // A hand-edited settings file is the source here, and the cost of a bad value
        // must be falling back to the default trigger, never starting with none.
        Assert.Equal(ShortcutBinding.None, ShortcutBinding.Parse(text));
        Assert.False(ShortcutBinding.Parse(text).IsValid);
    }

    [Fact]
    public void ParseAcceptsDecimalAndIsCaseInsensitive()
    {
        // Nobody hand-editing a settings file should have to guess which spelling works.
        var expected = new ShortcutBinding(0x20, ShortcutModifiers.Control | ShortcutModifiers.Shift);

        Assert.Equal(expected, ShortcutBinding.Parse("ctrl+shift+32"));
        Assert.Equal(expected, ShortcutBinding.Parse("CONTROL + SHIFT + 0x20"));
    }

    [Fact]
    public void AModifierlessCombination_IsNotValid()
    {
        // The bound key is withheld from other applications, so a bare letter would take
        // that character off the user's keyboard for as long as the app runs.
        Assert.False(new ShortcutBinding('K', ShortcutModifiers.None).IsValid);
        Assert.True(new ShortcutBinding('K', ShortcutModifiers.Alt).IsValid);
    }
}

/// <summary>
/// Key-cap labels. The recorder has to name the key the user actually pressed, or it
/// tells them to press something that is not there.
/// </summary>
public class KeyboardLayoutProviderTests
{
    [Theory]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x20, "Space")]
    [InlineData(0x0D, "Enter")]
    [InlineData(0x24, "Home")]
    [InlineData(0x2E, "Delete")]
    public void LayoutIndependentKeys_HaveFixedNames(int virtualKey, string expected)
    {
        // These keys mean the same thing on every layout, and several of them are named
        // unhelpfully by Windows itself.
        Assert.Equal(expected, KeyboardLayoutProvider.KeyCapName(virtualKey));
    }

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x7B, "F12")]
    [InlineData('A', "A")]
    [InlineData('7', "7")]
    public void LettersDigitsAndFunctionKeys_ReadAsThemselves(int virtualKey, string expected)
    {
        Assert.Equal(expected, KeyboardLayoutProvider.KeyCapName(virtualKey));
    }

    [Fact]
    public void EveryKeyGetsALabel()
    {
        // Never empty: an unlabelled button in a shortcut recorder is uninterpretable,
        // and a hex code is at least unambiguous.
        for (var vk = 1; vk <= 0xFF; vk++)
        {
            Assert.False(string.IsNullOrWhiteSpace(KeyboardLayoutProvider.KeyCapName(vk)),
                $"virtual key 0x{vk:X2} produced no label");
        }
    }

    [Fact]
    public void UnboundShortcut_ReadsAsNone()
    {
        Assert.Equal("None", KeyboardLayoutProvider.DisplayName(ShortcutBinding.None));
    }

    [Fact]
    public void DisplayNameUsesWindowsModifierOrder()
    {
        // Ctrl, Alt, Shift, Win — what every other Windows application shows.
        var binding = new ShortcutBinding(
            0x20,
            ShortcutModifiers.Win | ShortcutModifiers.Shift | ShortcutModifiers.Alt | ShortcutModifiers.Control);

        Assert.Equal("Ctrl + Alt + Shift + Win + Space", KeyboardLayoutProvider.DisplayName(binding));
    }
}

/// <summary>
/// What a shortcut withholds from other applications, and when.
/// </summary>
/// <remarks>
/// The rule that matters: a bound modifier key is withheld outright, but a shortcut's
/// main key is withheld only while its modifiers are held. Binding Alt+` must not cost
/// the user their backtick.
/// </remarks>
public class ShortcutSuppressionTests
{
    private const uint Backtick = 0xC0;

    private static HookEvent Down(uint data, ShortcutModifiers modifiers) =>
        new(Win32Input.WM_KEYDOWN, data, Injected: false, modifiers);

    private static HookEvent Up(uint data, ShortcutModifiers modifiers) =>
        new(Win32Input.WM_KEYUP, data, Injected: false, modifiers);

    private static bool Withheld(HookEvent evt, ShortcutModifiers required) =>
        KeyboardHook.ShouldSuppressWithModifiers(evt, Backtick, required);

    [Fact]
    public void WithItsModifiersHeld_TheKeyIsWithheld()
    {
        Assert.True(Withheld(Down(Backtick, ShortcutModifiers.Alt), ShortcutModifiers.Alt));
    }

    [Fact]
    public void OnItsOwn_TheKeyStillTypes()
    {
        // The whole point. Without this, binding Alt+` would delete the backtick from
        // the keyboard for as long as the app was running.
        Assert.False(Withheld(Down(Backtick, ShortcutModifiers.None), ShortcutModifiers.Alt));
    }

    [Fact]
    public void WithExtraModifiers_ItIsLeftAlone()
    {
        // Ctrl+Alt+` belongs to whatever else has bound it; only the exact combination
        // is ours.
        Assert.False(Withheld(
            Down(Backtick, ShortcutModifiers.Alt | ShortcutModifiers.Control), ShortcutModifiers.Alt));
    }

    [Fact]
    public void ReleaseIsWithheldWhateverTheModifiers()
    {
        // Users let go of Alt before the main key most of the time. Passing the key-up
        // through would deliver a stray keystroke to the focused application.
        Assert.True(Withheld(Up(Backtick, ShortcutModifiers.None), ShortcutModifiers.Alt));
    }

    [Fact]
    public void ADifferentKey_IsNeverWithheld()
    {
        Assert.False(Withheld(Down(0x41, ShortcutModifiers.Alt), ShortcutModifiers.Alt));
    }

    [Fact]
    public void ABareModifierTrigger_IsUnaffectedByTheShortcutRule()
    {
        // No required modifiers means the old behaviour exactly: withhold the bound key
        // whenever it appears.
        Assert.True(Withheld(Down(Backtick, ShortcutModifiers.None), ShortcutModifiers.None));
        Assert.True(Withheld(Down(Backtick, ShortcutModifiers.Shift), ShortcutModifiers.None));
    }

    [Fact]
    public void InjectedInput_IsNeverWithheld()
    {
        // Our own paste keystroke has to reach its target.
        var evt = new HookEvent(Win32Input.WM_KEYDOWN, Backtick, Injected: true, ShortcutModifiers.Alt);

        Assert.False(Withheld(evt, ShortcutModifiers.Alt));
    }
}

/// <summary>
/// Which trigger wins when more than one is configured.
/// </summary>
public class TriggerPrecedenceTests
{
    [Fact]
    public void AShortcutAloneResolvesToShortcutMode()
    {
        var resolved = TriggerBindings.Resolve(new AppSettings
        {
            MouseButtonHotkey = nameof(MouseButton.None),
            ModifierOnlyHotkey = nameof(ModifierKey.None),
            ShortcutHotkey = "Alt+0xC0",
        });

        Assert.Equal(TriggerMode.Shortcut, resolved.Mode);
        Assert.Equal(ShortcutBinding.Suggested, resolved.Shortcut);
    }

    [Fact]
    public void ABareModifierOutranksAShortcut()
    {
        // Precedence is mouse button, then bare modifier, then shortcut — inherited
        // from the mac app. The dialog clears the others when one is chosen; a
        // hand-edited file can set all three, and this is which one wins.
        var resolved = TriggerBindings.Resolve(new AppSettings
        {
            MouseButtonHotkey = nameof(MouseButton.None),
            ModifierOnlyHotkey = nameof(ModifierKey.RightControl),
            ShortcutHotkey = "Alt+0xC0",
        });

        Assert.Equal(TriggerMode.ModifierKey, resolved.Mode);
        Assert.Equal(ModifierKey.RightControl, resolved.Key);
    }

    [Fact]
    public void AMouseButtonOutranksEverything()
    {
        var resolved = TriggerBindings.Resolve(new AppSettings
        {
            MouseButtonHotkey = nameof(MouseButton.Button4),
            ModifierOnlyHotkey = nameof(ModifierKey.RightControl),
            ShortcutHotkey = "Alt+0xC0",
        });

        Assert.Equal(TriggerMode.MouseButton, resolved.Mode);
        Assert.Equal(MouseButton.Button4, resolved.Button);
    }

    [Fact]
    public void EverythingCleared_FallsBackToTheDefaultTrigger()
    {
        // Never "no trigger at all": that is an app that cannot record, with nothing on
        // screen to say why.
        var resolved = TriggerBindings.Resolve(new AppSettings
        {
            MouseButtonHotkey = nameof(MouseButton.None),
            ModifierOnlyHotkey = nameof(ModifierKey.None),
            ShortcutHotkey = string.Empty,
        });

        Assert.Equal(TriggerMode.ModifierKey, resolved.Mode);
        Assert.Equal(ModifierKey.RightControl, resolved.Key);
    }

    [Fact]
    public void NonsenseValues_FallBackToTheDefaultTrigger()
    {
        var resolved = TriggerBindings.Resolve(new AppSettings
        {
            MouseButtonHotkey = "ThumbstickSeven",
            ModifierOnlyHotkey = "HyperKey",
            ShortcutHotkey = "Meta+0xZZ",
        });

        Assert.Equal(TriggerMode.ModifierKey, resolved.Mode);
        Assert.Equal(ModifierKey.RightControl, resolved.Key);
    }

    [Fact]
    public void DefaultSettings_ResolveToRightControl()
    {
        var resolved = TriggerBindings.Resolve(new AppSettings());

        Assert.Equal(TriggerMode.ModifierKey, resolved.Mode);
        Assert.Equal(ModifierKey.RightControl, resolved.Key);
    }

    [Fact]
    public void ShortcutHotkey_DefaultsToUnbound()
    {
        // The default trigger is right Ctrl, a bare modifier. A shortcut is opt-in.
        Assert.Equal(string.Empty, new AppSettings().ShortcutHotkey);
    }

    [Fact]
    public void ShortcutHotkey_SurvivesTheSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "osw-shortcut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        try
        {
            new SettingsStore(path).Update(s =>
            {
                s.ModifierOnlyHotkey = nameof(ModifierKey.None);
                s.ShortcutHotkey = "Ctrl+Shift+0x20";
            });

            var reloaded = new SettingsStore(path).Current;

            Assert.Equal("Ctrl+Shift+0x20", reloaded.ShortcutHotkey);
            Assert.Equal(
                new ShortcutBinding(0x20, ShortcutModifiers.Control | ShortcutModifiers.Shift),
                ShortcutBinding.Parse(reloaded.ShortcutHotkey));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
