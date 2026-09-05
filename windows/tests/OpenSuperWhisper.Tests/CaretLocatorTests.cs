using System.Diagnostics;
using OpenSuperWhisper.Core.Indicator;
using OpenSuperWhisper.Interop;
using Xunit;

namespace OpenSuperWhisper.Tests;

public class CaretLocatorTests
{
    private sealed class StubStrategy(string name, AnchorPoint? result, TimeSpan delay = default)
        : ICaretStrategy
    {
        public int Calls { get; private set; }

        public string Name => name;

        public AnchorPoint? TryLocate()
        {
            Calls++;
            if (delay > TimeSpan.Zero) Thread.Sleep(delay);
            return result;
        }
    }

    private sealed class ThrowingStrategy : ICaretStrategy
    {
        public string Name => "throws";

        public AnchorPoint? TryLocate() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void FirstStrategyWithAnAnswer_Wins()
    {
        var first = new StubStrategy("first", new AnchorPoint(10, 20, "first"));
        var second = new StubStrategy("second", new AnchorPoint(99, 99, "second"));

        var anchor = new CaretLocator([first, second]).Resolve();

        Assert.Equal("first", anchor.Source);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public void StrategyReturningNull_FallsThroughToNext()
    {
        // The normal case, not an edge case: most applications expose a caret to at
        // most one of these mechanisms.
        var first = new StubStrategy("first", null);
        var second = new StubStrategy("second", new AnchorPoint(5, 6, "second"));

        var anchor = new CaretLocator([first, second]).Resolve();

        Assert.Equal("second", anchor.Source);
        Assert.Equal(1, first.Calls);
    }

    [Fact]
    public void ThrowingStrategy_DoesNotAbortResolution()
    {
        var locator = new CaretLocator([new ThrowingStrategy(), new StubStrategy("ok", new AnchorPoint(1, 2, "ok"))]);

        Assert.Equal("ok", locator.Resolve().Source);
    }

    [Fact]
    public void NoStrategySucceeds_FallsBackToMouse()
    {
        // The fallback always produces something, so callers never handle a null anchor.
        var anchor = new CaretLocator([new StubStrategy("none", null)]).Resolve();

        Assert.Contains(anchor.Source, new[] { "mouse", "origin" });
    }

    [Fact]
    public void NoStrategiesAtAll_StillResolves()
    {
        var anchor = new CaretLocator([]).Resolve();

        Assert.Contains(anchor.Source, new[] { "mouse", "origin" });
    }

    [Fact]
    public void SlowStrategy_StopsLaterOnesFromRunning()
    {
        // The budget's purpose: a hung application must not delay the indicator
        // indefinitely by holding up the strategies queued behind it.
        var slow = new StubStrategy("slow", null, CaretLocator.Budget + TimeSpan.FromMilliseconds(80));
        var never = new StubStrategy("never", new AnchorPoint(1, 1, "never"));

        var clock = Stopwatch.StartNew();
        var anchor = new CaretLocator([slow, never]).Resolve();
        clock.Stop();

        Assert.Equal(0, never.Calls);
        Assert.Contains(anchor.Source, new[] { "mouse", "origin" });

        // One in-flight strategy can overrun; the guarantee is that the queue stops.
        Assert.True(clock.Elapsed < CaretLocator.Budget * 3,
            $"Resolution took {clock.ElapsedMilliseconds} ms, well past the budget.");
    }

    // =====================================================================
    // Rectangle plausibility
    // =====================================================================

    [Fact]
    public void AllZeroRect_IsRejected()
    {
        // What applications report when they have no caret at all. Accepting it would
        // pin the indicator to the top-left corner of the primary monitor.
        Assert.False(CaretLocator.IsPlausibleCaret(new Win32Window.Rect()));
    }

    [Fact]
    public void ZeroWidthRect_IsAccepted()
    {
        // A caret with nothing selected legitimately has zero width - it is a thin bar.
        var caret = new Win32Window.Rect { Left = 100, Top = 200, Right = 100, Bottom = 218 };

        Assert.True(CaretLocator.IsPlausibleCaret(caret));
    }

    [Fact]
    public void ZeroHeightRect_IsRejected()
    {
        var flat = new Win32Window.Rect { Left = 100, Top = 200, Right = 101, Bottom = 200 };

        Assert.False(CaretLocator.IsPlausibleCaret(flat));
    }

    [Fact]
    public void AbsurdCoordinates_AreRejected()
    {
        var nonsense = new Win32Window.Rect
        {
            Left = 500_000, Top = 200, Right = 500_001, Bottom = 218,
        };

        Assert.False(CaretLocator.IsPlausibleCaret(nonsense));
    }

    [Fact]
    public void NegativeCoordinates_AreAccepted()
    {
        // A monitor arranged left of or above the primary has negative coordinates.
        // Rejecting those would break multi-monitor setups.
        var secondary = new Win32Window.Rect
        {
            Left = -1200, Top = -300, Right = -1200, Bottom = -282,
        };

        Assert.True(CaretLocator.IsPlausibleCaret(secondary));
    }

    [Fact]
    public void Win32Strategy_DoesNotThrowAgainstLiveDesktop()
    {
        // Whether anything is found depends on what happens to be focused; the point is
        // that the struct layout and ClientToScreen call are sound.
        var strategy = new Win32CaretStrategy();

        var exception = Record.Exception(() => strategy.TryLocate());

        Assert.Null(exception);
    }
}
