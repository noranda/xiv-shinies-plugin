using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// One pass over the registered collectors: what was read, and what was not.
/// </summary>
public sealed record CollectionSnapshot
{
    /// <summary>
    /// The facts, keyed by category. Goes straight into the sync payload's <c>collections</c>
    /// object. A category that could not be read is simply absent.
    /// </summary>
    public required Dictionary<string, JsonNode> Collections { get; init; }

    /// <summary>
    /// Why each omitted category was omitted, keyed by category. The settings UI turns these into
    /// hints (for example "open your Achievements window once"); nothing else interprets them.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Skipped { get; init; }

    /// <summary>
    /// How long each collector took, keyed by category. Only collectors that actually ran appear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collection happens on the game's framework thread, which has roughly 16ms to produce a frame.
    /// Every collector spends part of that budget, and the plugin is expected to grow more of them —
    /// so the cost has to be <b>visible</b>, not assumed. The orchestrator logs these, which means a
    /// contributor who adds an expensive collector sees its price in <c>/xllog</c> on the first sweep
    /// rather than discovering it as a stutter report.
    /// </para>
    /// <para>
    /// Measured here rather than logged here on purpose: this class holds no Dalamud services, which
    /// is what keeps it unit-testable. It reports the numbers; the caller decides what to do with them.
    /// </para>
    /// </remarks>
    // Not `required`: a snapshot assembled in a test need not care about timings, and an empty
    // dictionary is the honest default for "nothing was measured".
    public IReadOnlyDictionary<string, TimeSpan> Durations { get; init; } =
        new Dictionary<string, TimeSpan>();

    /// <summary>
    /// Per-source scan status (inventory live, saddlebag cached, retainers unscanned, etc.),
    /// merged from every collector that reported source notes this pass.
    /// </summary>
    /// <remarks>
    /// A dictionary (never null) because empty is meaningful: "no collector reported any source
    /// status this pass" is a real answer, and callers can iterate it unconditionally. The merge
    /// rules live at the merge site in <see cref="CollectorRunner.Run"/>.
    /// </remarks>
    // Not `required`: a snapshot assembled in a test need not care about source notes, and an empty
    // dictionary is the honest default for "nothing was reported".
    public IReadOnlyDictionary<string, ItemSourceStatus> SourceNotes { get; init; } =
        new Dictionary<string, ItemSourceStatus>();
}

/// <summary>
/// Runs every registered collector and assembles the snapshot.
/// </summary>
/// <remarks>
/// Contains <b>no category names</b>. It gates each collector by its own key, asks it for facts,
/// and files the answer under that same key. Adding a collection therefore needs no change here.
/// </remarks>
public static class CollectorRunner
{
    /// <summary>Collects from every enabled collector.</summary>
    /// <param name="collectors">
    /// The registered collectors. Passed in rather than constructed here: the real ones hold
    /// Dalamud services, and building them inside this class would make it impossible to test.
    /// </param>
    /// <param name="settings">The user's persisted choices.</param>
    /// <param name="remoteConfig">The latest <c>/config</c>, or null if not fetched yet.</param>
    /// <remarks>Reads game state through the collectors, so call this on the framework thread.</remarks>
    public static CollectionSnapshot Run(
        IEnumerable<ICollector> collectors, PluginSettings settings, ConfigResponse? remoteConfig)
    {
        var collections = new Dictionary<string, JsonNode>();
        var skipped = new Dictionary<string, string>();
        var durations = new Dictionary<string, TimeSpan>();
        var sourceNotes = new Dictionary<string, ItemSourceStatus>();

        // Built once and shared: every collector sees the same view of the world for this pass.
        // EnabledItemGroupKeys carries the user's per-group opt-ins so the item collector scans only
        // the groups they consented to (CollectContext.ItemManifest unions the enabled groups).
        var context = new CollectContext
        {
            RemoteConfig = remoteConfig,
            EnabledItemGroupKeys = new HashSet<string>(settings.EnabledItemGroupKeys),
        };

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            // Ask before reading: a disabled category must cost nothing, not even a game lookup.
            if (!CollectorGate.IsEnabled(key, settings, remoteConfig))
            {
                skipped[key] = CollectSkipReasons.Disabled;
                continue;
            }

            // A raw timestamp rather than a Stopwatch object: no allocation, and this runs inside the
            // game's per-frame loop. `Stopwatch.GetElapsedTime` converts the pair into a TimeSpan.
            var startedAt = Stopwatch.GetTimestamp();

            CollectResult result;
            try
            {
                result = collector.Collect(context);
            }
            // A deliberately broad catch: one misbehaving collector must not abort the whole
            // snapshot. Its category is simply omitted, which the server reads as "not read this
            // time" — never as "cleared".
            //
            // Note what this does NOT protect against. It catches ordinary managed exceptions only.
            // A corrupted-state exception — such as an AccessViolationException from a bad pointer
            // read inside a collector that walks game memory — is not delivered to a managed catch
            // in .NET, and terminates the process regardless. The guard against that is the
            // framework-thread check inside those collectors, not this try/catch.
            catch (Exception)
            {
                // Timed even on the failure path: a collector that is slow *and* throws is exactly
                // the one worth seeing in the log.
                durations[key] = Stopwatch.GetElapsedTime(startedAt);
                skipped[key] = CollectSkipReasons.CollectorError;
                continue;
            }

            durations[key] = Stopwatch.GetElapsedTime(startedAt);

            if (result.WasCollected)
            {
                // Note this includes an EMPTY list, which is a real fact ("I looked, there was
                // nothing"), unlike a skip.
                collections[key] = result.Facts!;

                // Merge source notes from this collector. Source-keyed: if two collectors both report
                // on the same source (e.g., both describe inventory), the last one wins because they
                // describe the same physical storage location. The snapshot iteration order means
                // "last" is the order collectors were registered.
                if (result.SourceNotes is not null)
                {
                    foreach (var (sourceKey, status) in result.SourceNotes)
                    {
                        sourceNotes[sourceKey] = status;
                    }
                }
            }
            else
            {
                skipped[key] = result.SkipReason ?? CollectSkipReasons.CollectorError;
            }
        }

        return new CollectionSnapshot
        {
            Collections = collections,
            Skipped = skipped,
            Durations = durations,
            SourceNotes = sourceNotes,
        };
    }
}
