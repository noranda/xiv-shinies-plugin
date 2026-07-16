using System;
using System.Threading;
using System.Threading.Tasks;
using XIVShinies.SyncPlugin.Api;

namespace XIVShinies.SyncPlugin.Windows;

/// <summary>
/// Runs the <c>GET /me</c> probe for the onboarding wizard, off the framework thread.
/// </summary>
/// <remarks>
/// <para>
/// The wizard's "Verify" button is drawn inside <c>Draw()</c>, which runs once per rendered frame.
/// Awaiting an HTTP call there would stall the game for the length of a network round trip. So the
/// probe is started as a background task, its answer is parked in a field, and the next frame's
/// <c>Draw()</c> picks it up. A window redraws roughly sixty times a second, so "next frame" is
/// imperceptible.
/// </para>
/// <para>
/// This is the same pattern the uploader uses, for the same reason. It is not a unit-testable class —
/// it owns a live <see cref="ApiClient"/> — but it holds no decisions either. Which state the answer
/// means is decided by <see cref="Onboarding.TokenCheck"/>, which is pure.
/// </para>
/// </remarks>
internal sealed class TokenVerifier : IDisposable
{
    private readonly ApiClient apiClient;

    /// <summary>Cancelled when the window is torn down, so a probe in flight stops.</summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Captured before the source can be disposed; reading `.Token` afterwards throws.</summary>
    private readonly CancellationToken lifetimeToken;

    /// <summary>
    /// The probe's answer, or null while none has arrived.
    /// </summary>
    /// <remarks>
    /// Written by the background task, read by the drawing thread, so it must be <c>volatile</c>:
    /// without it the reader may keep a stale copy in a register and never see the result land.
    /// </remarks>
    private volatile ApiResponse<MeResponse>? result;

    /// <summary>True while a probe is running. Same cross-thread reasoning as above.</summary>
    private volatile bool inFlight;

    /// <summary>
    /// Bumped whenever the question changes, so an answer to an old question can be recognized and
    /// thrown away.
    /// </summary>
    /// <remarks>
    /// A probe cannot be recalled once it is in the air. Without this counter, the following happens:
    /// the user verifies token A, and while the request is in flight edits the box to token B. The
    /// answer for A then lands, and the window cheerfully reports "Token accepted" beside token B —
    /// which was never checked and never saved. The generation makes that answer identifiably stale.
    /// </remarks>
    private volatile int generation;

    public TokenVerifier(ApiClient apiClient)
    {
        this.apiClient = apiClient;
        lifetimeToken = lifetime.Token;
    }

    /// <summary>True while a probe is in flight.</summary>
    public bool InFlight => inFlight;

    /// <summary>Starts a probe. Does nothing if one is already running.</summary>
    public void Start()
    {
        if (inFlight)
            return;

        result = null;
        inFlight = true;

        // Capture the question this answer will belong to. Read once, so the task compares against
        // the generation it was started for rather than whatever the field holds when it finishes.
        var startedFor = ++generation;

        // No token passed to Task.Run: an already-cancelled one would make it skip the delegate
        // entirely, so the `finally` that clears `inFlight` would never run and the button would stay
        // stuck on "Checking..." forever. Cancellation is observed inside the task instead.
        _ = Task.Run(() => ProbeAsync(startedFor));
    }

    /// <summary>
    /// Takes the probe's answer if one has arrived, clearing it so it is consumed exactly once.
    /// </summary>
    /// <returns>The answer, or null when none is waiting.</returns>
    public ApiResponse<MeResponse>? TakeResult()
    {
        var answer = result;
        if (answer is not null)
            result = null;

        return answer;
    }

    /// <summary>
    /// Discards the answer to the previous question, as when the user edits the token.
    /// </summary>
    /// <remarks>
    /// Bumping the generation is what makes this work on a probe that is still in the air: it cannot
    /// be recalled, but when it lands it will find the generation changed and discard itself. Clearing
    /// <see cref="result"/> alone would only drop an answer that had already arrived.
    /// </remarks>
    public void Forget()
    {
        generation++;
        result = null;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    /// <param name="startedFor">The generation this probe was started for.</param>
    private async Task ProbeAsync(int startedFor)
    {
        try
        {
            var answer = await apiClient.GetMeAsync(lifetimeToken).ConfigureAwait(false);

            // The token this answer describes may no longer be the token in the box. Publishing it
            // would tell the user their current, unchecked token was accepted.
            if (startedFor == generation)
                result = answer;
        }
        catch (OperationCanceledException)
        {
            // The window closed mid-probe. Nothing to report.
        }
        catch (Exception)
        {
            // ApiClient turns every failure into a status rather than an exception, so reaching here
            // means something genuinely unexpected happened. Surface it as an unreachable server
            // rather than letting it escape a discarded task.
            if (startedFor == generation)
                result = new ApiResponse<MeResponse> { Status = ApiStatus.Unknown };
        }
        finally
        {
            // Cleared unconditionally, even for a superseded probe: this one really has finished, and
            // leaving the flag set would block every future probe.
            inFlight = false;
        }
    }
}
