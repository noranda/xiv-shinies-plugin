using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// One category's contribution to an upload: its wire key, how many facts went out, and a short
/// content fingerprint.
/// </summary>
/// <remarks>
/// The fingerprint exists because the count alone cannot see an exchange: trading one watched
/// item for another leaves the count identical while the payload's contents change (a relic
/// tool trade-in does exactly this). It is a hash of the facts, so the log still carries no
/// ids — just enough to answer "did this category's contents change since last time?".
/// </remarks>
// A "positional record": the parameter list declares init-only properties and a constructor in
// one line — the C# shorthand for a tiny immutable data shape.
public sealed record UploadLogCategory(string Key, int Count, string Fingerprint = "");

/// <summary>
/// One upload, as shown in the settings window's upload log: when, why, what was sent, and how
/// the server answered.
/// </summary>
/// <remarks>
/// A transparency surface: it is built from the same snapshot the payload was built from, at the
/// moment the payload was assembled — never reconstructed after the fact. It carries category
/// <b>keys</b>, not names: the window maps keys to whatever the registered collectors call
/// themselves, so this type stays free of category-name knowledge.
/// </remarks>
public sealed record UploadLogEntry
{
    /// <summary>When the payload was assembled (UTC; the window renders it in local time).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What prompted the upload.</summary>
    public required SyncTrigger Trigger { get; init; }

    /// <summary>
    /// How the attempt ended. A draft carries <see cref="ApiStatus.Unknown"/> until the response
    /// lands; only settled entries reach the log.
    /// </summary>
    public required ApiStatus Status { get; init; }

    /// <summary>What was sent, per category, in the order the collectors ran.</summary>
    public required IReadOnlyList<UploadLogCategory> Categories { get; init; }

    /// <summary>The categories this pass could not read, keyed by category, with the reason code.</summary>
    public required IReadOnlyDictionary<string, string> Skipped { get; init; }

    // --- Failure diagnostics -----------------------------------------------------------------
    // Optional, and filled at settle time (except the manifest version, known at build time).
    // They exist for the pasted-into-Discord bug report: each answers a "why did it fail"
    // question the status alone cannot. None carries identity — the log must stay structurally
    // incapable of leaking who the character is.

    /// <summary>Which attempt settled the upload: 1 on the first try, 2 after a retry.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>
    /// The <c>/config</c> manifest version the items list was built against, or null when no
    /// config had been fetched — the first question when relic counts look wrong server-side.
    /// </summary>
    public string? ManifestVersion { get; init; }

    /// <summary>How long the server asked us to wait, on rate-limited/paused deferrals.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The literal HTTP status code, when a response arrived. The contract mapping erases it
    /// (a 502 from a proxy and a 418 both become <see cref="ApiStatus.Unknown"/>), and
    /// diagnostics need the real number.
    /// </summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>
    /// The server's validation complaints on a rejected payload, flattened to one line — a 400
    /// is by definition a plugin bug, and this is the server saying exactly which field it hated.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Summarizes a collection snapshot into a draft entry, before the upload's outcome is known.
    /// Settle it once the response lands: <c>draft with { Status = response.Status, … }</c>.
    /// </summary>
    public static UploadLogEntry Draft(
        DateTimeOffset at,
        SyncTrigger trigger,
        CollectionSnapshot snapshot,
        string? manifestVersion = null)
    {
        var categories = new List<UploadLogCategory>(snapshot.Collections.Count);
        foreach (var (key, facts) in snapshot.Collections)
            categories.Add(new UploadLogCategory(key, CountFacts(facts), Fingerprint(facts)));

        return new UploadLogEntry
        {
            At = at,
            Trigger = trigger,
            Status = ApiStatus.Unknown,
            Categories = categories,
            Skipped = snapshot.Skipped,
            ManifestVersion = manifestVersion,
        };
    }

    /// <summary>
    /// How many facts a category's JSON carries. Every category today is an array (of ids, or of
    /// item-count objects); anything else counts as one fact rather than crashing the log.
    /// </summary>
    private static int CountFacts(JsonNode facts) =>
        facts is JsonArray array ? array.Count : 1;

    /// <summary>
    /// A short, deterministic hash of a category's facts. Collectors build their facts in a
    /// stable order (game sheets and the manifest are iterated in order), so identical contents
    /// always hash identically — and any change, even one that leaves the count the same,
    /// changes the hash.
    /// </summary>
    private static string Fingerprint(JsonNode facts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(facts.ToJsonString()));

        // Eight hex characters: not a security boundary, just a change detector — 32 bits is
        // plenty to make an accidental collision between two consecutive uploads implausible.
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}

/// <summary>
/// The most recent uploads, newest first — what the settings window's upload log renders.
/// </summary>
/// <remarks>
/// In memory only, deliberately: persisting a history of what a character owns would be a new
/// data store to disclose, for a feature whose job is transparency about the current session.
/// Bounded so a long session cannot grow it forever.
/// </remarks>
public sealed class UploadLog
{
    /// <summary>How many entries are kept before the oldest falls off.</summary>
    public const int Capacity = 20;

    // Readers get whatever list this reference points at; writers publish a NEW list and swap
    // the reference. A reference swap is atomic in .NET, so the draw thread can read Entries at
    // any moment without locks — at worst it sees the list from one moment earlier. `volatile`
    // keeps either thread from caching a stale reference.
    private volatile IReadOnlyList<UploadLogEntry> entries = Array.Empty<UploadLogEntry>();

    // Guards the WRITERS against each other (readers need no lock — see above). Record runs on
    // the upload task and Clear on the draw thread; without this, a Clear landing between
    // Record's read of the old list and its swap would be undone — the freshly built list still
    // contains the entries the user just cleared.
    private readonly object writeLock = new();

    /// <summary>The recorded uploads, newest first. The returned list is never mutated.</summary>
    public IReadOnlyList<UploadLogEntry> Entries => entries;

    /// <summary>Adds a settled upload at the front, dropping the oldest entry past capacity.</summary>
    public void Record(UploadLogEntry entry)
    {
        lock (writeLock)
        {
            var current = entries;
            var next = new List<UploadLogEntry>(Math.Min(current.Count + 1, Capacity)) { entry };

            foreach (var existing in current)
            {
                if (next.Count >= Capacity)
                    break;

                next.Add(existing);
            }

            entries = next;
        }
    }

    /// <summary>Empties the log — the settings window's clear button.</summary>
    public void Clear()
    {
        lock (writeLock)
        {
            entries = Array.Empty<UploadLogEntry>();
        }
    }
}

/// <summary>
/// Compares log entries so the window can highlight what changed — the "you just got something
/// new" signal beside a count.
/// </summary>
public static class UploadLogDiff
{
    /// <summary>
    /// The category keys in <c>newestFirst[index]</c> whose contents differ from that category's
    /// most recent earlier appearance in the log — a different count, or the same count with a
    /// different fingerprint (one watched item traded for another, say).
    /// </summary>
    /// <remarks>
    /// The baseline is the nearest OLDER entry that mentions the category, not simply the
    /// previous entry: an unlock upload carries only the categories that changed, so in-between
    /// entries may not mention a category at all. A category the log has never seen before is
    /// not flagged — with no baseline, "changed" would be a guess, and it would paint the whole
    /// first upload of every session.
    /// </remarks>
    public static IReadOnlySet<string> ChangedCategories(
        IReadOnlyList<UploadLogEntry> newestFirst, int index)
    {
        var changed = new HashSet<string>();

        foreach (var category in newestFirst[index].Categories)
        {
            // `is { } baseline` is a null test and an unwrap in one: the branch runs only when a
            // baseline exists, with `baseline` as the record inside the nullable.
            if (Baseline(newestFirst, index, category.Key) is { } baseline
                && (baseline.Count != category.Count
                    || baseline.Fingerprint != category.Fingerprint))
            {
                changed.Add(category.Key);
            }
        }

        return changed;
    }

    /// <summary>
    /// The category's entry in the nearest log row older than <paramref name="index"/> that
    /// mentions it, or null when no older row does.
    /// </summary>
    private static UploadLogCategory? Baseline(
        IReadOnlyList<UploadLogEntry> newestFirst, int index, string categoryKey)
    {
        for (var older = index + 1; older < newestFirst.Count; older++)
        {
            foreach (var baseline in newestFirst[older].Categories)
            {
                if (baseline.Key == categoryKey)
                    return baseline;
            }
        }

        return null;
    }
}

/// <summary>Turns an upload log entry's enums into the words the window prints.</summary>
public static class UploadLogText
{
    /// <summary>The trigger, as a person would say it.</summary>
    public static string TriggerText(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => "manual sync",
        SyncTrigger.Login => "login sync",
        SyncTrigger.Unlock => "new unlock",
        SyncTrigger.Interval => "scheduled sync",
        _ => "sync",
    };

    /// <summary>
    /// The outcome, one short phrase. "Refused" means the user must fix something; "deferred"
    /// means the plugin will simply try again later; "failed" covers everything else.
    /// </summary>
    public static string StatusText(ApiStatus status) => status switch
    {
        ApiStatus.Ok => "accepted",
        ApiStatus.CharacterNotClaimed => "refused — character not claimed",
        ApiStatus.InvalidToken => "refused — token rejected",
        ApiStatus.RateLimited => "deferred — rate limited",
        ApiStatus.SyncDisabled => "deferred — syncing paused by the server",
        ApiStatus.NetworkError => "failed — could not reach the server",
        _ => "failed",
    };

    /// <summary>True only for an accepted upload — the one outcome the log paints green.</summary>
    public static bool IsSuccess(ApiStatus status) => status == ApiStatus.Ok;

    /// <summary>
    /// The full Outcome-column text: the status phrase plus its qualifiers — the wait the server
    /// asked for, and the attempt number when a retry was needed. Qualifiers appear only when
    /// they carry information, so the common first-try success stays one clean word.
    /// </summary>
    public static string OutcomeText(UploadLogEntry entry)
    {
        var text = StatusText(entry.Status);

        if (entry.RetryAfter is { } wait)
            text += $" — retry in {(int)wait.TotalSeconds}s";

        if (entry.Attempt > 1)
            text += $" (attempt {entry.Attempt})";

        return text;
    }

    /// <summary>
    /// Flattens a rejected payload's validation complaints to one line, or null when the server
    /// sent none — form-level errors first, then each failing field with its messages.
    /// </summary>
    public static string? IssuesText(ErrorResponse? error)
    {
        if (error?.Issues is not { } issues)
            return null;

        var parts = new List<string>();

        if (issues.FormErrors is { } formErrors)
        {
            foreach (var formError in formErrors)
                parts.Add(formError);
        }

        if (issues.FieldErrors is { } fieldErrors)
        {
            foreach (var (field, messages) in fieldErrors)
                parts.Add($"{field}: {string.Join("; ", messages)}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// True when the outcome is only a delay the plugin handles by itself (it will retry later).
    /// The log renders these muted: they need no action, unlike refusals, which render red.
    /// </summary>
    public static bool IsDeferral(ApiStatus status) =>
        status is ApiStatus.RateLimited or ApiStatus.SyncDisabled or ApiStatus.NetworkError;

    /// <summary>
    /// Renders the log as plain text for the clipboard, one line per upload — what a user pastes
    /// into Discord when reporting a problem.
    /// </summary>
    /// <remarks>
    /// Deliberately in wire terms — category keys, status enum names, UTC timestamps, invariant
    /// formatting — because the reader is whoever is debugging, and stable identifiers beat
    /// localized display copy there. Contains only what the log already shows: counts and
    /// outcomes, never the ids themselves.
    /// </remarks>
    public static string ClipboardText(
        string pluginVersion, string backendUrl, IReadOnlyList<UploadLogEntry> entries)
    {
        var text = new StringBuilder();

        // The backend matters because it is user-overridable: "you are pointed at the wrong
        // server" is a classic support case that is otherwise invisible in a pasted log.
        text.AppendLine($"XIV Shinies Sync v{pluginVersion} upload log — backend: {backendUrl}");

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            text.Append(entry.At.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(entry.Trigger);
            text.Append(" | ").Append(entry.Status);

            text.Append(" | sent:");
            foreach (var category in entry.Categories)
                text.Append(' ').Append(category.Key).Append('=').Append(category.Count);

            if (entry.Skipped.Count > 0)
            {
                text.Append(" | skipped:");
                foreach (var (key, reason) in entry.Skipped)
                    text.Append(' ').Append(key).Append('=').Append(reason);
            }

            // The same fact the window's gold highlight shows, in text: which categories'
            // contents differ from their previous appearance. Counts alone cannot carry this —
            // a one-for-one item swap keeps the count identical.
            var changed = UploadLogDiff.ChangedCategories(entries, index);
            if (changed.Count > 0)
            {
                text.Append(" | changed:");
                foreach (var key in changed)
                    text.Append(' ').Append(key);
            }

            // Diagnostics, only when they say something: a clean first-try success stays a clean
            // one-liner. The raw HTTP code is skipped on Ok — it can only be 200 there.
            if (entry.Attempt > 1)
                text.Append(" | attempt: ").Append(entry.Attempt);

            if (entry.RetryAfter is { } wait)
                text.Append(" | retryAfter: ").Append((int)wait.TotalSeconds).Append('s');

            if (entry.HttpStatusCode is { } code && entry.Status != ApiStatus.Ok)
                text.Append(" | http: ").Append(code);

            // On every line, not just failures: when relic counts look wrong server-side, the
            // question is which manifest the SUCCESSFUL upload counted against.
            if (entry.ManifestVersion is { } manifest)
                text.Append(" | manifest: ").Append(manifest);

            if (entry.Detail is { } detail)
                text.Append(" | issues: ").Append(detail);

            text.AppendLine();
        }

        return text.ToString();
    }
}
