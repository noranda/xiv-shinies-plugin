using System;
using Xunit;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The "last synced …" wording. Pinning the boundaries here means "59 seconds" vs "1 minute" is a
// decision, not an accident.
public class TimeTextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    public void Under_a_minute_is_just_now(int seconds)
    {
        Assert.Equal("just now", TimeText.Ago(TimeSpan.FromSeconds(seconds)));
    }

    // Clocks jump backwards (NTP corrections, laptops waking). "Just now" is the honest rendering
    // of an elapsed time we cannot actually compute.
    [Fact]
    public void A_negative_duration_reads_as_just_now()
    {
        Assert.Equal("just now", TimeText.Ago(TimeSpan.FromSeconds(-45)));
    }

    [Fact]
    public void Exactly_one_minute_is_singular()
    {
        Assert.Equal("1 minute ago", TimeText.Ago(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(2, "2 minutes ago")]
    [InlineData(30, "30 minutes ago")]
    [InlineData(59, "59 minutes ago")]
    public void Minutes_are_plural_and_truncated(int minutes, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(TimeSpan.FromMinutes(minutes)));
    }

    // 90 seconds is "1 minute ago", not "2": truncation, never rounding up — the sync did not
    // happen further back than it did.
    [Fact]
    public void Partial_minutes_truncate_down()
    {
        Assert.Equal("1 minute ago", TimeText.Ago(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void Exactly_one_hour_is_singular()
    {
        Assert.Equal("1 hour ago", TimeText.Ago(TimeSpan.FromHours(1)));
    }

    // Same truncation rule as minutes: 90 minutes is "1 hour ago", never rounded up to 2.
    [Fact]
    public void Partial_hours_truncate_down()
    {
        Assert.Equal("1 hour ago", TimeText.Ago(TimeSpan.FromMinutes(90)));
    }

    [Theory]
    [InlineData(2, "2 hours ago")]
    [InlineData(26, "26 hours ago")]
    public void Hours_are_plural_and_unbounded(int hours, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(TimeSpan.FromHours(hours)));
    }

    // The sync cadence shown in the settings ("syncs automatically every 30 minutes"). The server
    // tunes the interval at runtime, so the wording must survive any value the clamp allows.
    [Theory]
    [InlineData(5, "5 minutes")]
    [InlineData(30, "30 minutes")]
    [InlineData(90, "90 minutes")]
    public void Intervals_in_minutes_read_as_minutes(int minutes, string expected)
    {
        Assert.Equal(expected, TimeText.Interval(TimeSpan.FromMinutes(minutes)));
    }

    // Whole hours read as hours: "every 120 minutes" is technically true but nobody says it.
    [Theory]
    [InlineData(1, "1 hour")]
    [InlineData(2, "2 hours")]
    [InlineData(24, "24 hours")]
    public void Whole_hour_intervals_read_as_hours(int hours, string expected)
    {
        Assert.Equal(expected, TimeText.Interval(TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void A_one_minute_interval_is_singular()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromMinutes(1)));
    }

    // Defensive floor: an interval this small never reaches the UI (the scheduler clamps at five
    // minutes), but the formatter should still say something sane rather than "0 minutes".
    [Fact]
    public void Intervals_below_a_minute_read_as_one_minute()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromSeconds(10)));
    }

    // Same floor from further below: a negative interval is nonsense the scheduler never
    // produces, but the formatter must not render nonsense of its own ("-5 minutes").
    [Fact]
    public void Negative_intervals_also_floor_to_one_minute()
    {
        Assert.Equal("1 minute", TimeText.Interval(TimeSpan.FromMinutes(-5)));
    }
}
