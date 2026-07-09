using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Pins the DTOs to the exact wire format in docs/api-contract.md. The server owns this shape, so
// these tests are the tripwire that fires if a field is renamed, a casing slips, or an absent
// category accidentally starts serializing as an empty array.
public class DtoSerializationTests
{
    // Serializes a value and reparses it as a mutable JSON object so tests can ask "is this key
    // present at all?" — the omitted-vs-empty distinction the monotonic-write rule depends on.
    private static JsonObject Serialize<T>(T value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, ApiJson.Options))!.AsObject();

    private static SyncRequest MinimalRequest() => new()
    {
        CharacterContentIdHash = new string('a', 64),
        CharacterName = "Some Name",
        HomeWorld = "Excalibur",
        PluginVersion = "1.0.0",
        Trigger = SyncTrigger.Login,
        Collections = new Dictionary<string, JsonNode>(),
    };

    [Fact]
    public void SyncRequest_uses_the_contract_field_names_in_camelCase()
    {
        var json = Serialize(MinimalRequest());

        Assert.True(json.ContainsKey("characterContentIdHash"));
        Assert.True(json.ContainsKey("characterName"));
        Assert.True(json.ContainsKey("homeWorld"));
        Assert.True(json.ContainsKey("pluginVersion"));
        Assert.True(json.ContainsKey("trigger"));
        Assert.True(json.ContainsKey("collections"));
    }

    [Fact]
    public void SyncRequest_serializes_the_trigger_as_a_lowercase_string()
    {
        var json = Serialize(MinimalRequest() with { Trigger = SyncTrigger.Unlock });
        Assert.Equal("unlock", json["trigger"]!.GetValue<string>());
    }

    [Fact]
    public void SyncRequest_omits_manifestVersion_when_it_is_null()
    {
        var json = Serialize(MinimalRequest());
        Assert.False(json.ContainsKey("manifestVersion"));
    }

    [Fact]
    public void SyncRequest_includes_manifestVersion_when_present()
    {
        var json = Serialize(MinimalRequest() with { ManifestVersion = "a1b2c3d4e5f6" });
        Assert.Equal("a1b2c3d4e5f6", json["manifestVersion"]!.GetValue<string>());
    }

    // The monotonic-write rule: a category that could not be read must be ABSENT, never [].
    // Absence means "not read this time"; [] means "read, and it was empty".
    [Fact]
    public void Unread_collection_categories_are_omitted_entirely()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = SyncFacts.Ids(new uint[] { 65575, 66216 }),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("quests"));
        Assert.False(collections.ContainsKey("achievements"));
        Assert.False(collections.ContainsKey("mounts"));
        Assert.False(collections.ContainsKey("minions"));
        Assert.False(collections.ContainsKey("items"));
    }

    [Fact]
    public void An_explicitly_empty_category_still_serializes_as_an_empty_array()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["mounts"] = SyncFacts.Ids(new uint[0]),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("mounts"));
        Assert.Empty(collections["mounts"]!.AsArray());
    }

    [Fact]
    public void Items_serialize_with_id_count_and_fresh()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["items"] = SyncFacts.Items(
                    new[] { new ItemPossession { Id = 7851, Count = 1, Fresh = true } }),
            },
        };

        var item = Serialize(request)["collections"]!["items"]!.AsArray()[0]!.AsObject();

        Assert.Equal(7851u, item["id"]!.GetValue<uint>());
        Assert.Equal(1u, item["count"]!.GetValue<uint>());
        Assert.True(item["fresh"]!.GetValue<bool>());
    }

    // A category the plugin knows nothing about today must ride the wire untouched. The server
    // strips keys it does not recognize, which is what lets both sides ship independently.
    [Fact]
    public void An_unknown_category_key_serializes_without_any_special_handling()
    {
        var request = MinimalRequest() with
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["facewear"] = SyncFacts.Ids(new uint[] { 42 }),
            },
        };

        var collections = Serialize(request)["collections"]!.AsObject();

        Assert.True(collections.ContainsKey("facewear"));
        Assert.Equal(42u, collections["facewear"]!.AsArray()[0]!.GetValue<uint>());
    }

    [Fact]
    public void SyncRequest_round_trips_through_json()
    {
        var original = MinimalRequest() with
        {
            ManifestVersion = "a1b2c3d4e5f6",
            Trigger = SyncTrigger.Interval,
            Collections = new Dictionary<string, JsonNode>
            {
                ["achievements"] = SyncFacts.Ids(new uint[] { 1, 2 }),
            },
        };

        var json = JsonSerializer.Serialize(original, ApiJson.Options);
        var back = JsonSerializer.Deserialize<SyncRequest>(json, ApiJson.Options)!;

        Assert.Equal(original.CharacterContentIdHash, back.CharacterContentIdHash);
        Assert.Equal(original.ManifestVersion, back.ManifestVersion);
        Assert.Equal(SyncTrigger.Interval, back.Trigger);
        Assert.Equal(new uint[] { 1, 2 }, back.Collections["achievements"].AsArray().GetValues<uint>());
        Assert.False(back.Collections.ContainsKey("quests"));
    }

    // The exact 200 body from the contract's GET /me example.
    [Fact]
    public void MeResponse_deserializes_the_contract_sample()
    {
        const string body = """
        {
          "characters": [
            {"id": "12345678", "name": "Some Name", "pluginLinked": true,
             "verified": true, "world": "Excalibur"}
          ],
          "user": {"id": "abc-uuid"}
        }
        """;

        var me = JsonSerializer.Deserialize<MeResponse>(body, ApiJson.Options)!;

        Assert.Equal("abc-uuid", me.User.Id);
        var character = Assert.Single(me.Characters);
        Assert.Equal("12345678", character.Id); // a BigInt on the server, so it travels as a string
        Assert.Equal("Some Name", character.Name);
        Assert.True(character.PluginLinked);
        Assert.True(character.Verified);
        Assert.Equal("Excalibur", character.World);
    }

    [Fact]
    public void ConfigResponse_deserializes_the_contract_sample()
    {
        const string body = """
        {
          "categories": {"achievements": true, "items": true, "minions": false,
                         "mounts": true, "quests": true},
          "enabled": true,
          "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
          "itemManifest": [7851, 7852],
          "manifestVersion": "a1b2c3d4e5f6"
        }
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.True(config.Enabled);
        Assert.False(config.IsCategoryEnabled("minions"));
        Assert.True(config.IsCategoryEnabled("achievements"));
        Assert.Equal(30, config.Intervals.FullSyncMinutes);
        Assert.Equal(5, config.Intervals.UnlockDebounceSeconds);
        Assert.Equal(new uint[] { 7851, 7852 }, config.ItemManifest);
        Assert.Equal("a1b2c3d4e5f6", config.ManifestVersion);
    }

    // A switch the server has never heard of reads as enabled, so a new collector works before the
    // server grows its toggle. The server strips payload keys it does not recognize.
    [Fact]
    public void An_unknown_category_switch_defaults_to_enabled()
    {
        const string body = """
        {"categories": {"quests": true},
         "enabled": true,
         "intervals": {"fullSyncMinutes": 30, "unlockDebounceSeconds": 5},
         "itemManifest": [], "manifestVersion": "abc"}
        """;

        var config = JsonSerializer.Deserialize<ConfigResponse>(body, ApiJson.Options)!;

        Assert.True(config.IsCategoryEnabled("quests"));
        Assert.True(config.IsCategoryEnabled("facewear")); // never mentioned by the server
    }

    // Optional keys are OMITTED rather than null, so the plugin can feature-detect them.
    [Fact]
    public void SyncResponse_leaves_omitted_optional_keys_null()
    {
        const string body = """
        {"ok": true, "bound": false,
         "written": {"achievements": 0, "minions": 2, "mounts": 1, "quests": 12}}
        """;

        var response = JsonSerializer.Deserialize<SyncResponse>(body, ApiJson.Options)!;

        Assert.True(response.Ok);
        Assert.False(response.Bound);
        Assert.Equal(2, response.Written.Minions);
        Assert.Equal(12, response.Written.Quests);
        Assert.Null(response.AchievementsSkipped);
        Assert.Null(response.ProvenSteps);
        Assert.Null(response.SkippedCategories);
    }

    [Fact]
    public void SyncResponse_reads_optional_keys_when_the_server_sends_them()
    {
        const string body = """
        {"ok": true, "bound": true,
         "written": {"achievements": 0, "minions": 0, "mounts": 0, "quests": 0},
         "achievementsSkipped": "not_sent",
         "provenSteps": 3,
         "skippedCategories": ["minions"]}
        """;

        var response = JsonSerializer.Deserialize<SyncResponse>(body, ApiJson.Options)!;

        Assert.True(response.Bound);
        Assert.Equal("not_sent", response.AchievementsSkipped);
        Assert.Equal(3, response.ProvenSteps);
        Assert.Equal(new[] { "minions" }, response.SkippedCategories);
    }

    [Fact]
    public void ErrorResponse_reads_the_opaque_401_body()
    {
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            """{"error": "invalid_token"}""", ApiJson.Options)!;

        Assert.Equal("invalid_token", error.Error);
        Assert.Null(error.Name);
        Assert.Null(error.World);
    }

    // The 403 echoes name/world back so the UI can say "claim <name> @ <world> on the website".
    [Fact]
    public void ErrorResponse_reads_the_403_character_not_claimed_body()
    {
        var error = JsonSerializer.Deserialize<ErrorResponse>(
            """{"error": "character_not_claimed", "name": "Some Name", "world": "Excalibur"}""",
            ApiJson.Options)!;

        Assert.Equal("character_not_claimed", error.Error);
        Assert.Equal("Some Name", error.Name);
        Assert.Equal("Excalibur", error.World);
    }

    // A 400 carries the flattened validation issues. Useful for the log; a non-JSON body arrives
    // in the same shape with the explanation under formErrors.
    [Fact]
    public void ErrorResponse_reads_the_400_validation_issues()
    {
        const string body = """
        {"error": "invalid_payload",
         "issues": {"fieldErrors": {"trigger": ["Invalid enum value."]},
                    "formErrors": ["Request body is not valid JSON"]}}
        """;

        var error = JsonSerializer.Deserialize<ErrorResponse>(body, ApiJson.Options)!;

        Assert.Equal("invalid_payload", error.Error);
        Assert.Equal("Request body is not valid JSON", Assert.Single(error.Issues!.FormErrors!));
        Assert.Equal("Invalid enum value.", Assert.Single(error.Issues.FieldErrors!["trigger"]));
    }
}
