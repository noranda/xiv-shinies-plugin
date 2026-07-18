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

    // Object-shaped facts (questSequences: quest id → journal step) count their members, the same
    // way an array counts its elements. The log is a transparency surface: "Quest progress 2" must
    // mean two quests were reported.
    [Fact]
    public void Draft_counts_the_members_of_an_object_shaped_category()
    {
        var snapshot = SnapshotWith(collections: new Dictionary<string, JsonNode>
        {
            ["questSequences"] = JsonNode.Parse("""{"70562":3,"69208":255}""")!,
        });

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Manual, snapshot);

        var category = Assert.Single(draft.Categories);
        Assert.Equal(2, category.Count);
    }

    // A shape the log has never seen (neither array nor object) is purely hypothetical — but a
    // formatter that CAN misread it as zero facts eventually will, so the "count it as one rather
    // than crash or hide it" fallback is pinned.
    [Fact]
    public void Draft_counts_a_scalar_fact_node_as_one()
    {
        var snapshot = SnapshotWith(collections: new Dictionary<string, JsonNode>
        {
            ["future"] = JsonNode.Parse("42")!,
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

    // A content swap that keeps the count identical must still flag: the diff compares the
    // fingerprint as well as the count, so contents can never change invisibly.
    [Fact]
    public void A_same_count_content_swap_is_flagged_as_changed()
    {
        var newestFirst = new[]
        {
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("quests", 52, "aaaa1111") },
            },
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("quests", 52, "bbbb2222") },
            },
        };

        Assert.Contains("quests", UploadLogDiff.ChangedCategories(newestFirst, 0));
    }

    // A manifest-driven category's contents move whenever the SERVER edits its manifest, so
    // "changed" would claim the player obtained something when nothing happened. Those
    // categories are excluded from the diff entirely; their honest signal is the server's
    // proof answer (see the proof-reporting section below).
    [Fact]
    public void A_manifest_driven_category_is_never_flagged_as_changed()
    {
        var newestFirst = new[]
        {
            SomeEntry() with
            {
                Categories = new[]
                {
                    new UploadLogCategory("items", 60, "bbbb2222", UsesItemManifest: true),
                },
            },
            SomeEntry() with
            {
                Categories = new[]
                {
                    new UploadLogCategory("items", 52, "aaaa1111", UsesItemManifest: true),
                },
            },
        };

        Assert.Empty(UploadLogDiff.ChangedCategories(newestFirst, 0));
    }

    [Fact]
    public void Draft_marks_the_categories_the_snapshot_calls_manifest_driven()
    {
        var snapshot = new CollectionSnapshot
        {
            Collections = new Dictionary<string, JsonNode>
            {
                ["quests"] = JsonNode.Parse("[1,2,3]")!,
                ["items"] = JsonNode.Parse("""[{"id":10,"count":2}]""")!,
            },
            Skipped = new Dictionary<string, string>(),
            ManifestDrivenKeys = new HashSet<string> { "items" },
        };

        var draft = UploadLogEntry.Draft(DateTimeOffset.UnixEpoch, SyncTrigger.Manual, snapshot);

        Assert.Collection(
            draft.Categories,
            c => Assert.False(c.UsesItemManifest),
            c => Assert.True(c.UsesItemManifest));
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

    // --- Proof reporting for manifest-driven categories -------------------------------------
    // The server answers an items-carrying upload with provenSteps: the relic-step rows this
    // upload created or promoted. That delta — not a content diff — is what tells the user
    // whether they actually progressed, so the window prints it beside the category.

    private static UploadLogEntry ManifestEntry(
        int? provenSteps, ApiStatus status = ApiStatus.Ok) => SomeEntry() with
    {
        Status = status,
        Categories = new[]
        {
            new UploadLogCategory("items", 189, "aaaa1111", UsesItemManifest: true),
        },
        ProvenSteps = provenSteps,
    };

    [Theory]
    [InlineData(3, "3 new steps proven")]
    [InlineData(1, "1 new step proven")]
    public void Proof_text_reports_the_steps_an_accepted_upload_proved(int steps, string expected)
    {
        Assert.Equal(expected, UploadLogText.ProofText(ManifestEntry(steps)));
    }

    // Zero means "items applied, nothing new proved" — an honest quiet, not a note.
    [Fact]
    public void Proof_text_is_silent_when_nothing_new_was_proved()
    {
        Assert.Null(UploadLogText.ProofText(ManifestEntry(0)));
    }

    // No provenSteps key on an accepted items-carrying upload means derivation failed
    // server-side; the next upload retries it, so the honest word is "pending".
    [Fact]
    public void Proof_text_says_pending_when_the_server_sent_no_proof_answer()
    {
        Assert.Equal("proof pending", UploadLogText.ProofText(ManifestEntry(null)));
    }

    // A refused or failed upload has no server verdict to report — even when a step value is
    // somehow present, the status gate wins.
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void Proof_text_is_null_when_the_upload_was_not_accepted(int? steps)
    {
        Assert.Null(UploadLogText.ProofText(ManifestEntry(steps, ApiStatus.NetworkError)));
    }

    // An upload that carried no manifest-driven category was never owed a proof answer.
    [Fact]
    public void Proof_text_is_null_when_no_manifest_driven_category_was_sent()
    {
        Assert.Null(UploadLogText.ProofText(SomeEntry()));
    }

    // An EMPTY manifest-driven category carries no information (the contract reads an empty
    // array as "nothing to apply"), so the server owes it no proof answer — a missing one is
    // not a pending proof, and saying so would falsely report a server-side failure.
    [Fact]
    public void Proof_text_is_null_when_the_manifest_category_carried_no_facts()
    {
        var entry = ManifestEntry(null) with
        {
            Categories = new[]
            {
                new UploadLogCategory("items", 0, "aaaa1111", UsesItemManifest: true),
            },
        };

        Assert.Null(UploadLogText.ProofText(entry));
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

    // Validation text is server-supplied and the backend is user-overridable, so a hostile
    // server must not be able to bloat the in-memory log or the ImGui rendering with it.
    [Fact]
    public void Issues_text_truncates_absurdly_long_server_complaints()
    {
        var error = new ErrorResponse
        {
            Error = "invalid_payload",
            Issues = new ValidationIssues { FormErrors = new[] { new string('x', 10_000) } },
        };

        var text = UploadLogText.IssuesText(error);

        Assert.NotNull(text);
        Assert.Equal(UploadLogEntry.MaxServerStringLength + 3, text!.Length); // +3 for the ellipsis
        Assert.EndsWith("...", text);
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
                Categories = new[] { new UploadLogCategory("quests", 52, "bbbb2222") },
            },
            SomeEntry() with
            {
                Categories = new[] { new UploadLogCategory("quests", 52, "aaaa1111") },
            },
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", newestFirst);
        var lines = text.Split('\n');

        // Newest line (right after the header) is marked; the baseline line is not.
        Assert.Contains("| changed: quests", lines[1]);
        Assert.DoesNotContain("changed:", lines[2]);
    }

    // The dump reports the proof answer in wire terms, whatever its value — including zero,
    // which the window stays silent about but a debugger wants to see.
    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void Clipboard_text_reports_proven_steps_in_wire_terms(int steps)
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { ManifestEntry(steps) });

        Assert.Contains($"| provenSteps: {steps}", text);
    }

    // An accepted items-carrying upload with NO proof answer is the derivation-failed case —
    // exactly the line a bug report needs to show, so the dump says so explicitly.
    [Fact]
    public void Clipboard_text_marks_an_absent_proof_answer()
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { ManifestEntry(null) });

        Assert.Contains("| provenSteps: absent", text);
    }

    // No answer arrived and none was owed (nothing manifest-driven was sent) — the segment
    // would be noise on every id-list-only upload.
    [Fact]
    public void Clipboard_text_omits_proven_steps_when_none_arrived_and_none_was_owed()
    {
        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { SomeEntry() });

        Assert.DoesNotContain("provenSteps", text);
    }

    // The empty-category rule, in the dump: an all-empty manifest-driven category was never
    // owed an answer, so a missing one gets no "absent" marker.
    [Fact]
    public void Clipboard_text_omits_the_absent_marker_when_the_manifest_category_was_empty()
    {
        var entry = ManifestEntry(null) with
        {
            Categories = new[]
            {
                new UploadLogCategory("items", 0, "aaaa1111", UsesItemManifest: true),
            },
        };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", new[] { entry });

        Assert.DoesNotContain("provenSteps", text);
    }

    // The dump is verbatim wire truth: if an answer is present it prints, even on an entry
    // that carried nothing manifest-driven — a contract-impossible reply is exactly what a
    // debugger would want to see.
    [Fact]
    public void Clipboard_text_reports_a_proof_answer_even_without_a_manifest_driven_category()
    {
        var entry = SomeEntry() with { ProvenSteps = 2 };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", new[] { entry });

        Assert.Contains("| provenSteps: 2", text);
    }

    // The two signals are independent segments: an id-list category's "changed" mark and the
    // upload's proof answer both appear when one upload carries both facts.
    [Fact]
    public void Clipboard_text_reports_changed_and_proven_steps_together()
    {
        var older = SomeEntry() with
        {
            Categories = new[]
            {
                new UploadLogCategory("quests", 52, "aaaa1111"),
                new UploadLogCategory("items", 189, "cccc3333", UsesItemManifest: true),
            },
        };
        var newest = older with
        {
            Categories = new[]
            {
                new UploadLogCategory("quests", 53, "bbbb2222"),
                new UploadLogCategory("items", 189, "cccc3333", UsesItemManifest: true),
            },
            ProvenSteps = 2,
        };

        var text = UploadLogText.ClipboardText(
            "1.2.3", "https://xiv-shinies.com", new[] { newest, older });
        var lines = text.Split('\n');

        // Both segments land on the newest line: quests changed against its baseline, and the
        // upload's proof answer printed verbatim.
        Assert.Contains("| changed: quests", lines[1]);
        Assert.Contains("| provenSteps: 2", lines[1]);
    }

    // The window rule and the dump rule agree: a manifest-driven category never reads as
    // "changed", in gold or in text.
    [Fact]
    public void Clipboard_text_never_marks_a_manifest_driven_category_as_changed()
    {
        var older = ManifestEntry(0) with
        {
            Categories = new[]
            {
                new UploadLogCategory("items", 52, "bbbb2222", UsesItemManifest: true),
            },
        };
        var newestFirst = new[] { ManifestEntry(3), older };

        var text = UploadLogText.ClipboardText("1.2.3", "https://xiv-shinies.com", newestFirst);

        Assert.DoesNotContain("changed:", text);
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
