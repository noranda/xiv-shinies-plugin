using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Chooses which collectors an upload should run.
/// </summary>
/// <remarks>
/// <para>
/// Split out of the orchestrator so it can be unit-tested. The orchestrator holds Dalamud services
/// and cannot be constructed outside the game; this cannot fail to be testable, because it knows
/// nothing but a list and a set of keys.
/// </para>
/// <para>
/// It matters more than its size suggests. An unlock upload names the categories it carries, and the
/// server dates them from that. Selecting one category too many would attach an acquisition date to
/// a collection the player did not just acquire, and dates are never revised.
/// </para>
/// </remarks>
public static class CollectorSelection
{
    /// <summary>The collectors to run for one upload.</summary>
    /// <param name="collectors">Every registered collector.</param>
    /// <param name="categories">
    /// The categories to restrict to, or <c>null</c> for "all of them". Null is how a full sweep is
    /// expressed; an empty set is not the same thing and correctly selects nothing.
    /// </param>
    /// <remarks>
    /// A key naming no registered collector simply matches nothing. That is deliberate: a category
    /// the plugin no longer ships must not throw, it must be ignored.
    /// </remarks>
    public static IEnumerable<ICollector> For(
        IEnumerable<ICollector> collectors, IReadOnlySet<string>? categories) =>
        categories is null
            ? collectors
            : collectors.Where(collector => categories.Contains(collector.CategoryKey));
}
