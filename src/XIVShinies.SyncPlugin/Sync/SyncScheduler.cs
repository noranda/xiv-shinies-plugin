using System;
using System.Collections.Generic;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// Decides <b>when</b> the plugin should upload, and what it should collect when it does.
/// </summary>
/// <remarks>
/// <para>
/// Pure bookkeeping: it never reads the game, never touches the network, and never asks the clock
/// what time it is. Every method takes <c>now</c> as an argument instead, which is what lets the
/// whole thing be unit-tested without waiting real seconds. The caller (<c>SyncManager</c>) owns the
/// clock and does the actual work.
/// </para>
/// <para>
/// Three sources of work, in descending priority: a user-driven request (<c>manual</c>, <c>login</c>),
/// a debounced burst of unlock events, and the periodic full sweep. A backoff deadline suppresses all
/// three without discarding any of them.
/// </para>
/// </remarks>
public sealed class SyncScheduler
{
    /// <summary>The shortest unlock debounce the plugin will honor.</summary>
    public static readonly TimeSpan MinUnlockDebounce = TimeSpan.FromSeconds(1);

    /// <summary>The longest unlock debounce the plugin will honor.</summary>
    public static readonly TimeSpan MaxUnlockDebounce = TimeSpan.FromSeconds(60);

    /// <summary>The shortest full-sweep interval the plugin will honor.</summary>
    public static readonly TimeSpan MinFullSyncInterval = TimeSpan.FromMinutes(5);

    /// <summary>The longest full-sweep interval the plugin will honor.</summary>
    public static readonly TimeSpan MaxFullSyncInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Guards every field below. <c>NotifyUnlock</c> and <c>Poll</c> run on the framework thread,
    /// but <c>MarkUploaded</c> and <c>BackOffUntil</c> run on a background task once the HTTP call
    /// finishes, so the state really is touched from more than one thread.
    /// </summary>
    /// <remarks>
    /// C# note: <c>lock (x) { … }</c> lets one thread at a time into the block. JavaScript has no
    /// equivalent because its runtime is single-threaded — this is the cost of real concurrency.
    /// </remarks>
    private readonly object gate = new();

    /// <summary>Categories unlocked since the last upload, deduplicated.</summary>
    private readonly HashSet<string> pendingUnlockCategories = [];

    /// <summary>When the current debounce window closes. Meaningless if no categories are pending.</summary>
    private DateTimeOffset unlockDueAt;

    /// <summary>A queued full sweep, if the user (or a login) asked for one.</summary>
    private SyncTrigger? pendingFullSweep;

    /// <summary>When the last full sweep happened. Null until one has, so the interval never fires first.</summary>
    private DateTimeOffset? lastFullSweepAt;

    /// <summary>The moment a server-instructed backoff expires. Null when not backing off.</summary>
    private DateTimeOffset? backoffUntil;

    /// <summary>How long to wait after an unlock before uploading, batching the burst.</summary>
    public TimeSpan UnlockDebounce { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to run a full-sweep upload.</summary>
    public TimeSpan FullSyncInterval { get; private set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Adopts the server's cadence from <c>/config</c>, clamped to a sane range.
    /// </summary>
    /// <remarks>
    /// The server chooses the cadence, but it does not get to choose an absurd one. A zero or
    /// negative interval would turn the plugin into a request flood against the very server that
    /// configured it, and an enormous one would silently stop it syncing forever. Clamping keeps a
    /// misconfiguration (or a compromised backend, since the backend URL is user-overridable) from
    /// becoming either.
    /// </remarks>
    public void ApplyIntervals(ConfigIntervals intervals)
    {
        lock (gate)
        {
            UnlockDebounce = Clamp(
                TimeSpan.FromSeconds(intervals.UnlockDebounceSeconds), MinUnlockDebounce, MaxUnlockDebounce);
            FullSyncInterval = Clamp(
                TimeSpan.FromMinutes(intervals.FullSyncMinutes), MinFullSyncInterval, MaxFullSyncInterval);
        }
    }

    /// <summary>Queues a full sweep. Only <see cref="SyncTrigger.Login"/> and <see cref="SyncTrigger.Manual"/> qualify.</summary>
    /// <exception cref="ArgumentOutOfRangeException">For a trigger the caller does not get to request.</exception>
    public void Request(SyncTrigger trigger, DateTimeOffset now)
    {
        if (trigger is not (SyncTrigger.Login or SyncTrigger.Manual))
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Only login and manual sweeps can be requested.");

        lock (gate)
        {
            // A manual request outranks a queued login: the user is standing at the window watching.
            if (pendingFullSweep is null || Rank(trigger) > Rank(pendingFullSweep.Value))
                pendingFullSweep = trigger;
        }
    }

    /// <summary>Records that a category just unlocked, and (re)opens the debounce window.</summary>
    /// <remarks>
    /// The window <b>slides</b>: each new unlock pushes the deadline out, so a quest turn-in that
    /// fires a mount, a minion, and three achievements a second apart produces one upload rather
    /// than five. Assigning the deadline outright (rather than keeping the later of the two) also
    /// means a clock that jumps backwards cannot leave a window that never closes.
    /// </remarks>
    public void NotifyUnlock(string categoryKey, DateTimeOffset now)
    {
        lock (gate)
        {
            pendingUnlockCategories.Add(categoryKey);
            unlockDueAt = now + UnlockDebounce;
        }
    }

    /// <summary>Suppresses all work until the given moment, as instructed by the server.</summary>
    /// <remarks>
    /// Pending work is deferred, never dropped — a 429 mid-login must not cost that sync. Note the
    /// deadline is absolute, so a clock that jumps backwards lengthens a backoff rather than ending
    /// it early. That errs toward waiting too long, which is the safe direction against a server that
    /// asked us to stop; the interval clock, where over-waiting costs the user data, is clamped instead.
    /// </remarks>
    public void BackOffUntil(DateTimeOffset until)
    {
        lock (gate)
        {
            // Never shorten a backoff already in force.
            if (backoffUntil is null || until > backoffUntil)
                backoffUntil = until;
        }
    }

    /// <summary>Forgets all queued work, as when the player logs out.</summary>
    /// <remarks>
    /// <para>
    /// Queued work belongs to the character it was queued for. A mount unlocked just before logout,
    /// uploaded after switching characters, would be filed under the wrong ContentId hash — the
    /// server would record it against a character that never earned it.
    /// </para>
    /// <para>
    /// The backoff deadline deliberately survives, because it is the server talking to the
    /// <i>token</i>, not to the character. Switching characters must not become a way to shake off a
    /// rate limit. The cadence survives too: it came from <c>/config</c> and has nothing to do with
    /// who is logged in.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        lock (gate)
        {
            pendingUnlockCategories.Clear();
            pendingFullSweep = null;

            // Forgetting the last sweep means the next login sweeps on its own merits, rather than
            // inheriting an interval measured against the previous character.
            lastFullSweepAt = null;
        }
    }

    /// <summary>Records that an upload completed, restarting the interval clock if it was a full sweep.</summary>
    /// <remarks>
    /// An <c>unlock</c> upload carried only the categories that changed, so it is not a sweep and must
    /// not postpone the next one. Otherwise a player who keeps unlocking things would never run a full
    /// sweep, and the categories nothing unlocked in would drift out of date indefinitely.
    /// </remarks>
    public void MarkUploaded(SyncTrigger trigger, DateTimeOffset now)
    {
        lock (gate)
        {
            if (trigger is not SyncTrigger.Unlock)
                lastFullSweepAt = now;
        }
    }

    /// <summary>
    /// Returns the work that is due right now and removes it from the queue, or null if nothing is.
    /// </summary>
    /// <remarks>
    /// Called once per framework tick. It hands each piece of work out exactly once, so the caller
    /// cannot accidentally upload the same thing on every frame.
    /// </remarks>
    public SyncDue? Poll(DateTimeOffset now)
    {
        lock (gate)
        {
            // The server said wait. Nothing is due, and nothing queued is lost.
            if (backoffUntil is { } until)
            {
                if (now < until)
                    return null;

                backoffUntil = null;
            }

            // A clock that jumped backwards past the last sweep would otherwise delay the next one by
            // however far it jumped. Treat the earlier "now" as the new reference point.
            if (lastFullSweepAt is { } lastSweep && now < lastSweep)
                lastFullSweepAt = now;

            // 1. A user-driven sweep wins. It also covers whatever the pending unlock would have
            //    sent, so that unlock is redundant and is dropped rather than uploaded twice.
            if (pendingFullSweep is { } requested)
            {
                pendingFullSweep = null;
                pendingUnlockCategories.Clear();

                // Anchor the interval clock the moment a sweep is ISSUED, not when it succeeds.
                // Otherwise a login sweep whose upload fails transiently would leave this null, and
                // since only a full sweep ever sets it, the periodic sweep below could never become
                // due again — one unlucky 500 at login would silently kill periodic syncing for the
                // whole session. Anchoring here means the next interval simply retries.
                lastFullSweepAt = now;

                return new SyncDue {Trigger = requested};
            }

            // 2. A settled burst of unlocks: upload exactly the categories that changed.
            if (pendingUnlockCategories.Count > 0 && now >= unlockDueAt)
            {
                var categories = new HashSet<string>(pendingUnlockCategories);
                pendingUnlockCategories.Clear();
                return new SyncDue {Trigger = SyncTrigger.Unlock, Categories = categories};
            }

            // 3. The periodic sweep. Stamping the clock here rather than in MarkUploaded means a
            //    failed upload waits out the next interval instead of being retried every tick.
            //
            //    It also drops any unlock still inside its debounce window. No fact is lost: the
            //    sweep is about to report the very same ids. The only thing the unlock upload would
            //    have added is an acquisition timestamp for the categories it named — and the
            //    contract does not promise that a later upload can supply a date for a row an earlier
            //    upload already created. So we neither rely on that nor spend a request finding out.
            if (lastFullSweepAt is { } sweptAt && now >= sweptAt + FullSyncInterval)
            {
                lastFullSweepAt = now;
                pendingUnlockCategories.Clear();
                return new SyncDue {Trigger = SyncTrigger.Interval};
            }

            return null;
        }
    }

    /// <summary>Ranks the full-sweep triggers so the stronger user signal wins.</summary>
    private static int Rank(SyncTrigger trigger) => trigger switch
    {
        SyncTrigger.Manual => 2,
        _ => 1,
    };

    /// <summary>
    /// Keeps a value within bounds. <c>TimeSpan</c> is comparable, so this is a plain three-way test.
    /// </summary>
    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
