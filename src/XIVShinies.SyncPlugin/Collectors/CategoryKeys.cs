namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// The category keys the server contract defines. A collector announces one of these as its
/// <see cref="ICollector.CategoryKey"/>, and the same string is used for the user's opt-in toggle,
/// the server's kill switch, and the payload key.
/// </summary>
/// <remarks>
/// Naming keys here is not a category-name branch: nothing reads these constants to decide
/// behavior. Only the collectors themselves use them, to say what they are.
/// </remarks>
public static class CategoryKeys
{
    /// <summary>Completed quest IDs.</summary>
    public const string Quests = "quests";

    /// <summary>Completed achievement IDs.</summary>
    public const string Achievements = "achievements";

    /// <summary>Unlocked mount IDs.</summary>
    public const string Mounts = "mounts";

    /// <summary>Unlocked minion (Companion) IDs.</summary>
    public const string Minions = "minions";

    /// <summary>Possession counts for the items the server asked about.</summary>
    public const string Items = "items";
}
