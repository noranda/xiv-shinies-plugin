using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Turns one collection pass into the body of a sync upload.
/// </summary>
/// <remarks>
/// Deliberately dumb. It reports exactly the categories the snapshot read — no more, no fewer — and
/// invents nothing. A category the collectors skipped is simply absent, which the server reads as
/// "not read this time" rather than "empty", so a partial upload can never erase anything.
/// </remarks>
public static class SyncPayloadBuilder
{
    /// <summary>Builds the request body.</summary>
    /// <param name="identity">The local character, already hashed.</param>
    /// <param name="pluginVersion">This plugin's version.</param>
    /// <param name="trigger">What prompted the upload.</param>
    /// <param name="snapshot">What the collectors read.</param>
    /// <param name="manifestVersion">
    /// The <c>/config</c> manifest version the item list was built against, echoed back so the
    /// server can record it. Null when no config has been fetched, and then omitted from the JSON.
    /// </param>
    public static SyncRequest Build(
        CharacterIdentity identity,
        string pluginVersion,
        SyncTrigger trigger,
        CollectionSnapshot snapshot,
        string? manifestVersion)
    {
        return new SyncRequest
        {
            CharacterContentIdHash = identity.ContentIdHash,

            // The server trims and length-checks these; sending untrimmed input would fail
            // validation and take the whole upload down with it.
            CharacterName = identity.Name.Trim(),
            HomeWorld = identity.HomeWorld.Trim(),

            PluginVersion = pluginVersion,
            Trigger = trigger,
            ManifestVersion = manifestVersion,

            // Handed straight through. Whichever categories the collectors read, and only those.
            Collections = snapshot.Collections,
        };
    }
}
