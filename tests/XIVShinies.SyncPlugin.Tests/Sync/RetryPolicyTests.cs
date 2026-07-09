using System;
using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Sync;

namespace XIVShinies.SyncPlugin.Tests.Sync;

// Decides what to do after an upload fails. Getting this wrong is how a plugin ends up hammering a
// server that already told it to stop.
public class RetryPolicyTests
{
    // Retrying an identical request that the server refused on principle can only fail again.
    [Theory]
    [InlineData(ApiStatus.InvalidToken)]
    [InlineData(ApiStatus.CharacterNotClaimed)]
    [InlineData(ApiStatus.InvalidPayload)]
    [InlineData(ApiStatus.PayloadTooLarge)]
    [InlineData(ApiStatus.MethodNotAllowed)]
    [InlineData(ApiStatus.NotConfigured)]
    public void Never_retries_a_terminal_failure(ApiStatus status)
    {
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 0));
    }

    [Fact]
    public void Does_not_retry_a_success()
    {
        Assert.False(RetryPolicy.ShouldRetryNow(ApiStatus.Ok, attempt: 0));
    }

    // Writes are idempotent server-side, so one immediate retry of a transient failure is safe.
    [Theory]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.NetworkError)]
    public void Retries_a_transient_failure_exactly_once(ApiStatus status)
    {
        Assert.True(RetryPolicy.ShouldRetryNow(status, attempt: 0));
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 1));
    }

    // 429 and 503 are the server saying "wait". They are never retried in-place; the caller stops
    // uploading until the backoff expires.
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void Never_retries_immediately_when_told_to_back_off(ApiStatus status)
    {
        Assert.False(RetryPolicy.ShouldRetryNow(status, attempt: 0));
    }

    [Fact]
    public void Honors_the_servers_retry_after_when_it_sends_one()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var until = RetryPolicy.BackoffUntil(
            ApiStatus.RateLimited, TimeSpan.FromSeconds(90), now);

        Assert.Equal(now.AddSeconds(90), until);
    }

    // The contract advertises Retry-After on 429/503, but a proxy can strip it. Fall back to the
    // documented defaults rather than retrying instantly.
    [Fact]
    public void Falls_back_to_a_short_wait_when_rate_limited_without_a_header()
    {
        var now = DateTimeOffset.UnixEpoch;

        var until = RetryPolicy.BackoffUntil(ApiStatus.RateLimited, retryAfter: null, now);

        Assert.Equal(now.Add(RetryPolicy.DefaultRateLimitBackoff), until);
    }

    // A disabled server usually means it needs breathing room; back off far longer than a poll.
    [Fact]
    public void Falls_back_to_an_hour_when_sync_is_disabled_without_a_header()
    {
        var now = DateTimeOffset.UnixEpoch;

        var until = RetryPolicy.BackoffUntil(ApiStatus.SyncDisabled, retryAfter: null, now);

        Assert.Equal(now.Add(RetryPolicy.DefaultSyncDisabledBackoff), until);
    }

    [Fact]
    public void Imposes_no_backoff_for_statuses_that_are_not_a_wait_instruction()
    {
        var now = DateTimeOffset.UnixEpoch;

        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.Ok, null, now));
        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.ServerError, null, now));
        Assert.Null(RetryPolicy.BackoffUntil(ApiStatus.InvalidToken, null, now));
    }

    // A 401 never heals on retry: the token is gone. Syncing must stop until the user pastes a new
    // one, rather than quietly retrying every interval forever.
    [Fact]
    public void An_invalid_token_stops_syncing_until_the_user_intervenes()
    {
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.InvalidToken));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.CharacterNotClaimed));
        Assert.True(RetryPolicy.RequiresUserAction(ApiStatus.NotConfigured));

        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.ServerError));
        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.RateLimited));
        Assert.False(RetryPolicy.RequiresUserAction(ApiStatus.Ok));
    }
}
