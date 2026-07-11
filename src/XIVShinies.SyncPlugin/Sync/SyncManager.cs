using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Sync;

/// <summary>
/// The orchestrator: listens to the game, decides nothing itself, and does the work.
/// </summary>
/// <remarks>
/// <para>
/// Every policy question this class faces is answered somewhere else, by a class with no game and no
/// network in it: <see cref="UploadGate"/> says whether an upload is allowed, <see cref="SyncScheduler"/>
/// says when and what, <see cref="RetryPolicy"/> says what to do about a failure,
/// <see cref="CollectorSelection"/> says which collectors run, and <see cref="SyncPayloadBuilder"/>
/// says what the body looks like. That is what makes those parts unit-testable, and it is why this
/// class cannot be — it holds Dalamud services, so merely constructing it requires the game process.
/// It is verified by in-game QA instead.
/// </para>
/// <para>
/// <b>Threading.</b> Two worlds meet here. Game state may only be read on the framework thread, and
/// HTTP may never run on it. So collection happens inside the per-frame <c>Update</c> handler, and
/// the resulting payload — a plain object with no game handles left in it — is uploaded on a
/// background task. Nothing ever blocks the framework thread waiting for a response.
/// </para>
/// <para>
/// <b>Nothing here fires on its own before the user opts in.</b> The constructor subscribes to game
/// events, but every path out of them runs through <see cref="UploadGate"/> first.
/// </para>
/// </remarks>
internal sealed class SyncManager : IDisposable
{
    /// <summary>How often to re-fetch <c>/config</c>, per the contract's "roughly every 30 minutes".</summary>
    private static readonly TimeSpan ConfigPollInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to wait after the login event before reading the character.
    /// </summary>
    /// <remarks>
    /// The <c>Login</c> event fires early. Unlock bitmaps, the achievement list, and inventory all
    /// stream in over the following seconds, so collecting immediately would read a half-populated
    /// world and report far less than the character owns. That would not be <i>wrong</i> — absence
    /// never clears anything server-side — but it would waste an upload and delay the real one by a
    /// full interval. Waiting costs nothing.
    /// </remarks>
    private static readonly TimeSpan LoginSettleDelay = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before re-reading a character whose details were not usable yet.</summary>
    private static readonly TimeSpan IdentityRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>How many times to re-read an unusable character before giving up until next login.</summary>
    private const int MaxIdentityAttempts = 12;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IUnlockState unlockState;
    private readonly IPluginLog log;
    private readonly ApiClient apiClient;
    private readonly PluginSettings settings;
    private readonly IReadOnlyList<ICollector> collectors;
    private readonly string pluginVersion;

    /// <summary>The source of "now". Injected so the schedule never depends on the wall clock in tests.</summary>
    private readonly TimeProvider timeProvider;

    private readonly SyncScheduler scheduler = new();

    // What each recent upload sent and how the server answered — the settings window's upload
    // log. In memory only; see UploadLog for why it is deliberately not persisted.
    private readonly UploadLog uploadLog = new();

    // True once the FIRST /config poll of this plugin load has answered, successfully or not.
    // Sweeps are held until then (see DispatchDueWork): the item manifest lives only in memory,
    // so without the hold the session's first sweep races the fetch and runs manifest-less.
    // Volatile: written by the poll task, read by the framework tick.
    private volatile bool firstConfigAnswerSeen;

    /// <summary>
    /// Cancelled on unload, so an upload in flight when the plugin is torn down stops rather than
    /// completing against disposed state.
    /// </summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>
    /// A copy of <see cref="lifetime"/>'s token, taken before it can ever be disposed.
    /// </summary>
    /// <remarks>
    /// Reading <c>lifetime.Token</c> after the source is disposed throws. Background work started
    /// before <see cref="Dispose"/> may still be running when that happens, so the token is captured
    /// once here. A token whose source was cancelled and then disposed is still safe to observe.
    /// </remarks>
    private readonly CancellationToken lifetimeToken;

    // `volatile` on the next four fields is not decoration. Each is written on one thread and read on
    // another (the background task writes, the framework or UI thread reads, or vice versa). Without
    // it the compiler or CPU may keep a stale copy in a register and never observe the update.

    /// <summary>The most recent <c>/config</c>, or null until the first poll succeeds.</summary>
    private volatile ConfigResponse? remoteConfig;

    /// <summary>True while an upload is in flight, so the framework thread never starts a second one.</summary>
    private volatile bool uploadInFlight;

    /// <summary>True while a <c>/config</c> poll is in flight.</summary>
    private volatile bool configPollInFlight;

    /// <summary>
    /// Set when the server reported a failure only the user can fix. Suppresses all further work
    /// until the user intervenes, rather than looping against a server that will keep refusing.
    /// </summary>
    private volatile bool blockedPendingUserAction;

    /// <summary>
    /// The last outcome as an <see cref="ApiStatus"/> cast to int, or -1 when nothing has completed.
    /// </summary>
    /// <remarks>
    /// An int rather than an <c>ApiStatus?</c> because <c>volatile</c> cannot be applied to a
    /// nullable enum. Written by the background task, read by the UI thread.
    /// </remarks>
    private volatile int lastStatusCode = -1;

    /// <summary>
    /// When the last successful upload completed, boxed, or null if none has this session.
    /// </summary>
    /// <remarks>
    /// A boxed <see cref="DateTimeOffset"/> rather than a plain field because <c>volatile</c> cannot
    /// be applied to a struct type — but a reference write is atomic, so boxing buys a safe
    /// cross-thread handoff. Written by the background task, read by the UI.
    /// </remarks>
    private volatile object? lastSyncedAtBox;

    /// <summary>
    /// Bumped on logout, so a response that arrives for a character who already left can be
    /// recognized and discarded.
    /// </summary>
    /// <remarks>
    /// A request in flight cannot be recalled: logout does not cancel it (only plugin unload does),
    /// so its answer can land seconds later — and without this counter that late answer would write
    /// the <i>previous</i> character's status, or worse a halt, onto the next one's session. Same pattern
    /// as <see cref="Windows.TokenVerifier"/>: the work captures the generation it was started for,
    /// and an answer to a superseded generation is thrown away. Only the framework thread writes
    /// this (single writer, so the non-atomic increment is safe); background tasks only read it.
    /// </remarks>
    private volatile int sessionGeneration;

    // The next three are WRITTEN only on the framework thread — from the Update handler and from the
    // Login/Logout/Unlock events, which Dalamud raises on that same thread. They therefore need no
    // synchronization between writers. (`identity` is additionally null-checked by the settings window
    // via HasCharacter; a reference read is atomic, so the worst case there is a one-frame-stale
    // status line.) Writing any of them from a background thread would break this reasoning.

    /// <summary>The character to attribute uploads to. Null whenever nobody is logged in.</summary>
    private CharacterIdentity? identity;

    /// <summary>When the login settle delay expires, or null when not waiting for one.</summary>
    private DateTimeOffset? loginSettledAt;

    /// <summary>How many times the character has been read but found unusable since the last login.</summary>
    private int identityAttempts;

    /// <summary>When the next <c>/config</c> poll is due. Null means "poll at the first opportunity".</summary>
    private DateTimeOffset? nextConfigPollAt;

    /// <summary>
    /// Why each category was omitted from the most recent collection pass, keyed by category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept so the settings window can explain a category that never appears — for example, that the
    /// achievements list must be opened once before the game will answer for it. The window renders
    /// whatever reason it is handed, which is how that hint reaches the user without anyone writing a
    /// branch on a category name.
    /// </para>
    /// <para>
    /// <c>volatile</c>, and replaced wholesale rather than mutated. The writer is the collection pass
    /// on the framework thread; the reader is the window's draw call, which Dalamud may or may not
    /// invoke on that same thread. Rather than depend on the answer, the dictionary handed out is
    /// never touched again — a reader either sees the whole old map or the whole new one, never a map
    /// mid-update. Mutating it in place would be a genuine data race if the threads ever differ.
    /// </para>
    /// </remarks>
    private volatile IReadOnlyDictionary<string, string> lastSkipped = new Dictionary<string, string>();

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing.</summary>
    public SyncManager(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IUnlockState unlockState,
        IPluginLog log,
        ApiClient apiClient,
        PluginSettings settings,
        IReadOnlyList<ICollector> collectors,
        string pluginVersion,
        TimeProvider? timeProvider = null)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.unlockState = unlockState;
        this.log = log;
        this.apiClient = apiClient;
        this.settings = settings;
        this.collectors = collectors;
        this.pluginVersion = pluginVersion;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        lifetimeToken = lifetime.Token;

        // Every `+=` here has a matching `-=` in Dispose. A handler left attached after unload would
        // be invoked against a torn-down plugin.
        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        unlockState.Unlock += OnUnlock;

        // A plugin enabled while already logged in never receives a Login event, so treat that as one.
        if (clientState.IsLoggedIn)
            OnLogin();
    }

    /// <summary>The last upload's outcome, or null if none has completed. For the settings window.</summary>
    public ApiStatus? LastStatus => lastStatusCode < 0 ? null : (ApiStatus)lastStatusCode;

    /// <summary>When the last successful upload completed, or null if none has this session.</summary>
    // `as DateTimeOffset?` unboxes when the box holds a value and yields null when the field is null,
    // in one step.
    public DateTimeOffset? LastSyncedAt => lastSyncedAtBox as DateTimeOffset?;

    /// <summary>
    /// The current character's name, or null when nobody is loaded. Lets the settings window say
    /// "claim <c>Name</c> on the website" instead of something generic.
    /// </summary>
    /// <remarks>A reference read, so atomic; at worst one frame stale, like <see cref="HasCharacter"/>.</remarks>
    public string? CharacterName => identity?.Name;

    /// <summary>True when syncing is halted until the user fixes something (bad token, unclaimed character).</summary>
    public bool BlockedPendingUserAction => blockedPendingUserAction;

    /// <summary>The most recent server config, for the settings window to render category switches from.</summary>
    public ConfigResponse? RemoteConfig => remoteConfig;

    /// <summary>
    /// The current automatic full-sweep cadence, after the server's tuning and the plugin's
    /// clamps — so the settings window can state the real number instead of a hardcoded one.
    /// </summary>
    public TimeSpan FullSyncInterval => scheduler.FullSyncInterval;

    /// <summary>True while an upload is on the wire — lets the Sync now button show it live.</summary>
    /// <remarks>A volatile bool read, so safe from the draw call; at worst one frame stale.</remarks>
    public bool UploadInFlight => uploadInFlight;

    /// <summary>The recent uploads, newest first, for the settings window's upload log.</summary>
    public IReadOnlyList<UploadLogEntry> UploadHistory => uploadLog.Entries;

    /// <summary>Empties the upload log, for the settings window's clear button.</summary>
    public void ClearUploadHistory() => uploadLog.Clear();

    /// <summary>
    /// Why each category was skipped by the last collection pass. Empty before the first pass.
    /// </summary>
    /// <remarks>The returned dictionary is never mutated, so it is safe to read from any thread.</remarks>
    public IReadOnlyDictionary<string, string> LastSkipped => lastSkipped;

    /// <summary>True when a character is loaded and identified, so an upload could actually happen.</summary>
    /// <remarks>
    /// A null check on a reference, which is atomic. The window may read this from the draw call while
    /// the framework thread is writing it; the worst outcome is a status line that is one frame stale.
    /// </remarks>
    public bool HasCharacter => identity is not null;

    /// <summary>Queues an immediate full sweep, as when the user presses "Sync now".</summary>
    /// <remarks>
    /// Clears the "needs user action" halt: pressing the button is the user asserting they fixed it.
    /// It still respects any backoff — a button must not become a way to hammer a server that said stop.
    /// </remarks>
    public void RequestManualSync()
    {
        var now = timeProvider.GetUtcNow();

        blockedPendingUserAction = false;

        // Identity capture can give up (a home world that never resolved), and only a fresh login
        // re-arms it. Without this, "Sync now" would queue a sweep that the `identity is null` guard
        // silently drops on every frame, and the button would appear to do nothing at all.
        if (identity is null && clientState.IsLoggedIn)
        {
            loginSettledAt = now;
            identityAttempts = 0;
        }

        scheduler.Request(SyncTrigger.Manual, now);
    }

    /// <summary>Unsubscribes everything the constructor subscribed to, then cancels work in flight.</summary>
    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        unlockState.Unlock -= OnUnlock;

        // Unsubscribe first, then cancel: no new work can start once the handlers are detached.
        lifetime.Cancel();
        lifetime.Dispose();
    }

    // --- Game events -----------------------------------------------------------------------

    /// <summary>
    /// The character began loading. The identity is deliberately not read yet — at this moment the
    /// game has not necessarily populated it.
    /// </summary>
    private void OnLogin()
    {
        loginSettledAt = timeProvider.GetUtcNow() + LoginSettleDelay;
        identityAttempts = 0;
    }

    /// <summary>The character logged out. Everything character-specific is dropped.</summary>
    /// <remarks>
    /// Dalamud hands this a logout type and code that the plugin has no use for; the signature must
    /// still match the event's delegate.
    /// </remarks>
    private void OnLogout(int type, int code)
    {
        identity = null;
        loginSettledAt = null;
        identityAttempts = 0;

        // The "user must fix this" halt is released on logout, because its most common cause —
        // this character is not claimed on the website — belongs to the character, and the
        // character is leaving. Without this, logging into a properly-claimed character behind an
        // unclaimed one inherits a halt that does not apply to it, and the login sync silently
        // never fires. The token-shaped causes self-heal: a genuinely revoked token earns one 401
        // from the next character's login sync (and one more if a /config poll happens to be due)
        // and halts again — a request or two per relog, never a loop.
        blockedPendingUserAction = false;

        // Responses still in flight belong to the character who just left; bumping the generation
        // makes them discard themselves when they land, instead of re-latching the halt (or a stale
        // status line) onto whoever logs in next.
        sessionGeneration++;

        // The status line describes the previous character's last upload. Left in place it would
        // flash — or, if nothing syncs, stick — on whoever logs in next.
        lastStatusCode = -1;
        lastSyncedAtBox = null;

        // Skip reasons describe the session that just ended — the achievements list, for instance, is
        // unloaded on logout. Carrying them into the next character would show hints about a state
        // that no longer exists.
        lastSkipped = new Dictionary<string, string>();

        // Queued work belongs to the character that queued it. Uploading it after a character switch
        // would attribute one character's unlocks to another.
        scheduler.Reset();
    }

    /// <summary>
    /// The local player unlocked something. Routes it to the collector that owns that sheet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains no category names. It asks each collector "is this row yours?" and files the answer
    /// under that collector's own key, so a new collection needs no change here.
    /// </para>
    /// <para>
    /// Routing rather than sweeping is a correctness requirement, not an optimization. The contract
    /// says an <c>unlock</c> upload "stamps the upload moment as the acquisition time for every
    /// category in it", and an acquisition date is never revised afterwards. Quests are the sharpest
    /// case: a snapshot upload deliberately leaves their date null, so sweeping every category on one
    /// mount unlock would stamp a fake acquisition date onto every quest uploaded for the first time
    /// alongside it — permanently.
    /// </para>
    /// <para>
    /// The <c>identity is null</c> guard is load-bearing for compliance, not just for correctness: it
    /// is what keeps this handler completely inert before the user has opted in, since the identity
    /// is only ever captured on the far side of the upload gate.
    /// </para>
    /// </remarks>
    private void OnUnlock(RowRef rowRef)
    {
        if (identity is null)
            return;

        var now = timeProvider.GetUtcNow();

        foreach (var collector in collectors)
        {
            // Not every collector maps to an unlockable sheet — possession counts, for instance, are
            // not unlocks. Those simply do not implement IUnlockAware and never match.
            //
            // `is IUnlockAware aware` tests the type and binds the converted value in one step.
            if (collector is IUnlockAware aware && aware.Handles(rowRef))
            {
                scheduler.NotifyUnlock(collector.CategoryKey, now);
                return;
            }
        }
    }

    /// <summary>
    /// Runs every frame on the framework thread. Must stay cheap: this is the game's render loop.
    /// </summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        // Consent and a credential, before anything else happens. This is the check that keeps the
        // plugin silent — no network, no game reads — on a fresh install.
        if (!UploadGate.CanContactServer(settings))
            return;

        if (blockedPendingUserAction)
            return;

        var now = timeProvider.GetUtcNow();

        // Read the volatile field ONCE and use the snapshot for the whole frame. A background poll
        // completing mid-frame would otherwise let us collect items against one manifest and stamp
        // the payload with a different manifest's version.
        var config = remoteConfig;

        // Polled even while the kill switch is off — it is how we learn the switch flipped back.
        PollConfigIfDue(now);

        CaptureIdentityIfSettled(now);

        // Hold sweeps until the first config poll of this load has ANSWERED, one way or the
        // other. Without this, the first sweep dispatches on the same tick the fetch starts and
        // collects with no item manifest — skipping the items category and leaving a scary
        // "waiting for XIV Shinies" hint on the settings screen until the next sweep, minutes
        // later. Success populates the manifest; failure lets syncing proceed anyway (the skip
        // hint then describes a server we genuinely could not reach). The scheduler keeps the
        // queued trigger, so the held sweep dispatches on the first tick after the answer —
        // the cost is one config round trip before the session's first upload.
        if (remoteConfig is null && !firstConfigAnswerSeen)
            return;

        if (identity is null || uploadInFlight)
            return;

        // The server's global kill switch, honored locally to save a round trip that would only 503.
        if (!UploadGate.CanUpload(settings, config))
            return;

        var due = scheduler.Poll(now);
        if (due is null)
            return;

        StartUpload(due, config);
    }

    // --- Work ------------------------------------------------------------------------------

    /// <summary>
    /// Reads the character once the login has settled. On the framework thread, as required.
    /// </summary>
    /// <remarks>
    /// The character is committed only when it is <b>usable</b>. An empty name or home world would
    /// fail the contract's length constraints and earn a 400 on every single upload for the rest of
    /// the session — a silent, permanent failure. So an unusable read is retried a bounded number of
    /// times and then abandoned until the next login, rather than cached.
    /// </remarks>
    private void CaptureIdentityIfSettled(DateTimeOffset now)
    {
        // `x is not { } y` reads as "x is null". The positive form, `x is { } y`, means "x is not
        // null, and call the unwrapped value y" — a null test and an unwrap in one step, after which
        // the compiler knows y is non-null. So: bail out if no login is pending, or if it has not
        // settled yet.
        if (loginSettledAt is not { } settledAt || now < settledAt)
            return;

        // The delay elapsed but the game has not populated the player yet. Re-check next frame; this
        // costs two property reads and never logs, so it is safe to do per-frame.
        if (!playerState.IsLoaded || playerState.ContentId == 0)
            return;

        CharacterIdentity candidate;
        try
        {
            // The one-way door. The raw ContentId is hashed here, at the edge, and the plain value
            // never reaches the payload, the log, or the config file.
            candidate = new CharacterIdentity
            {
                ContentIdHash = ContentIdHash.Compute(playerState.ContentId),
                Name = playerState.CharacterName,

                // The home world is a lazy reference into a game data sheet. It can fail to resolve
                // — a sheet still loading, or a world newer than the installed game data.
                HomeWorld = playerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            // A character we cannot identify is a character we must not upload for: the hash is what
            // the server binds the data to, and guessing it would write to the wrong character.
            log.Error(ex, "Could not read the local character; syncing stays idle until next login.");
            loginSettledAt = null;
            return;
        }

        // Never cache an identity the server would reject. See CharacterIdentity.IsUsable.
        if (!CharacterIdentity.IsUsable(candidate.Name, candidate.HomeWorld))
        {
            if (++identityAttempts >= MaxIdentityAttempts)
            {
                log.Warning(
                    "The local character's name or home world never became readable; " +
                    "syncing stays idle until next login.");
                loginSettledAt = null;
                return;
            }

            // Back off and try again, rather than committing an identity every upload will fail on.
            loginSettledAt = now + IdentityRetryDelay;
            return;
        }

        loginSettledAt = null;
        identityAttempts = 0;
        identity = candidate;

        log.Debug("Character loaded; queuing a login sync.");
        scheduler.Request(SyncTrigger.Login, now);
    }

    /// <summary>Collects on the framework thread, then hands the payload to a background upload.</summary>
    /// <param name="due">The work the scheduler handed out. Already consumed — it will not be reissued.</param>
    /// <param name="config">The frame's snapshot of the server config, so it cannot change mid-build.</param>
    private void StartUpload(SyncDue due, ConfigResponse? config)
    {
        CollectionSnapshot snapshot;
        SyncRequest request;
        UploadLogEntry logDraft;

        // The scheduler has already handed this work out, so a throw here would silently lose the
        // sweep. Worse, an exception escaping the Update handler propagates into Dalamud's dispatch.
        // Individual collectors are guarded inside CollectorRunner; this guards everything around them.
        try
        {
            // Reading game state — must happen here, on the framework thread, before anything is async.
            snapshot = CollectorRunner.Run(
                CollectorSelection.For(collectors, due.Categories), settings, config);

            LogCollectionCost(snapshot, due.Trigger);

            // Kept for the settings window. An unlock pass runs only the collectors whose categories
            // changed, so it can speak for those and no others; replacing the whole map would erase
            // every absent category's reason. Merging keeps the last thing each category said about
            // itself.
            RememberSkipReasons(snapshot);

            if (snapshot.Collections.Count == 0)
            {
                // Every category was disabled, skipped, or unreadable. An empty `collections` object
                // is a valid payload, but it asserts nothing, so sending it would only spend a
                // rate-limit slot.
                log.Debug($"Nothing to upload for {due.Trigger}; skipping.");
                return;
            }

            request = SyncPayloadBuilder.Build(
                identity!, pluginVersion, due.Trigger, snapshot, config?.ManifestVersion);

            // The upload-log draft is summarized HERE, from the same snapshot the payload was
            // built from — so the log describes what actually went out, never a reconstruction.
            // Its status and failure diagnostics are filled in when the response settles. Inside
            // this try on purpose: Draft serializes and hashes the facts, and this method's
            // promise is that nothing in it can throw into the frame dispatch.
            logDraft = UploadLogEntry.Draft(
                timeProvider.GetUtcNow(), due.Trigger, snapshot, config?.ManifestVersion);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Could not assemble the {due.Trigger} upload; skipping it.");
            return;
        }

        uploadInFlight = true;

        // Task.Run moves the whole thing off the framework thread, including the JSON serialization
        // inside PostSyncAsync. `_ =` discards the task deliberately: it is awaited by nobody, and
        // UploadAsync is written so that nothing can escape it.
        //
        // No cancellation token is passed to Task.Run on purpose. Handing it one that is ALREADY
        // cancelled makes Task.Run skip the delegate entirely — so UploadAsync's `finally` would
        // never run, `uploadInFlight` would stay true, and syncing would be dead for the rest of the
        // session. Cancellation is instead observed inside UploadAsync, where the flag is cleared.
        //
        // The generation is captured here, once, so the task compares against the session it was
        // started for rather than whatever the field holds when the response lands.
        var startedFor = sessionGeneration;
        _ = Task.Run(() => UploadAsync(request, due.Trigger, startedFor, logDraft));
    }

    /// <summary>
    /// Folds this pass's skip reasons into what the settings window shows.
    /// </summary>
    /// <remarks>
    /// Merged rather than replaced. An <c>unlock</c> pass runs only the collectors for the categories
    /// that changed — one, or several when a burst of unlocks was debounced together — so it knows
    /// nothing about the rest. Overwriting the map with its results would wipe their reasons and make
    /// the window claim every absent category is fine. A category that succeeded this time has its
    /// stale reason removed; a category that did not run keeps whatever it last said.
    /// </remarks>
    private void RememberSkipReasons(CollectionSnapshot snapshot)
    {
        var merged = new Dictionary<string, string>(lastSkipped);

        foreach (var (category, reason) in snapshot.Skipped)
            merged[category] = reason;

        // Whatever was read successfully this pass is no longer skipped.
        foreach (var category in snapshot.Collections.Keys)
            merged.Remove(category);

        lastSkipped = merged;
    }

    /// <summary>
    /// Reports what this collection pass cost the frame it ran in.
    /// </summary>
    /// <remarks>
    /// Escalates to Warning past the budget threshold because at that point it is no longer a
    /// curiosity: a pass that overruns the frame is a visible stutter for the player, and the fix is
    /// to spread the work across frames rather than to collect less. The arithmetic and the ordering
    /// live in <see cref="CollectionCost"/>, where they are unit-tested; only the logging is here.
    /// </remarks>
    private void LogCollectionCost(CollectionSnapshot snapshot, SyncTrigger trigger)
    {
        var cost = CollectionCost.From(snapshot);
        if (cost.IsEmpty)
            return;

        // C# interpolation is eager: these strings are assembled whether or not the log level would
        // print them. Affordable only because this runs once per upload (a login or a 30-minute
        // sweep), never once per frame. Do not copy this pattern into a per-frame path.
        if (cost.OverBudget)
        {
            log.Warning(
                $"A {trigger} collection pass took {cost.Total.TotalMilliseconds:F1}ms on the " +
                $"framework thread, over the " +
                $"{CollectionCost.FrameBudgetWarningThreshold.TotalMilliseconds:F0}ms budget: " +
                $"{cost.Breakdown}. This may stutter the game.");
            return;
        }

        log.Debug($"{trigger} collection took {cost.Total.TotalMilliseconds:F1}ms: {cost.Breakdown}");
    }

    /// <summary>Uploads off the framework thread, retrying once if the failure was transient.</summary>
    /// <param name="startedFor">The session generation this upload belongs to.</param>
    /// <param name="logDraft">The upload-log summary of what this request carries, awaiting its outcome.</param>
    private async Task UploadAsync(
        SyncRequest request, SyncTrigger trigger, int startedFor, UploadLogEntry logDraft)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var response = await apiClient.PostSyncAsync(request, lifetimeToken).ConfigureAwait(false);

                // The character this answer is about may have logged out while it was in the air.
                // Applying it would write the previous character's status — or worse, a halt — onto
                // whoever is logged in now. The server already acted on the request (its writes are
                // monotonic, so that is harmless); only the local bookkeeping is stale. Checked per
                // iteration, so a logout during the retry delay is caught too. The one thing this
                // can drop is a late 429's Retry-After, which costs at most one extra request that
                // will receive its own.
                if (startedFor != sessionGeneration)
                {
                    log.Debug($"Discarding a {trigger} sync response from a previous session.");
                    return;
                }

                // The draft plus everything the settled response can add to "why": the outcome,
                // which attempt this was, the server's requested wait, the raw HTTP code, and
                // any validation complaints — the diagnostics a pasted bug report needs.
                var settledEntry = logDraft with
                {
                    Status = response.Status,
                    Attempt = attempt + 1,
                    RetryAfter = response.RetryAfter,
                    HttpStatusCode = response.HttpStatusCode,
                    Detail = UploadLogText.IssuesText(response.Error),
                };

                if (HandleResponse(response, trigger, startedFor))
                {
                    // Settled: recorded even for failures — a transparency log that hid failed
                    // uploads would be lying. The generation is re-checked at the moment of
                    // writing, the same discipline HandleResponse applies to the status fields:
                    // HandleResponse also returns true for a response it DISCARDED as stale, and
                    // a previous character's upload must not be written into the log either.
                    if (startedFor == sessionGeneration)
                        uploadLog.Record(settledEntry);

                    return;
                }

                if (!RetryPolicy.ShouldRetryNow(response.Status, attempt))
                {
                    if (startedFor == sessionGeneration)
                        uploadLog.Record(settledEntry);

                    return;
                }

                // Writes are idempotent server-side, so repeating the identical body is safe.
                log.Debug($"Retrying {trigger} sync after {response.Status}.");
                await Task.Delay(RetryPolicy.TransientRetryDelay, lifetimeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin unloaded mid-upload. Deliberately not logged: the log service may itself be
            // torn down by now.
        }
        catch (Exception ex)
        {
            // A last-resort net. An exception left unhandled in a discarded task escapes as an
            // unobserved-task exception on the finalizer thread, long after the context that caused
            // it is gone — logged at best, silently swallowed at worst. Catch it while we still know
            // what we were doing.
            log.Error(ex, "Unexpected failure during sync upload.");
        }
        finally
        {
            uploadInFlight = false;
        }
    }

    /// <summary>Applies the server's answer to the scheduler. Returns true when the attempt is settled.</summary>
    /// <param name="startedFor">The session generation the upload belongs to.</param>
    private bool HandleResponse(ApiResponse<SyncResponse> response, SyncTrigger trigger, int startedFor)
    {
        // Re-checked here, at the moment of writing, not only before the call: a logout can land in
        // the gap between the caller's check and these writes, and a stale write after OnLogout's
        // clears would put the previous character's status — or worse, a halt — onto the next one.
        // The re-check shrinks that window from a network round trip to a few instructions.
        if (startedFor != sessionGeneration)
        {
            log.Debug($"Discarding a {trigger} sync response from a previous session.");
            return true;
        }

        lastStatusCode = (int)response.Status;
        var now = timeProvider.GetUtcNow();

        if (response.Status == ApiStatus.Ok)
        {
            scheduler.MarkUploaded(trigger, now);
            lastSyncedAtBox = now;

            // Categories the server refused (a per-category kill switch). Surfaced so the user is not
            // left wondering why a collection never appears on the website.
            var skipped = response.Value?.SkippedCategories;
            if (skipped is {Count: > 0})
                log.Information($"Server skipped: {string.Join(", ", skipped)}");

            return true;
        }

        // The server told us to wait. Never argue: honor Retry-After when it sent one.
        if (RetryPolicy.BackoffUntil(response.Status, response.RetryAfter, now) is { } until)
        {
            log.Information($"Backing off until {until:u} after {response.Status}.");
            scheduler.BackOffUntil(until);
            return true;
        }

        // Nothing a retry can fix — a bad token, an unclaimed character, a misconfigured backend.
        if (RetryPolicy.RequiresUserAction(response.Status))
        {
            blockedPendingUserAction = true;
            log.Warning($"Sync halted: {response.Status}. The user must resolve this.");
            return true;
        }

        // Terminal but not the user's fault (a 400, 405, or 413 — all plugin bugs). Retrying the
        // identical body cannot help, so drop it and let the next trigger build a fresh one.
        if (ApiStatusMap.IsTerminal(response.Status))
        {
            log.Error($"Sync rejected: {response.Status}. Dropping this upload.");
            return true;
        }

        // Transient. The caller decides whether an attempt remains.
        log.Warning($"Sync failed: {response.Status}.");
        return false;
    }

    /// <summary>Fetches <c>/config</c> when it is due, adopting the server's cadence and manifest.</summary>
    private void PollConfigIfDue(DateTimeOffset now)
    {
        if (configPollInFlight)
            return;

        if (nextConfigPollAt is { } dueAt && now < dueAt)
            return;

        nextConfigPollAt = now + ConfigPollInterval;
        configPollInFlight = true;

        // Untokened for the same reason as the upload above: an already-cancelled token would make
        // Task.Run skip the delegate, stranding configPollInFlight at true.
        var startedFor = sessionGeneration;
        _ = Task.Run(() => PollConfigAsync(startedFor));
    }

    /// <param name="startedFor">The session generation this poll belongs to.</param>
    private async Task PollConfigAsync(int startedFor)
    {
        try
        {
            var response = await apiClient.GetConfigAsync(lifetimeToken).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                // Applied even when the session generation has moved on: the config describes the
                // server and the token, not the character, so it is just as valid for whoever logs
                // in next.
                remoteConfig = response.Value;
                scheduler.ApplyIntervals(response.Value!.Intervals);
                return;
            }

            // The halt, by contrast, is scoped like the upload's: a failure that arrives after the
            // character who caused it left must not halt the next one's session.
            if (startedFor == sessionGeneration && RetryPolicy.RequiresUserAction(response.Status))
            {
                blockedPendingUserAction = true;
                log.Warning($"Config poll halted: {response.Status}. The user must resolve this.");
            }

            // Any other failure keeps the previous config, which is exactly right: a config we cannot
            // refresh is better than no config, and the next poll is only an interval away.
        }
        catch (OperationCanceledException)
        {
            // Unloaded mid-poll.
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unexpected failure polling config.");
        }
        finally
        {
            configPollInFlight = false;

            // Any answer — success, failure, even cancellation at unload — releases the
            // first-sweep hold. A failed poll must unblock syncing rather than strand it: the
            // upload itself will surface whatever is actually wrong with the server.
            firstConfigAnswerSeen = true;
        }
    }
}
