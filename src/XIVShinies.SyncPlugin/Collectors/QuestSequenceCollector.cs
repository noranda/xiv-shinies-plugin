using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Reports which step of each server-requested quest the local character's journal is currently
/// on — the quest's <b>sequence</b>, a single byte the game advances as objectives complete.
/// </summary>
/// <remarks>
/// <para>
/// Only the quests in the server's <c>questSequenceManifest</c> are ever looked at. The server
/// asks about quests with several sequential turn-ins (hand in a batch of items, then a larger
/// batch, and so on inside ONE quest): the completed-quests category only fires when the whole
/// chain is done, so mid-chain progress is invisible without this. Knowing the sequence lets the
/// server credit the batches already handed over.
/// </para>
/// <para>
/// The sequence values are opaque bytes the game defines per quest — they are not step numbers
/// like 1, 2, 3. The plugin reports them raw and uninterpreted; the server's curated tables know
/// which byte proves which turn-in. That division is what keeps this collector a dumb fact-reader.
/// </para>
/// <para>
/// A quest appears in the result only while it is in the journal. Absence is no-information: the
/// server cannot tell "never started" from "abandoned" from "completed" here, and must keep any
/// credit it has already derived (a turned-in batch's items are gone regardless of what the
/// journal does later).
/// </para>
/// <para>
/// Reads game memory through FFXIVClientStructs, so it must run on the framework thread and cannot
/// be unit-tested; it is verified by in-game QA. The pure logic around it — the manifest funnel
/// (<see cref="CollectContext.QuestSequenceManifest"/>) and the payload shape
/// (<see cref="CollectResult.Sequences"/>) — is covered by tests.
/// </para>
/// <para>
/// Reads only the <b>local</b> player's journal. It never touches the object table or any other
/// character, which is a hard Dalamud rule.
/// </para>
/// </remarks>
// `unsafe` allows raw pointers. FFXIVClientStructs maps the game's own memory layout, so its
// Instance() methods hand back pointers into the live game rather than managed objects. C# normally
// forbids this; the keyword is the explicit opt-in. There is no JS equivalent whatsoever.
public sealed unsafe class QuestSequenceCollector : ICollector
{
    private readonly IFramework framework;

    // How this collection names and describes itself to the user.
    private readonly CategoryInfo info;

    /// <summary>Creates the collector.</summary>
    /// <param name="info">
    /// The category's wire key and its user-facing copy. Passed in from the registry rather than
    /// hardcoded here, so that every category is described in exactly one file.
    /// </param>
    /// <param name="framework">Used to verify we are on the framework thread before reading.</param>
    public QuestSequenceCollector(CategoryInfo info, IFramework framework)
    {
        this.info = info;
        this.framework = framework;
    }

    /// <inheritdoc/>
    public string CategoryKey => info.Key;

    /// <inheritdoc/>
    public string DisplayName => info.DisplayName;

    /// <inheritdoc/>
    public string WhatGetsSent => info.WhatGetsSent;

    /// <inheritdoc/>
    public bool UsesItemManifest => info.UsesItemManifest;

    /// <inheritdoc/>
    public CollectResult Collect(CollectContext context)
    {
        // Reading game memory off the framework thread races the game's own writes, and the
        // resulting access violation cannot be caught — so refuse.
        GameThread.EnsureFrameworkThread(framework, nameof(QuestSequenceCollector));

        // Without a config we do not know which quests the server cares about. That is "could not
        // read", not "found nothing" — so skip rather than send an empty result.
        if (context.RemoteConfig is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        // A server that does not send the field (an older server is a supported peer) has equally
        // never said what to look for — and would strip the category from the payload anyway.
        // Distinct from an EMPTY manifest below, which is a real answer.
        if (context.RemoteConfig.QuestSequenceManifest is null)
            return CollectResult.Skipped(CollectSkipReasons.NoRemoteConfig);

        var questManager = QuestManager.Instance();
        if (questManager is null)
            return CollectResult.Skipped(CollectSkipReasons.CollectorError);

        // An empty manifest is a real answer: the server asked about nothing, so we found nothing.
        // The bounded funnel also caps a hostile backend's manifest, same as the item manifest.
        var sequences = new Dictionary<uint, byte>();
        foreach (var questId in context.QuestSequenceManifest)
        {
            // The game stores active quests by the low 16 bits of the Excel row id; both calls
            // below mask the full row id down themselves. IsQuestAccepted gates presence in the
            // journal — GetQuestSequence alone returns 0 for an absent quest, which would be
            // indistinguishable from a genuinely-at-step-zero quest the player just picked up.
            if (questManager->IsQuestAccepted(questId))
                sequences[questId] = QuestManager.GetQuestSequence(questId);
        }

        return CollectResult.Sequences(sequences);
    }
}
