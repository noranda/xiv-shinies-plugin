using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Assembles the upload body from a collection pass. Pure: it never reads the game or the network.
public class SyncPayloadBuilderTests
{
    private static readonly CharacterIdentity Identity = new()
    {
        ContentIdHash = new string('a', 64),
        Name = "Some Name",
        HomeWorld = "Excalibur",
    };

    private static CollectionSnapshot Snapshot(params string[] categoryKeys)
    {
        var collections = new Dictionary<string, JsonNode>();
        foreach (var key in categoryKeys)
            collections[key] = SyncFacts.Ids(new uint[] { 1, 2 });

        return new CollectionSnapshot
        {
            Collections = collections,
            Skipped = new Dictionary<string, string>(),
        };
    }

    [Fact]
    public void A_snapshot_built_without_source_notes_yields_no_item_sources()
    {
        // The Snapshot helper never sets SourceNotes, so this pins the property's default (an
        // empty dictionary) flowing through Build as an omitted itemSources key.
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"), manifestVersion: null);

        Assert.Null(request.ItemSources);
    }

    // The unreadable state exists for the settings panel only: it marks a source the game never
    // exposes (mannequins), so it carries no counts the server could judge. Sending it would be
    // a constant fact repeated on every upload — noise the data-minimization rule says to omit.
    [Fact]
    public void An_unreadable_source_note_stays_off_the_wire()
    {
        var snapshot = Snapshot("items") with
        {
            SourceNotes = new Dictionary<string, ItemSourceStatus>
            {
                [SourceKeys.Inventory] = new ItemSourceStatus { State = SourceStates.Live },
                [SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable },
            },
        };

        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, snapshot, manifestVersion: null);

        Assert.NotNull(request.ItemSources);
        Assert.True(request.ItemSources!.ContainsKey(SourceKeys.Inventory));
        Assert.False(request.ItemSources.ContainsKey(SourceKeys.Mannequins));
    }

    [Fact]
    public void A_snapshot_whose_only_source_note_is_unreadable_yields_no_item_sources()
    {
        // With the unreadable note dropped there is nothing left to say, and an empty itemSources
        // object is exactly the noise the omitted-key rule exists to avoid.
        var snapshot = Snapshot("items") with
        {
            SourceNotes = new Dictionary<string, ItemSourceStatus>
            {
                [SourceKeys.Mannequins] = new ItemSourceStatus { State = SourceStates.Unreadable },
            },
        };

        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, snapshot, manifestVersion: null);

        Assert.Null(request.ItemSources);
    }

    [Fact]
    public void Carries_the_identity_version_and_trigger()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.2.3", SyncTrigger.Login, Snapshot("quests"), manifestVersion: null);

        Assert.Equal(Identity.ContentIdHash, request.CharacterContentIdHash);
        Assert.Equal("Some Name", request.CharacterName);
        Assert.Equal("Excalibur", request.HomeWorld);
        Assert.Equal("1.2.3", request.PluginVersion);
        Assert.Equal(SyncTrigger.Login, request.Trigger);
    }

    [Fact]
    public void Carries_exactly_the_categories_the_snapshot_read()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Interval, Snapshot("quests", "mounts"), null);

        Assert.True(request.Collections.ContainsKey("quests"));
        Assert.True(request.Collections.ContainsKey("mounts"));
        Assert.Equal(2, request.Collections.Count);
    }

    // An unlock names the one category that changed rather than sweeping all of them, because the
    // contract says an `unlock` upload "stamps the upload moment as the acquisition time for every
    // category in it". A sweep would therefore date rows in categories that did not change, and an
    // acquisition date, once written, is never revised by a later plugin upload. Routing also keeps
    // the work small: unlocks arrive in bursts, and a sweep would re-scan every inventory container
    // to re-report what the server already knows.
    [Fact]
    public void An_unlock_upload_carries_only_the_category_that_was_unlocked()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Unlock, Snapshot("mounts"), null);

        Assert.Equal(SyncTrigger.Unlock, request.Trigger);
        Assert.Equal(new[] { "mounts" }, request.Collections.Keys);
    }

    [Fact]
    public void Omits_the_manifest_version_when_there_is_none()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("quests"), manifestVersion: null);

        Assert.Null(request.ManifestVersion);
    }

    // Echoing it back records which manifest the item list was built against.
    [Fact]
    public void Echoes_the_manifest_version_when_the_server_supplied_one()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Manual, Snapshot("items"), "a1b2c3d4e5f6");

        Assert.Equal("a1b2c3d4e5f6", request.ManifestVersion);
    }

    // A snapshot that read nothing still round-trips: `collections` is an empty object, not null.
    [Fact]
    public void An_empty_snapshot_produces_an_empty_collections_object()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Interval, Snapshot(), null);

        Assert.Empty(request.Collections);
    }

    // The contract trims the name and caps it at 100 characters; sending untrimmed input would fail
    // validation and reject the whole upload.
    [Fact]
    public void Trims_the_character_name_and_world()
    {
        var padded = Identity with { Name = "  Some Name  ", HomeWorld = " Excalibur " };

        var request = SyncPayloadBuilder.Build(
            padded, "1.0.0", SyncTrigger.Login, Snapshot("quests"), null);

        Assert.Equal("Some Name", request.CharacterName);
        Assert.Equal("Excalibur", request.HomeWorld);
    }

    // Nothing to upload is not the same as nothing to say — but the caller decides that, not the
    // builder. The builder must never invent facts.
    [Fact]
    public void Never_adds_a_category_the_snapshot_did_not_contain()
    {
        var request = SyncPayloadBuilder.Build(
            Identity, "1.0.0", SyncTrigger.Login, Snapshot("quests"), null);

        Assert.False(request.Collections.ContainsKey("achievements"));
    }

    [Fact]
    public void Build_puts_source_notes_on_the_request_when_present()
    {
        var sourceNotes = new Dictionary<string, ItemSourceStatus>
        {
            ["inventory"] = new ItemSourceStatus { State = SourceStates.Live },
            ["saddlebag"] = new ItemSourceStatus { State = SourceStates.Cached, Count = 3 },
        };
        var snapshot = Snapshot("items") with { SourceNotes = sourceNotes };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.NotNull(request.ItemSources);
        Assert.Equal(2, request.ItemSources.Count);
        Assert.Equal(SourceStates.Live, request.ItemSources["inventory"].State);
        Assert.Equal(SourceStates.Cached, request.ItemSources["saddlebag"].State);
        Assert.Equal(3, request.ItemSources["saddlebag"].Count);
    }

    [Fact]
    public void Build_omits_item_sources_when_none_are_present()
    {
        var snapshot = Snapshot("items") with { SourceNotes = new Dictionary<string, ItemSourceStatus>() };

        var request = SyncPayloadBuilder.Build(Identity, "1.0.0", SyncTrigger.Manual, snapshot, null);

        Assert.Null(request.ItemSources);
    }
}
