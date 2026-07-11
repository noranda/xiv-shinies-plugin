using System;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// Renders elapsed time the way a person says it.
/// </summary>
/// <remarks>
/// Pure, so the boundary choices (when does "just now" become "1 minute ago"?) are pinned by tests
/// rather than discovered by users. Deliberately coarse: a sync status line needs "about how long",
/// not a stopwatch.
/// </remarks>
public static class TimeText
{
    /// <summary>Formats an elapsed duration as "just now", "N minutes ago", or "N hours ago".</summary>
    /// <param name="elapsed">How long ago it happened. A negative value reads as "just now".</param>
    public static string Ago(TimeSpan elapsed)
    {
        // A clock that jumped backwards can hand us a negative duration; "just now" is the honest
        // rendering of "we cannot say".
        if (elapsed < TimeSpan.FromMinutes(1))
            return "just now";

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        var hours = (int)elapsed.TotalHours;
        return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
    }

    /// <summary>
    /// Formats a recurring cadence as "30 minutes", "1 hour", "90 minutes" — for sentences like
    /// "syncs automatically every {Interval}".
    /// </summary>
    /// <remarks>
    /// The server tunes the sync interval at runtime, so this cannot be a hardcoded string. Whole
    /// hours read as hours; anything else reads as minutes, because "1 hour 30 minutes" is longer
    /// than the number it explains.
    /// </remarks>
    public static string Interval(TimeSpan interval)
    {
        // Sub-minute values cannot reach this from the scheduler (it clamps at five minutes), but
        // a formatter that can print "0 minutes" eventually will.
        var minutes = Math.Max(1, (int)Math.Round(interval.TotalMinutes));

        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
