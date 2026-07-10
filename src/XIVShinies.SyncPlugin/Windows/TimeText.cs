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
}
