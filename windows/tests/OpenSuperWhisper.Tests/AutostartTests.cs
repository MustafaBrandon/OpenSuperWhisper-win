using Microsoft.Win32;
using OpenSuperWhisper.Core.Startup;
using Xunit;

namespace OpenSuperWhisper.Tests;

/// <summary>
/// Starting at sign-in, via the per-user Run key.
/// </summary>
/// <remarks>
/// Exercises the real registry code against a scratch key under HKCU rather than the
/// user's actual startup list — the logic worth testing is the reading and writing, and
/// a test that turned the developer's own machine's startup apps on and off would be a
/// poor trade for that.
/// </remarks>
public class AutostartTests : IDisposable
{
    private readonly string _keyPath =
        $@"Software\OpenSuperWhisper\Tests\{Guid.NewGuid():N}\Run";

    private const string Exe = @"C:\Program Files\OpenSuperWhisper\OpenSuperWhisper.exe";

    private AutostartRegistration Registration(string? executable = null) =>
        new(executable ?? Exe, _keyPath);

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\OpenSuperWhisper\Tests", false); }
        catch (Exception) { /* a leftover scratch key is not worth failing a test run */ }

        GC.SuppressFinalize(this);
    }

    private string? StoredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(AutostartRegistration.ValueName) as string;
    }

    [Fact]
    public void OffByDefault()
    {
        Assert.False(Registration().IsEnabled);
    }

    [Fact]
    public void EnableThenDisable_RoundTrips()
    {
        var autostart = Registration();

        Assert.True(autostart.Set(true));
        Assert.True(autostart.IsEnabled);

        Assert.True(autostart.Set(false));
        Assert.False(autostart.IsEnabled);
    }

    [Fact]
    public void DisablingWhenNotEnabled_Succeeds()
    {
        // Deleting a value that is not there is the state the caller asked for, not a
        // failure — and a dialog that reported one would be baffling.
        Assert.True(Registration().Set(false));
    }

    [Fact]
    public void TheStoredPathIsQuoted()
    {
        // The oldest bug in this registry key: an unquoted path under Program Files
        // makes Windows try C:\Program.exe first.
        Registration().Set(true);

        var command = StoredCommand();

        Assert.NotNull(command);
        Assert.StartsWith($"\"{Exe}\"", command);
    }

    [Fact]
    public void TheStoredCommandMarksTheLaunchAsAutomatic()
    {
        // Signing in must not put a window on screen: the machine asked for the app,
        // not the user.
        Registration().Set(true);

        Assert.Contains(AutostartRegistration.StartupArgument, StoredCommand());
    }

    [Fact]
    public void AnEntryPointingElsewhere_IsStale()
    {
        // What happens after the app is moved or reinstalled somewhere else: sign-in
        // silently starts nothing.
        Registration(@"C:\Old\Location\OpenSuperWhisper.exe").Set(true);

        var current = Registration();

        Assert.True(current.IsEnabled);
        Assert.True(current.IsStale);
    }

    [Fact]
    public void RepairRepointsAnExistingEntry()
    {
        Registration(@"C:\Old\Location\OpenSuperWhisper.exe").Set(true);

        var current = Registration();
        current.RepairIfStale();

        Assert.False(current.IsStale);
        Assert.StartsWith($"\"{Exe}\"", StoredCommand());
    }

    [Fact]
    public void RepairNeverCreatesAnEntry()
    {
        // Repairing the user's choice is helpful. Inventing one is not: an app that
        // added itself to startup because it noticed it was not there would be
        // indistinguishable from malware.
        var autostart = Registration();
        autostart.RepairIfStale();

        Assert.False(autostart.IsEnabled);
        Assert.Null(StoredCommand());
    }

    [Fact]
    public void AMatchingEntry_IsNotStale()
    {
        var autostart = Registration();
        autostart.Set(true);

        Assert.False(autostart.IsStale);
    }
}
