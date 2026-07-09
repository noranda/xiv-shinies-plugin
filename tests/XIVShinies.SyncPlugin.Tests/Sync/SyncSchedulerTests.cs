using System;
using System.Collections.Generic;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Decides *when* to upload, never *how*. No clock, no game, no network: every method takes the
// current time as an argument, so these tests run instantly instead of sleeping.
public class SyncSchedulerTests
{
    // An arbitrary fixed instant. Tests move time by adding to it, never by waiting.
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SyncScheduler NewScheduler() => new();

    [Fact]
    public void A_fresh_scheduler_has_nothing_to_do()
    {
        Assert.Null(NewScheduler().Poll(T0));
    }

    [Fact]
    public void A_login_request_becomes_due_immediately_and_sweeps_every_category()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        var due = scheduler.Poll(T0);

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Login, due.Trigger);
        // A null category set means "collect everything the user has enabled".
        Assert.Null(due.Categories);
    }

    // Poll hands the work out exactly once. Without this the caller would upload in a tight loop.
    [Fact]
    public void Polling_consumes_the_pending_work()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        Assert.NotNull(scheduler.Poll(T0));
        Assert.Null(scheduler.Poll(T0));
    }

    [Fact]
    public void An_unlock_waits_for_the_debounce_window_to_close()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        // The burst may still be arriving; uploading now would send an incomplete picture.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(4)));

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(5));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Unlock, due.Trigger);
    }

    // A quest turn-in can fire several unlocks a second apart. Each one restarts the window so the
    // whole burst rides in a single upload.
    [Fact]
    public void A_second_unlock_restarts_the_debounce_window()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.NotifyUnlock(CategoryKeys.Minions, T0 + TimeSpan.FromSeconds(3));

        // 5s after the first unlock, but only 2s after the second.
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(5)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(8)));
    }

    // Every category that genuinely unlocked during the burst rides along, and nothing else does.
    // Each one really was acquired just now, so dating them all is correct.
    [Fact]
    public void An_unlock_upload_names_only_the_categories_that_unlocked()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.NotifyUnlock(CategoryKeys.Minions, T0);
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0); // duplicate: coalesced, not repeated

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(5));

        Assert.NotNull(due);
        Assert.Equal<IEnumerable<string>>(
            new SortedSet<string> { CategoryKeys.Minions, CategoryKeys.Mounts },
            new SortedSet<string>(due.Categories!));
    }

    [Fact]
    public void The_interval_sweep_comes_due_once_the_full_sync_period_has_elapsed()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(29)));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
        Assert.Null(due.Categories);
    }

    // The interval clock is anchored when a sweep is ISSUED, not when its upload succeeds. Otherwise
    // one transient 500 during the login sweep would leave the clock unset — and since only a full
    // sweep ever sets it, periodic syncing would be silently dead for the rest of the session.
    [Fact]
    public void A_login_sweep_whose_upload_never_succeeds_still_starts_the_interval_clock()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);

        // The sweep is handed out, then its upload fails: MarkUploaded is never called.
        Assert.Equal(SyncTrigger.Login, scheduler.Poll(T0)!.Trigger);

        // Syncing must recover on its own at the next interval rather than stopping forever.
        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(30));
        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Interval, due.Trigger);
    }

    // An unlock upload carried one category, so it is not a full sweep and must not pretend to be
    // one. Letting it reset the clock would let a steadily-unlocking player never sweep at all.
    [Fact]
    public void An_unlock_upload_does_not_postpone_the_interval_sweep()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);
        scheduler.MarkUploaded(SyncTrigger.Unlock, T0 + TimeSpan.FromMinutes(29));

        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Nothing_is_due_while_the_server_has_told_us_to_wait()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(4)));
    }

    // Backoff defers work; it never throws it away. A 429 during a login sync must not cost the user
    // that sync entirely.
    [Fact]
    public void Work_deferred_by_a_backoff_survives_it()
    {
        var scheduler = NewScheduler();
        scheduler.Request(SyncTrigger.Login, T0);
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        var due = scheduler.Poll(T0 + TimeSpan.FromMinutes(5));

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Login, due.Trigger);
    }

    // The user pressing "Sync now" is the strongest signal there is, so it outranks a queued unlock.
    // It still waits out a backoff: a button must not become a way to hammer a server that said stop.
    [Fact]
    public void A_manual_request_outranks_a_pending_unlock()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Manual, T0 + TimeSpan.FromSeconds(6));

        var due = scheduler.Poll(T0 + TimeSpan.FromSeconds(6));

        Assert.NotNull(due);
        Assert.Equal(SyncTrigger.Manual, due.Trigger);
        Assert.Null(due.Categories);
    }

    // A full sweep covers everything an unlock would have sent, so the queued unlock is redundant.
    [Fact]
    public void A_full_sweep_clears_a_pending_unlock()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Manual, T0);

        Assert.Equal(SyncTrigger.Manual, scheduler.Poll(T0)!.Trigger);
        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Intervals_from_the_server_replace_the_defaults()
    {
        var scheduler = NewScheduler();
        scheduler.ApplyIntervals(new ConfigIntervals {FullSyncMinutes = 10, UnlockDebounceSeconds = 20});
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromSeconds(19)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromSeconds(20)));
    }

    // A misconfigured or hostile /config must not turn the plugin into a request flood, nor stall it
    // forever. The plugin honors the server's cadence only within sane bounds.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Absurd_intervals_are_clamped_rather_than_obeyed(int fullSyncMinutes, int debounceSeconds)
    {
        var scheduler = NewScheduler();
        scheduler.ApplyIntervals(new ConfigIntervals
        {
            FullSyncMinutes = fullSyncMinutes,
            UnlockDebounceSeconds = debounceSeconds,
        });

        Assert.InRange(scheduler.UnlockDebounce, SyncScheduler.MinUnlockDebounce, SyncScheduler.MaxUnlockDebounce);
        Assert.InRange(scheduler.FullSyncInterval, SyncScheduler.MinFullSyncInterval, SyncScheduler.MaxFullSyncInterval);
    }

    // Logging out must discard queued work. An unlock earned on one character, uploaded after
    // switching to another, would file that character's mount under the wrong ContentId hash.
    [Fact]
    public void Reset_discards_queued_work()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);
        scheduler.Request(SyncTrigger.Login, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
    }

    // A backoff is the server talking to the token, not to the character. Switching characters must
    // not be a way to shake off a 429.
    [Fact]
    public void Reset_keeps_a_backoff_in_force()
    {
        var scheduler = NewScheduler();
        scheduler.BackOffUntil(T0 + TimeSpan.FromMinutes(5));

        scheduler.Reset();
        scheduler.Request(SyncTrigger.Login, T0);

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromMinutes(1)));
        Assert.NotNull(scheduler.Poll(T0 + TimeSpan.FromMinutes(5)));
    }

    // Reset also forgets when the last sweep happened, so the next login sweeps immediately rather
    // than waiting out an interval measured against the previous character.
    [Fact]
    public void Reset_does_not_leave_an_interval_pending_from_the_previous_character()
    {
        var scheduler = NewScheduler();
        scheduler.MarkUploaded(SyncTrigger.Login, T0);

        scheduler.Reset();

        Assert.Null(scheduler.Poll(T0 + TimeSpan.FromHours(1)));
    }

    // Clocks move backwards: NTP corrections, and a laptop resuming from sleep. A negative elapsed
    // time must not make a debounce window that never closes.
    [Fact]
    public void A_clock_that_jumps_backwards_still_lets_the_debounce_close()
    {
        var scheduler = NewScheduler();
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0);

        // The clock rewinds an hour, then advances past the window from there.
        scheduler.NotifyUnlock(CategoryKeys.Mounts, T0 - TimeSpan.FromHours(1));

        Assert.NotNull(scheduler.Poll(T0));
    }
}
