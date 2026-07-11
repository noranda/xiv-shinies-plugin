using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// The upload log is a transparency surface: it must describe the payload that actually went out,
// so its summarization is pinned here rather than eyeballed in game.
public class UploadLogTests
{
    private static CollectionSnapshot SnapshotWith(
        Dictionary<string, JsonNode>? collections = null,
        Dictionary<string, string>? skipped = null) => new()
    {
        Collections = collections ?? new Dictionary<string, JsonNode>(),
        Skipped = skipped ?? new Dictionary<string, string>(),
    };

    private static UploadLogEntry SomeEntry(int minute = 0) => new()
    {
        At = new DateTimeOffset(2026, 7, 10, 20, minute, 0, TimeSpan.Zero),
        Trigger = SyncTrigger.Manual,
        Status = ApiStatus.Ok,
        Categories = Array.Empty<UploadLogCategory>(),
        Skipped = new Dictionary<string, string>(),
    };

    // --- Draft: summarizing what a snapshot is about to send -----------------------------

    [Fact]
    public void Draft_counts_the_facts_in_each_category()
    {
        var snapshot = SnapshotWith(collections: new Dictionary<string, JsonNode>
        {
            ["quests"] = JsonNode.Parse("[1,2,3]")!,
            ["items"] = JsonNode.Parse("""[{"id":10,"count":2}]""")!,
        });

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Login, snapshot);

        Assert.Collection(
            draft.Categories,
            c => { Assert.Equal("quests", c.Key); Assert.Equal(3, c.Count); },
            c => { Assert.Equal("items", c.Key); Assert.Equal(1, c.Count); });
    }

    // An empty list is a real answer ("read the source, found nothing") and must appear in the
    // log as zero, not vanish — vanishing is what SKIPPED categories do.
    [Fact]
    public void Draft_keeps_a_collected_but_empty_category_as_zero()
    {
        var snapshot = SnapshotWith(collections: new Dictionary<string, JsonNode>
        {
            ["minions"] = JsonNode.Parse("[]")!,
        });

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Interval, snapshot);

        var category = Assert.Single(draft.Categories);
        Assert.Equal(0, category.Count);
    }

    // Every collector today emits a JSON array, so this branch is purely defensive — but a
    // formatter that CAN misread a future non-array shape as zero facts eventually will, so the
    // "count it as one rather than crash or hide it" fallback is pinned.
    [Fact]
    public void Draft_counts_a_non_array_fact_node_as_one()
    {
        var snapshot = SnapshotWith(collections: new Dictionary<string, JsonNode>
        {
            ["future"] = JsonNode.Parse("""{"some":"object"}""")!,
        });

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Manual, snapshot);

        var category = Assert.Single(draft.Categories);
        Assert.Equal(1, category.Count);
    }

    [Fact]
    public void Draft_carries_the_skip_reasons_through()
    {
        var snapshot = SnapshotWith(
            skipped: new Dictionary<string, string> { ["achievements"] = "achievement_list_not_loaded" });

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Manual, snapshot);

        Assert.Equal("achievement_list_not_loaded", draft.Skipped["achievements"]);
    }

    [Fact]
    public void Draft_settles_into_an_entry_with_the_response_status()
    {
        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Manual, SnapshotWith());

        var settled = draft with { Status = ApiStatus.RateLimited };

        Assert.Equal(ApiStatus.RateLimited, settled.Status);
        Assert.Equal(SyncTrigger.Manual, settled.Trigger);
    }

    // --- The log itself -------------------------------------------------------------------

    [Fact]
    public void The_newest_entry_comes_first()
    {
        var log = new UploadLog();
        log.Record(SomeEntry(minute: 1));
        log.Record(SomeEntry(minute: 2));

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(2, log.Entries[0].At.Minute);
    }

    // The clear button: everything goes, and the log keeps working afterwards.
    [Fact]
    public void Clear_empties_the_log_and_recording_still_works_after()
    {
        var log = new UploadLog();
        log.Record(SomeEntry(minute: 1));
        log.Record(SomeEntry(minute: 2));

        log.Clear();
        Assert.Empty(log.Entries);

        log.Record(SomeEntry(minute: 3));
        var entry = Assert.Single(log.Entries);
        Assert.Equal(3, entry.At.Minute);
    }

    [Fact]
    public void The_log_holds_at_most_its_capacity_dropping_the_oldest()
    {
        var log = new UploadLog();
        for (var minute = 0; minute < UploadLog.Capacity + 5; minute++)
            log.Record(SomeEntry(minute));

        Assert.Equal(UploadLog.Capacity, log.Entries.Count);

        // Newest first; the oldest five fell off the end.
        Assert.Equal(UploadLog.Capacity + 4, log.Entries[0].At.Minute);
        Assert.Equal(5, log.Entries[^1].At.Minute);
    }

    // --- Wording --------------------------------------------------------------------------

    [Theory]
    [InlineData(SyncTrigger.Manual, "manual sync")]
    [InlineData(SyncTrigger.Login, "login sync")]
    [InlineData(SyncTrigger.Unlock, "new unlock")]
    [InlineData(SyncTrigger.Interval, "scheduled sync")]
    public void Triggers_read_as_plain_words(SyncTrigger trigger, string expected)
    {
        Assert.Equal(expected, UploadLogText.TriggerText(trigger));
    }

    [Theory]
    [InlineData(ApiStatus.Ok, "accepted")]
    [InlineData(ApiStatus.CharacterNotClaimed, "refused — character not claimed")]
    [InlineData(ApiStatus.InvalidToken, "refused — token rejected")]
    [InlineData(ApiStatus.RateLimited, "deferred — rate limited")]
    [InlineData(ApiStatus.SyncDisabled, "deferred — syncing paused by the server")]
    [InlineData(ApiStatus.NetworkError, "failed — could not reach the server")]
    [InlineData(ApiStatus.ServerError, "failed")]
    public void Statuses_read_as_outcomes(ApiStatus status, string expected)
    {
        Assert.Equal(expected, UploadLogText.StatusText(status));
    }

    // Accepted is the only green outcome; everything else needs the user's eye.
    [Theory]
    [InlineData(ApiStatus.Ok, true)]
    [InlineData(ApiStatus.RateLimited, false)]
    [InlineData(ApiStatus.NetworkError, false)]
    public void Only_accepted_counts_as_success(ApiStatus status, bool expected)
    {
        Assert.Equal(expected, UploadLogText.IsSuccess(status));
    }

    // Deferrals resolve themselves (the plugin retries later); refusals need the user. The log
    // colors them differently, so the split is pinned.
    [Theory]
    [InlineData(ApiStatus.RateLimited, true)]
    [InlineData(ApiStatus.SyncDisabled, true)]
    [InlineData(ApiStatus.NetworkError, true)]
    [InlineData(ApiStatus.Ok, false)]
    [InlineData(ApiStatus.InvalidToken, false)]
    [InlineData(ApiStatus.CharacterNotClaimed, false)]
    public void Deferrals_are_the_outcomes_that_heal_on_their_own(ApiStatus status, bool expected)
    {
        Assert.Equal(expected, UploadLogText.IsDeferral(status));
    }

    // --- Change highlighting ----------------------------------------------------------------
    // The window paints a category gold when what was sent differs from the last time the log
    // saw it — the "you just got something new" signal. Both the count and a content
    // fingerprint are compared: trading one watched item for another keeps the count identical
    // while the contents change (observed live with a relic tool trade-in), and the highlight
    // must fire for that too. The comparison rules are pinned here.

    [Fact]
    public void Draft_fingerprints_each_category_by_content_not_count()
    {
        var before = UploadLogEntry.Draft(
            DateTimeOffset.UnixEpoch, SyncTrigger.Manual,
            SnapshotWith(collections: new Dictionary<string, JsonNode>
            {
                ["items"] = JsonNode.Parse("""[{"id":1,"count":1},{"id":2,"count":1}]""")!,
            }));
        var after = UploadLogEntry.Draft(
            DateTimeOffset.UnixEpoch, SyncTrigger.Manual,
            SnapshotWith(collections: new Dictionary<string, JsonNode>
            {
                ["items"] = JsonNode.Parse("""[{"id":1,"count":1},{"id":3,"count":1}]""")!,
            }));

        // Same count, different contents — the fingerprints must differ.
        Assert.Equal(before.Categories[0].Count, after.Categories[0].Count);
        Assert.NotEqual(before.Categories[0].Fingerprint, after.Categories[0].Fingerprint);
    }

    [Fact]
    public void Identical_facts_produce_identical_fingerprints()
    {
        JsonNode Facts() => JsonNode.Parse("[1,2,3]")!;

        var first = UploadLogEntry.Draft(
            DateTimeOffset.UnixEpoch, SyncTrigger.Manual,
            SnapshotWith(collections: new Dictionary<string, JsonNode> { ["quests"] = Facts() }));
        var second = UploadLogEntry.Draft(
            DateTimeOffset.UnixEpoch, SyncTrigger.Manual,
            SnapshotWith(collections: new Dictionary<string, JsonNode> { ["quests"] = Facts() }));

        Assert.Equal(first.Categories[0].Fingerprint, second.Categories[0].Fingerprint);
    }

    // The trade-in case: count unchanged, contents swapped — must still flag as changed.
    [Fact]
    public void A_same_count_content_swap_is_flagged_as_changed()
    {
        var newestFirst = new[]
        {
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("items", 52, "aaaa1111") },
            },
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("items", 52, "bbbb2222") },
            },
        };

        Assert.Contains("items", UploadLogDiff.ChangedCategories(newestFirst, 0));
    }

    private static UploadLogEntry EntryWith(params (string Key, int Count)[] categories)
    {
        var list = new List<UploadLogCategory>(categories.Length);
        foreach (var (key, count) in categories)
            list.Add(new UploadLogCategory(key, count));

        return SomeEntry() with { Categories = list };
    }

    [Fact]
    public void A_count_that_changed_since_the_previous_upload_is_flagged()
    {
        var newestFirst = new[]
        {
            EntryWith(("minions", 390), ("mounts", 142)),
            EntryWith(("minions", 389), ("mounts", 142)),
        };

        var changed = UploadLogDiff.ChangedCategories(newestFirst, 0);

        Assert.Contains("minions", changed);
        Assert.DoesNotContain("mounts", changed);
    }

    // An unlock upload carries only the categories that changed, so the baseline for a category
    // is its most recent EARLIER appearance — entries that never mention it are skipped over.
    [Fact]
    public void The_baseline_skips_entries_that_do_not_mention_the_category()
    {
        var newestFirst = new[]
        {
            EntryWith(("minions", 390)),
            EntryWith(("quests", 3120)),
            EntryWith(("minions", 390)),
        };

        Assert.Empty(UploadLogDiff.ChangedCategories(newestFirst, 0));
    }

    // The first time the log ever sees a category there is nothing to compare against, and
    // flagging everything on the session's first upload would make the highlight meaningless.
    [Fact]
    public void A_category_with_no_earlier_appearance_is_not_flagged()
    {
        var newestFirst = new[]
        {
            EntryWith(("minions", 390)),
            EntryWith(("quests", 3120)),
        };

        Assert.Empty(UploadLogDiff.ChangedCategories(newestFirst, 0));
    }

    [Fact]
    public void The_oldest_entry_flags_nothing()
    {
        var newestFirst = new[]
        {
            EntryWith(("minions", 390)),
            EntryWith(("minions", 389)),
        };

        Assert.Empty(UploadLogDiff.ChangedCategories(newestFirst, 1));
    }

    // --- Failure diagnostics ----------------------------------------------------------------
    // Extra fields that answer "why did it fail" in a pasted bug report. Each is optional and
    // rendered only when it carries something.

    [Fact]
    public void Draft_records_the_manifest_version_the_items_were_built_against()
    {
        var draft = UploadLogEntry.Draft(
            DateTimeOffset.UnixEpoch, SyncTrigger.Manual, SnapshotWith(), "manifest-abc");

        Assert.Equal("manifest-abc", draft.ManifestVersion);
    }

    [Fact]
    public void Outcome_text_is_just_the_status_when_there_are_no_qualifiers()
    {
        Assert.Equal("accepted", UploadLogText.OutcomeText(SomeEntry()));
    }

    [Fact]
    public void Outcome_text_appends_the_server_requested_wait_and_the_retry_attempt()
    {
        var entry = SomeEntry() with
        {
            Status = ApiStatus.RateLimited,
            RetryAfter = TimeSpan.FromSeconds(90),
            Attempt = 2,
        };

        Assert.Equal(
            "deferred — rate limited — retry in 90s (attempt 2)",
            UploadLogText.OutcomeText(entry));
    }

    [Fact]
    public void Issues_text_is_null_when_the_server_sent_no_validation_detail()
    {
        Assert.Null(UploadLogText.IssuesText(null));
        Assert.Null(UploadLogText.IssuesText(new ErrorResponse { Error = "invalid_token" }));
        Assert.Null(UploadLogText.IssuesText(new ErrorResponse
        {
            Error = "invalid_payload",
            Issues = new ValidationIssues(),
        }));
    }

    [Fact]
    public void Issues_text_flattens_form_and_field_errors_into_one_line()
    {
        var error = new ErrorResponse
        {
            Error = "invalid_payload",
            Issues = new ValidationIssues
            {
                FormErrors = new[] { "body was not valid JSON" },
                FieldErrors = new Dictionary<string, string[]>
                {
                    ["characterName"] = new[] { "too long", "trailing whitespace" },
                },
            },
        };

        Assert.Equal(
            "body was not valid JSON · characterName: too long; trailing whitespace",
            UploadLogText.IssuesText(error));
    }

    // --- The clipboard dump ----------------------------------------------------------------
    // Pasted into Discord for bug reports, so it speaks in wire terms (category keys, status
    // names, UTC) rather than display copy — the person debugging needs stable identifiers.

    [Fact]
    public void Clipboard_text_reports_each_entry_in_wire_terms()
    {
        var entry = new UploadLogEntry
        {
            At = new DateTimeOffset(2026, 7, 10, 20, 42, 5, TimeSpan.Zero),
            Trigger = SyncTrigger.Manual,
            Status = ApiStatus.Ok,
            Categories = new[]
            {
                new UploadLogCategory("quests", 3120),
                new UploadLogCategory("items", 12),
            },
            Skipped = new Dictionary<string, string>
            {
                ["achievements"] = "achievement_list_not_loaded",
            },
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", new[] { entry });

        Assert.Contains("XIV Shinies Sync v1.2.3", text);
        Assert.Contains(
            "2026-07-10 20:42:05Z | Manual | Ok | sent: quests=3120 items=12 " +
            "| skipped: achievements=achievement_list_not_loaded",
            text);
    }

    // The backend is user-overridable, and "you are pointed at the wrong server" is a classic
    // support case — the dump must say which server the log is about.
    [Fact]
    public void Clipboard_text_names_the_backend_in_its_header()
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://staging.example.com", new[] { SomeEntry() });

        Assert.Contains("backend: https://staging.example.com", text);
    }

    [Fact]
    public void Clipboard_text_appends_the_diagnostics_a_failed_entry_carries()
    {
        var entry = SomeEntry() with
        {
            Status = ApiStatus.InvalidPayload,
            Attempt = 2,
            RetryAfter = TimeSpan.FromSeconds(30),
            HttpStatusCode = 400,
            ManifestVersion = "manifest-abc",
            Detail = "characterName: too long",
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", new[] { entry });

        Assert.Contains("| attempt: 2", text);
        Assert.Contains("| retryAfter: 30s", text);
        Assert.Contains("| http: 400", text);
        Assert.Contains("| manifest: manifest-abc", text);
        Assert.Contains("| issues: characterName: too long", text);
    }

    // A clean first-try success stays a clean one-liner: no qualifier noise on the common case.
    [Fact]
    public void Clipboard_text_omits_absent_diagnostics()
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { SomeEntry() with { HttpStatusCode = 200 } });

        Assert.DoesNotContain("attempt:", text);
        Assert.DoesNotContain("retryAfter:", text);
        Assert.DoesNotContain("http:", text);
        Assert.DoesNotContain("manifest:", text);
        Assert.DoesNotContain("issues:", text);
    }

    // The gold changed-highlight is invisible in a paste, so the dump carries the same fact as
    // a segment: which categories' contents differ from their previous appearance.
    [Fact]
    public void Clipboard_text_marks_categories_whose_contents_changed()
    {
        var newestFirst = new[]
        {
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("items", 52, "bbbb2222") },
            },
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("items", 52, "aaaa1111") },
            },
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", newestFirst);
        var lines = text.Split('\n');

        // Newest line (right after the header) is marked; the baseline line is not.
        Assert.Contains("| changed: items", lines[1]);
        Assert.DoesNotContain("changed:", lines[2]);
    }

    [Fact]
    public void Clipboard_text_omits_the_skipped_section_when_nothing_was_skipped()
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { SomeEntry() });

        Assert.DoesNotContain("skipped", text);
    }

    // Timestamps are normalized to UTC in the dump, whatever offset the entry carried.
    [Fact]
    public void Clipboard_text_normalizes_timestamps_to_utc()
    {
        var entry = SomeEntry() with
        {
            At = new DateTimeOffset(2026, 7, 10, 22, 0, 0, TimeSpan.FromHours(2)),
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", new[] { entry });

        Assert.Contains("2026-07-10 20:00:00Z", text);
    }
}
