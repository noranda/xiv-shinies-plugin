using System.Collections.Generic;

namespace XIVShinies.SyncPlugin.Api;

/// <summary>
/// The 200 body of <c>POST /api/plugin/v1/sync</c>.
/// </summary>
/// <remarks>
/// The server omits optional keys rather than sending them as null, so the plugin can
/// feature-detect them. That is why they are nullable here: null means "the server did not send
/// this", which is exactly the signal we want.
/// </remarks>
public sealed record SyncResponse
{
    /// <summary>Always true on a 200.</summary>
    public required bool Ok { get; init; }

    /// <summary>True only when THIS request performed the first-upload character bind.</summary>
    public required bool Bound { get; init; }

    /// <summary>Rows created plus promoted, per id-list category. Always all four keys.</summary>
    public required WrittenCounts Written { get; init; }

    /// <summary>
    /// Present only when the achievements key was absent or stripped as disabled. An explicit
    /// empty array counts as "sent" and gets no flag.
    /// </summary>
    public string? AchievementsSkipped { get; init; }

    /// <summary>
    /// Present only when items were applied and relic-proof derivation succeeded. Absent on an
    /// items-carrying upload means derivation failed server-side; the next upload retries it.
    /// </summary>
    public int? ProvenSteps { get; init; }

    /// <summary>
    /// Present only when the server stripped disabled categories from this payload. Lets the
    /// plugin tell the user why a category did not sync.
    /// </summary>
    public IReadOnlyList<string>? SkippedCategories { get; init; }
}

/// <summary>
/// Rows created plus promoted per category. Note <c>items</c> never appears here — item
/// possession feeds relic proofs rather than a collection count.
/// </summary>
public sealed record WrittenCounts
{
    /// <summary>Achievement rows written.</summary>
    public required int Achievements { get; init; }

    /// <summary>Minion rows written.</summary>
    public required int Minions { get; init; }

    /// <summary>Mount rows written.</summary>
    public required int Mounts { get; init; }

    /// <summary>Quest rows written.</summary>
    public required int Quests { get; init; }
}
