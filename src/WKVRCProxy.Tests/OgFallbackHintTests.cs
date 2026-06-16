using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class OgFallbackHintTests
{
    [Fact]
    public void ShouldPreferOg_DefaultsFalse_WhenSourceNeverFailed()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        Assert.False(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void ShouldPreferOg_TrueWithinTtl_AfterRecordedFailure()
    {
        var clock = new TestClock(new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc));
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");

        Assert.True(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void ShouldPreferOg_FalseAfterTtl_DroppedFromMap()
    {
        var clock = new TestClock(new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc));
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
        // Expired entry should be pruned on read so the map stays small
        // without an external sweep.
        Assert.Equal(0, hint.LiveEntryCountForTests());
    }

    [Fact]
    public void RecordLoadFailure_ReArmsExpiry_OnRepeatedFailure()
    {
        var clock = new TestClock(new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc));
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");
        clock.Advance(TimeSpan.FromSeconds(45));
        // Second failure within the window pushes TTL back out 60 s from now.
        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");

        clock.Advance(TimeSpan.FromSeconds(45)); // now 90 s after first failure
        Assert.True(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void RecordLoadFailure_IsKeyedBySourceUrl_NotResolvedUrl()
    {
        var clock = new TestClock(new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc));
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");

        // A different source URL does not get the og hint just because some
        // other source recently failed -- avoids cross-contamination between
        // unrelated playbacks.
        Assert.False(hint.ShouldPreferOg("https://www.youtube.com/watch?v=def"));
        Assert.True(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void TryClear_RemovesActiveEntry()
    {
        var clock = new TestClock(new DateTime(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc));
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("https://www.youtube.com/watch?v=abc");
        Assert.True(hint.TryClear("https://www.youtube.com/watch?v=abc"));
        Assert.False(hint.ShouldPreferOg("https://www.youtube.com/watch?v=abc"));
        Assert.False(hint.TryClear("https://www.youtube.com/watch?v=abc"));
    }

    [Fact]
    public void RecordLoadFailure_EmptyOrNullSourceUrl_NoOp()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var hint = new OgFallbackHint(TimeSpan.FromSeconds(60), clock.Now);

        hint.RecordLoadFailure("");
        hint.RecordLoadFailure(null!);

        Assert.Equal(0, hint.LiveEntryCountForTests());
        Assert.False(hint.ShouldPreferOg(""));
    }

    [Fact]
    public void DefaultTtl_IsSixtySeconds()
    {
        // Pinned: this is the user-visible recovery window between an
        // observed AVPro failure and the og hint expiring.
        var hint = new OgFallbackHint();
        Assert.Equal(TimeSpan.FromSeconds(60), hint.Ttl);
    }

    private sealed class TestClock
    {
        private DateTime _now;
        public TestClock(DateTime initialUtc) => _now = initialUtc;
        public DateTime Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
