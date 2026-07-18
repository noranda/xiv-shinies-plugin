using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// ReadStatusView assembles the settings window's whole "Reading from:" panel: the collections the sync
// will actually upload, and the storage containers the item counts are drawn from, returned as two
// separate groups so the window can label each. It is pure and Dalamud-free, so every note the panel can
// show is pinned here rather than only seen in game — and, like CategorySettingsView, it must contain no
// category-name or source-name branch, which is what these tests exist to enforce.
//
// A note's tone picks its rendered form (see SourceNote): healthy notes are chips whose Label is their
// entire visible text, notes needing an in-game action are full lines carried in Text.
public class ReadStatusViewTests
{
    // A category this plugin has never heard of — the extensibility gate below rides on it.
    private const string UnknownCategory = "facewear";

    private static CategorySettingsRow Row(
        string key,
        bool userEnabled = true,
        bool serverEnabled = true,
        string? skipReason = null,
        bool usesItemManifest = false) => new()
    {
        Key = key,
        DisplayName = $"{key} display",
        WhatGetsSent = $"what {key} sends",
        UserEnabled = userEnabled,
        ServerEnabled = serverEnabled,
        SkipReason = skipReason,
        UsesItemManifest = usesItemManifest,
    };

    private static ItemSourceStatus Status(string state) => new() { State = state };

    private static IReadOnlyDictionary<string, ItemSourceStatus> NoSources() =>
        new Dictionary<string, ItemSourceStatus>();

    // One container the panel has copy for, so the container group is non-empty. Every suppression
    // test needs this: a manifest-driven collection's note is only dropped when there is a container
    // note left standing to report in its place.
    private static IReadOnlyDictionary<string, ItemSourceStatus> OneSource() =>
        new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

    // The containers are read on behalf of a manifest-driven collection, so any test that expects
    // container notes must have one of those collections switched on for them to belong to.
    private static CategorySettingsRow ManifestRow(string key = "items") =>
        Row(key, usesItemManifest: true);

    // An unreadable source (mannequins) reaches the panel as a container chip like any other note
    // the copy set describes — Build has no tone branch that could drop it, and this pins that.
    [Fact]
    public void An_unreadable_source_flows_through_as_a_container_chip()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Mannequins] = Status(SourceStates.Unreadable),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources);

        var note = Assert.Single(status.Containers);
        Assert.Equal("Mannequins", note.Label);
        Assert.Equal(SourceTone.Unreadable, note.Tone);
        Assert.Null(note.Text);
        Assert.NotNull(note.Detail);
    }

    // Container notes keep the order the item pass reported them in — the collector reports the
    // unreadable mannequins source last on purpose, so the one chip nothing can change sits at the
    // end of the row rather than among the working sources.
    [Fact]
    public void Container_notes_keep_the_order_the_sources_were_reported_in()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
            [SourceKeys.Saddlebag] = Status(SourceStates.Cached),
            [SourceKeys.Mannequins] = Status(SourceStates.Unreadable),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources);

        Assert.Equal(
            new[] { "Inventory", "Saddlebag", "Mannequins" },
            status.Containers.Select(note => note.Label));
    }

    // A collection that was read this pass has nothing for the user to do, so it is a chip: the
    // green check IS "read", and the label is the collection's own display name. No sentence and no
    // hover copy — there is nothing more to say about a healthy collection.
    [Fact]
    public void An_enabled_collection_that_was_read_is_a_chip_labelled_with_its_name()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts") }, NoSources());

        var note = Assert.Single(status.Collections);
        Assert.Equal("mounts display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
        Assert.Null(note.Text);
        Assert.Null(note.Detail);
    }

    // The mechanism that carries the "open your Achievements window once" hint into the panel: the
    // collector reported a reason, CollectSkipReasons turned it into advice, and this appends it after
    // the category's own display name. Nothing here names achievements. The action must stay visible,
    // so the note is a full line, never a chip.
    [Fact]
    public void A_collection_skipped_for_a_described_reason_appends_that_reasons_hint()
    {
        var rows = new[] { Row("achievements", skipReason: CollectSkipReasons.AchievementListNotLoaded) };

        var note = Assert.Single(ReadStatusView.Build(rows, NoSources()).Collections);

        Assert.Equal("achievements display", note.Label);
        Assert.Equal(
            "achievements display: " + CollectSkipReasons.Describe(CollectSkipReasons.AchievementListNotLoaded),
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // A reason the user cannot act on (a collector bug, an unloadable game sheet) has no advice to
    // give. The note still has to appear — the collection genuinely did not make it into the upload —
    // so it falls back to a plain statement rather than leaking a raw wire string like "collector_error".
    [Fact]
    public void A_collection_skipped_for_an_undescribed_reason_says_only_that_it_could_not_be_read()
    {
        var rows = new[] { Row("mounts", skipReason: CollectSkipReasons.CollectorError) };

        var note = Assert.Single(ReadStatusView.Build(rows, NoSources()).Collections);

        Assert.Equal("mounts display: could not be read.", note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // A collection the user switched off has no status worth reporting: they chose not to sync it, so
    // saying it was not read would read as a fault rather than as their own decision.
    [Fact]
    public void A_collection_the_user_switched_off_gets_no_note_at_all()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts", userEnabled: false) }, NoSources());

        Assert.Empty(status.Collections);
    }

    // Same reasoning for the server's per-category kill switch: the settings card already explains the
    // grayed-out checkbox, and the collection is not being uploaded either way.
    [Fact]
    public void A_collection_the_server_switched_off_gets_no_note_at_all()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts", serverEnabled: false) }, NoSources());

        Assert.Empty(status.Collections);
    }

    // A healthy manifest-driven collection would only show a bare "read" chip — which the container
    // notes beneath it already say, in more detail, since the containers ARE where its items were
    // counted. So it is omitted rather than repeating them. The rule is keyed on the collector's own
    // UsesItemManifest flag, never on its category name.
    [Fact]
    public void A_manifest_driven_collection_that_was_read_gets_no_note_of_its_own()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true) }, OneSource());

        Assert.Empty(status.Collections);
    }

    // The suppression stands down when there are no container notes. Suppressing costs the reader
    // nothing only while a container note is there to report in the collection's place; when the
    // server's manifest is empty the pass opens no container and reports no source note at all, so
    // dropping the collection's note too would leave the panel silent about a collection the user has
    // switched on.
    [Fact]
    public void A_manifest_driven_collection_keeps_its_note_when_no_container_note_stands_in_for_it()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true) }, NoSources());

        var note = Assert.Single(status.Collections);
        Assert.Equal("items display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // The suppression asks whether a container NOTE exists, not whether a container status arrived: a
    // status SourceNoteText has no copy for is dropped from the panel, so it cannot stand in for
    // anything the reader can actually see.
    [Fact]
    public void A_manifest_driven_collection_keeps_its_note_when_every_container_status_was_dropped()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            ["facewearCabinet"] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { Row("items", usesItemManifest: true) }, sources);

        Assert.Empty(status.Containers);
        Assert.Equal("items display", Assert.Single(status.Collections).Label);
    }

    // The other half of that rule: a manifest-driven collection that was SKIPPED still gets its note.
    // Why it was missed (the config has not arrived, the inventory is unreadable) exists nowhere else in
    // the panel — the container notes cannot say it.
    [Fact]
    public void A_manifest_driven_collection_that_was_skipped_still_reports_its_hint()
    {
        var rows = new[]
        {
            Row("items", skipReason: CollectSkipReasons.NoRemoteConfig, usesItemManifest: true),
        };

        // With a container note present — the state that would suppress a HEALTHY manifest-driven
        // collection. A skipped one is owed its note regardless, because no container note explains why.
        var note = Assert.Single(ReadStatusView.Build(rows, OneSource()).Collections);

        Assert.Equal(
            "items display: " + CollectSkipReasons.Describe(CollectSkipReasons.NoRemoteConfig),
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }

    // The suppression is scoped to manifest-driven collections alone: an ordinary collection has no
    // container notes standing in for it, so its healthy chip is the only thing that says the sync can
    // see it at all.
    [Fact]
    public void A_collection_that_is_not_manifest_driven_keeps_its_chip()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts", usesItemManifest: false) }, NoSources());

        var note = Assert.Single(status.Collections);
        Assert.Equal("mounts display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // Storage containers get their copy from SourceNoteText; this view only groups the notes.
    [Fact]
    public void Storage_containers_are_described_by_the_existing_source_note_copy()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { ManifestRow() }, sources);

        var note = Assert.Single(status.Containers);
        Assert.Equal(
            SourceNoteText.Describe(SourceKeys.Inventory, Status(SourceStates.Live)),
            note);
    }

    // SourceNoteText returns null for a (source, state) pair it has no note for. A null means "nothing
    // worth saying", so the panel must drop it rather than show a raw wire string.
    [Fact]
    public void A_storage_container_with_nothing_worth_saying_is_dropped()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            ["facewearCabinet"] = Status(SourceStates.Live),
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var note = Assert.Single(ReadStatusView.Build(new[] { ManifestRow() }, sources).Containers);

        Assert.Equal("Inventory", note.Label);
    }

    // The two groups are returned separately so the window can head each with its own label. A
    // collection note must never end up among the containers, or vice versa.
    [Fact]
    public void Collections_and_containers_are_returned_as_separate_groups()
    {
        var sources = new Dictionary<string, ItemSourceStatus>
        {
            [SourceKeys.Inventory] = Status(SourceStates.Live),
        };

        var status = ReadStatusView.Build(new[] { Row("mounts"), ManifestRow() }, sources);

        Assert.Equal(new[] { "mounts display" }, status.Collections.Select(note => note.Label));
        Assert.Equal(new[] { "Inventory" }, status.Containers.Select(note => note.Label));
    }

    [Fact]
    public void Collection_notes_follow_the_order_of_the_rows_they_came_from()
    {
        var status = ReadStatusView.Build(new[] { Row("b"), Row("a") }, NoSources());

        Assert.Equal(
            new[] { "b display", "a display" },
            status.Collections.Select(note => note.Label));
    }

    [Fact]
    public void No_rows_and_no_sources_produce_no_notes_in_either_group()
    {
        var status = ReadStatusView.Build(new List<CategorySettingsRow>(), NoSources());

        Assert.Empty(status.Collections);
        Assert.Empty(status.Containers);
    }

    // THE GATE: a collection this plugin has never heard of gets a note built from its own row, proving
    // the panel holds no table of known category names.
    [Fact]
    public void A_collection_for_an_unknown_category_still_gets_a_note()
    {
        var status = ReadStatusView.Build(new[] { Row(UnknownCategory) }, NoSources());

        var note = Assert.Single(status.Collections);
        Assert.Equal("facewear display", note.Label);
        Assert.Equal(SourceTone.Live, note.Tone);
    }

    // THE GATE, extended to the suppression rule: a manifest-driven collector for a category this plugin
    // has never heard of is suppressed exactly like the one it ships with, proving the rule is keyed on
    // the self-reported flag rather than on any category name.
    [Fact]
    public void A_manifest_driven_collection_for_an_unknown_category_is_also_suppressed_when_read()
    {
        var status = ReadStatusView.Build(
            new[] { Row(UnknownCategory, usesItemManifest: true) }, OneSource());

        Assert.Empty(status.Collections);
    }

    // The containers are only ever opened on behalf of a manifest-driven collection. With every such
    // collection switched off, nothing is reading them — and a note urging the user to open their
    // saddlebag for a scan that is not happening is advice they cannot act on.
    [Fact]
    public void Container_notes_are_dropped_when_no_manifest_driven_collection_is_on()
    {
        var status = ReadStatusView.Build(new[] { Row("mounts") }, OneSource());

        Assert.Empty(status.Containers);
        Assert.Equal(new[] { "mounts display" }, status.Collections.Select(note => note.Label));
    }

    // The real shape of the rule: the user unticks the collection whose scan the containers exist for.
    // Its saddlebag and retainer notes have to go with it — they describe a scan that is not happening.
    [Fact]
    public void Container_notes_are_dropped_when_the_user_switches_the_manifest_collection_off()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", userEnabled: false, usesItemManifest: true) }, OneSource());

        Assert.Empty(status.Containers);
    }

    // Same when the server is the one that switched it off: nothing is reading the containers either way.
    [Fact]
    public void Container_notes_are_dropped_when_the_server_switches_the_manifest_collection_off()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", serverEnabled: false, usesItemManifest: true) }, OneSource());

        Assert.Empty(status.Containers);
    }

    // Switching the collection back on brings its containers back with it — the notes are dropped, not
    // forgotten.
    [Fact]
    public void Container_notes_return_once_a_manifest_driven_collection_is_on()
    {
        var status = ReadStatusView.Build(
            new[] { Row("mounts"), ManifestRow() }, OneSource());

        Assert.Equal(new[] { "Inventory" }, status.Containers.Select(note => note.Label));
    }

    // A manifest-driven collection with every consent group switched off scans nothing at all, and the
    // collector reports that as a skip. The panel must say so: with no containers read, the suppression
    // rule would otherwise leave the user's ticked collection entirely unmentioned.
    [Fact]
    public void A_manifest_driven_collection_with_no_groups_enabled_says_what_to_do_about_it()
    {
        var status = ReadStatusView.Build(
            new[] { Row("items", usesItemManifest: true, skipReason: CollectSkipReasons.NoItemGroupsEnabled) },
            NoSources());

        var note = Assert.Single(status.Collections);
        Assert.Equal(
            "items display: not read — none of its groups are switched on. Tick at least one under " +
            "Collections to include it.",
            note.Text);
        Assert.Equal(SourceTone.Missing, note.Tone);
    }
}
