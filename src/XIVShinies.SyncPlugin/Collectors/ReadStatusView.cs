using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One assembled "Reading from:" panel: the collection lines and the container lines, kept apart so the
/// window can head each group with its own label.
/// </summary>
/// <remarks>
/// The two groups answer different questions, which is why they are two lists and not one. A
/// <b>collection</b> line answers "can the sync see this collection at all?"; a <b>container</b> line
/// answers "and where did the item counts come from?". A reader who cannot tell which kind of line they
/// are looking at cannot act on it, and the shape of this record is what keeps the two apart without
/// anything downstream having to rely on their order.
/// </remarks>
public sealed record ReadStatus
{
    /// <summary>One line per collection worth reporting on, in the order its row was registered.</summary>
    public required IReadOnlyList<SourceNote> Collections { get; init; }

    /// <summary>
    /// One line per storage container the item pass looked at (inventory, saddlebag, armoire, …), in
    /// whatever order the pass reported them.
    /// </summary>
    public required IReadOnlyList<SourceNote> Containers { get; init; }
}

/// <summary>
/// Assembles every line of the settings window's "Reading from:" panel: what the sync managed to read
/// this session, and what it did not.
/// </summary>
/// <remarks>
/// <para>
/// The panel answers one question — "is the plugin actually seeing my collections?" — and that
/// question has two halves. A <b>collection</b> can fail to be read at all (the game will not answer
/// for achievements until the player has opened their Achievements window once), and a <b>storage
/// container</b> the item counts are drawn from can be stale or never opened (the saddlebag, a
/// never-summoned retainer). Both halves are reported here, as the two lists of
/// <see cref="SourceNote"/> on a <see cref="ReadStatus"/> which the window prints without interpreting.
/// </para>
/// <para>
/// Pure and Dalamud-free, which is what makes the <b>extensibility contract testable</b>: a row for a
/// category this plugin has never heard of must flow through and produce a line. There is no table of
/// category names here and no table of source names — a collection's line is built from the row the
/// collector produced, and a container's line comes from <see cref="SourceNoteText.Describe"/>.
/// Adding a collection, or a storage container, must require no change to this file.
/// </para>
/// <para>
/// The two hint helpers this leans on both return null for something not worth saying, and the two
/// nulls mean different things — which is why they are handled differently below.
/// <see cref="CollectSkipReasons.Describe"/> returning null means "there is no advice for this reason",
/// but the collection was still missed, so a line is still owed; the fallback copy supplies it.
/// <see cref="SourceNoteText.Describe"/> returning null means "there is nothing to report about this
/// container at all", so that entry is dropped entirely.
/// </para>
/// </remarks>
public static class ReadStatusView
{
    /// <summary>
    /// Every line the panel should draw, split into its collection lines and its container lines.
    /// </summary>
    /// <param name="rows">
    /// The settings window's category rows — the same list the consent card is drawn from (see
    /// <see cref="CategorySettingsView.Build"/>). Only rows that are
    /// <see cref="CategorySettingsRow.IsEffectivelyOn"/> produce a line: a collection the user (or the
    /// server) switched off is not being uploaded by choice, so it has no read status worth reporting,
    /// and a "not read" line beside it would read as a fault rather than as a decision.
    /// </param>
    /// <param name="sourceNotes">
    /// Per-container scan status from the most recent item pass, keyed by <see cref="SourceKeys"/>.
    /// Empty before any pass has looked at the item sources, which simply means no container lines.
    /// </param>
    public static ReadStatus Build(
        IReadOnlyList<CategorySettingsRow> rows,
        IReadOnlyDictionary<string, ItemSourceStatus> sourceNotes)
    {
        var collections = new List<SourceNote>(rows.Count);
        var containers = new List<SourceNote>(sourceNotes.Count);

        // The containers are only ever looked at on behalf of a manifest-driven collection, so with
        // every such collection switched off they are not being read at all — and the scan status of a
        // container nothing consults describes nothing. Left in, those lines would keep urging the user
        // to open their saddlebag for a scan that is no longer happening.
        var anyManifestCollectionOn = false;
        foreach (var row in rows)
        {
            if (row.UsesItemManifest && row.IsEffectivelyOn)
            {
                anyManifestCollectionOn = true;
                break;
            }
        }

        // --- Storage containers ---------------------------------------------------------------
        // Built FIRST, because the collection lines below need to know whether this group ended up
        // with anything in it — see DescribeCollection's manifest-driven rule.
        //
        // `foreach` over an IReadOnlyDictionary hands back each entry as a KeyValuePair, which
        // deconstructs straight into (key, value) here.
        if (anyManifestCollectionOn)
        {
            foreach (var (sourceKey, status) in sourceNotes)
            {
                // `is not { } note` reads as "is null": skip a container this copy set has no line for,
                // rather than printing a raw wire string such as "unscanned" at the user.
                if (SourceNoteText.Describe(sourceKey, status) is not { } note)
                    continue;

                containers.Add(note);
            }
        }

        // --- Collections ---------------------------------------------------------------------
        foreach (var row in rows)
        {
            if (!row.IsEffectivelyOn)
                continue;

            // `is { } note` reads as "is not null": a row with nothing to add to the panel is dropped
            // rather than drawn — see DescribeCollection for the one case where that happens.
            if (DescribeCollection(row, containers.Count > 0) is { } note)
                collections.Add(note);
        }

        return new ReadStatus { Collections = collections, Containers = containers };
    }

    /// <summary>
    /// One collection's note — whether the last pass could read it, and what to do if not — or null
    /// when this collection has nothing to add to the panel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A collection that was read is a healthy chip: the label is the collection's own display name,
    /// and the chip's green check is the whole message, so there is no sentence to carry. A collection
    /// that was missed is a full line whose text completes the pattern
    /// "<c>{DisplayName}: {phrase}</c>" — which is why every string in
    /// <see cref="CollectSkipReasons.Describe"/> is written as a phrase rather than a standalone
    /// sentence. Nothing here knows which collection it is describing: the name comes from the row and
    /// the phrase comes from the reason the collector itself reported.
    /// </para>
    /// <para>
    /// The null case is the manifest-driven rule. A manifest-driven collection's facts ARE the item
    /// counts read out of the containers, so when it has no skip reason its own line says nothing the
    /// container group below does not already say in more detail — and a line that only repeats its
    /// neighbours teaches the reader to skim past both. It is dropped only while there is at least one
    /// container line to stand in for it: no pass has reported yet, or every status it did report was
    /// one this copy set has no line for, and dropping this line as well would leave the panel silent
    /// about a collection the user has switched on. A <i>skipped</i> manifest-driven collection is a
    /// third case: why it was missed (the server's config has not arrived, the inventory is unreadable,
    /// no consent group beneath it is switched on) exists nowhere else in the panel, so that line is
    /// always owed. Keyed on the collector's own <see cref="CategorySettingsRow.UsesItemManifest"/>
    /// flag, never on a category name — a future manifest-driven collection inherits the rule for free.
    /// </para>
    /// </remarks>
    /// <param name="row">The category row this line is about.</param>
    /// <param name="hasContainerLines">
    /// Whether the panel's container group ended up with at least one line in it — the thing a
    /// suppressed manifest-driven collection is being suppressed in favour of. Asked about the LINES
    /// rather than the raw statuses: a status <see cref="SourceNoteText.Describe"/> has no copy for is
    /// dropped from the panel, so it cannot stand in for anything the reader can actually see.
    /// </param>
    private static SourceNote? DescribeCollection(CategorySettingsRow row, bool hasContainerLines)
    {
        // No skip reason means the pass read this collection — the healthy resting state, with nothing
        // for the user to do, so the note is a chip carrying only the collection's name.
        if (row.SkipReason is not { } reason)
        {
            return row.UsesItemManifest && hasContainerLines
                ? null
                : new SourceNote { Label = row.DisplayName, Tone = SourceTone.Live };
        }

        // A reason with advice: the collection was missed AND the user can do something about it.
        if (CollectSkipReasons.Describe(reason) is { } hint)
        {
            return new SourceNote
            {
                Label = row.DisplayName,
                Text = $"{row.DisplayName}: {hint}",
                Tone = SourceTone.Missing,
            };
        }

        // A reason with no advice — a collector bug, an unloadable game sheet. The collection was still
        // missed, so the panel must say so; it just has no action to offer alongside it.
        return new SourceNote
        {
            Label = row.DisplayName,
            Text = $"{row.DisplayName}: could not be read.",
            Tone = SourceTone.Missing,
        };
    }
}
