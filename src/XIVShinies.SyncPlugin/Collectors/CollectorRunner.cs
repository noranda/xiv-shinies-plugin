using System;
using System.Collections.Generic;
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

        // Built once and shared: every collector sees the same view of the world for this pass.
        var context = new CollectContext { RemoteConfig = remoteConfig };

        foreach (var collector in collectors)
        {
            var key = collector.CategoryKey;

            // Ask before reading: a disabled category must cost nothing, not even a game lookup.
            if (!CollectorGate.IsEnabled(key, settings, remoteConfig))
            {
                skipped[key] = CollectSkipReasons.Disabled;
                continue;
            }

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
                skipped[key] = CollectSkipReasons.CollectorError;
                continue;
            }

            if (result.WasCollected)
            {
                // Note this includes an EMPTY list, which is a real fact ("I looked, there was
                // nothing"), unlike a skip.
                collections[key] = result.Facts!;
            }
            else
            {
                skipped[key] = result.SkipReason ?? CollectSkipReasons.CollectorError;
            }
        }

        return new CollectionSnapshot { Collections = collections, Skipped = skipped };
    }
}
