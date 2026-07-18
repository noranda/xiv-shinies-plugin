using System.Collections.Generic;
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

            // Per-source scan status, or null when there is nothing to send. An empty object on
            // the wire is noise when no source status is worth reporting; null makes the shared
            // serializer policy (ApiJson.Options omits null properties) drop the key entirely.
            ItemSources = BuildWireSourceNotes(snapshot.SourceNotes),
        };
    }

    /// <summary>
    /// The source notes that belong on the wire, or null when none do.
    /// </summary>
    /// <remarks>
    /// Source status exists to make counts judgeable — "was the saddlebag ever scanned?" — so a
    /// source in the <see cref="SourceStates.Unreadable"/> state is dropped here: the game never
    /// exposes it, it can never carry counts, and repeating that constant on every upload would be
    /// exactly the noise the minimize-what-you-send rule forbids. The state is kept for the
    /// settings panel, which is where a user wondering about such a source actually looks. Keyed
    /// on the STATE, never on a source name, so any future unreadable source stays local the same
    /// way.
    /// </remarks>
    private static Dictionary<string, ItemSourceStatus>? BuildWireSourceNotes(
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes)
    {
        // Null until something wire-worthy appears, so the "no notes at all" and the "only local
        // notes" cases converge on the same omitted key without a second emptiness check.
        Dictionary<string, ItemSourceStatus>? wireNotes = null;

        foreach (var (sourceKey, status) in sourceNotes)
        {
            if (status.State == SourceStates.Unreadable)
                continue;

            // `??=` assigns only when the left side is still null — the dictionary is created on
            // the first wire-worthy note and reused for the rest.
            wireNotes ??= new Dictionary<string, ItemSourceStatus>();
            wireNotes[sourceKey] = status;
        }

        return wireNotes;
    }
}
