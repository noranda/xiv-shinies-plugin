using Xunit;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Tests.Api;

// Maps raw HTTP status codes onto the meanings the server contract assigns them, and classifies
// how the client must react. Getting this table right is what keeps the plugin from hammering a
// server that told it to stop (401 never heals; 429/503 mean back off).
public class ApiStatusTests
{
    [Theory]
    [InlineData(200, ApiStatus.Ok)]
    [InlineData(400, ApiStatus.InvalidPayload)]
    [InlineData(401, ApiStatus.InvalidToken)]
    [InlineData(403, ApiStatus.CharacterNotClaimed)]
    [InlineData(405, ApiStatus.MethodNotAllowed)]
    [InlineData(413, ApiStatus.PayloadTooLarge)]
    [InlineData(429, ApiStatus.RateLimited)]
    [InlineData(500, ApiStatus.ServerError)]
    [InlineData(503, ApiStatus.SyncDisabled)]
    [InlineData(418, ApiStatus.Unknown)]  // anything the contract doesn't define
    public void Maps_http_status_codes_to_contract_meanings(int httpStatus, ApiStatus expected)
    {
        Assert.Equal(expected, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // "Terminal" = retrying the identical request cannot help; stop and surface it to the user.
    [Theory]
    [InlineData(ApiStatus.InvalidToken)]         // 401 never heals on retry
    [InlineData(ApiStatus.CharacterNotClaimed)]  // user must claim the character on the website
    [InlineData(ApiStatus.InvalidPayload)]       // our bug — the same body will fail again
    [InlineData(ApiStatus.PayloadTooLarge)]      // must split the upload, not repeat it
    [InlineData(ApiStatus.MethodNotAllowed)]     // a bug in this plugin; the call is wrong
    [InlineData(ApiStatus.NotConfigured)]        // never even sent; fix the settings first
    public void Terminal_statuses_must_not_be_retried(ApiStatus status)
    {
        Assert.True(ApiStatusMap.IsTerminal(status));
        Assert.False(ApiStatusMap.IsRetryable(status));
    }

    // NotConfigured is produced by the client before any request leaves the machine, so no HTTP
    // status code may ever map onto it.
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(503)]
    [InlineData(418)]
    public void NotConfigured_is_never_produced_from_an_http_status(int httpStatus)
    {
        Assert.NotEqual(ApiStatus.NotConfigured, ApiStatusMap.FromHttpStatusCode(httpStatus));
    }

    // "Back off" = the server explicitly told us to wait (it sends Retry-After).
    [Theory]
    [InlineData(ApiStatus.RateLimited)]
    [InlineData(ApiStatus.SyncDisabled)]
    public void Backoff_statuses_are_not_terminal_and_not_immediately_retryable(ApiStatus status)
    {
        Assert.True(ApiStatusMap.ShouldBackOff(status));
        Assert.False(ApiStatusMap.IsTerminal(status));
        Assert.False(ApiStatusMap.IsRetryable(status));
    }

    // Transient failures we may retry once with backoff.
    [Theory]
    [InlineData(ApiStatus.ServerError)]
    [InlineData(ApiStatus.NetworkError)]
    public void Transient_failures_are_retryable(ApiStatus status)
    {
        Assert.True(ApiStatusMap.IsRetryable(status));
        Assert.False(ApiStatusMap.IsTerminal(status));
    }

    [Fact]
    public void Ok_is_neither_terminal_nor_retryable_nor_backoff()
    {
        Assert.False(ApiStatusMap.IsTerminal(ApiStatus.Ok));
        Assert.False(ApiStatusMap.IsRetryable(ApiStatus.Ok));
        Assert.False(ApiStatusMap.ShouldBackOff(ApiStatus.Ok));
    }
}
