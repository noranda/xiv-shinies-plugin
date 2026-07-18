using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Serilog.Events;
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
    /// How long to wait before retrying a <c>/config</c> poll that did not answer.
    /// </summary>
    /// <remarks>
    /// A failed poll must not cost a full interval. Without a config the plugin does not know which
    /// items the server asks about, so a first-run user whose poll happened to meet a network blip would
    /// otherwise sit for half an hour with the item collection unread and no way to hurry it along. Long
    /// enough that a server having a bad minute is not hammered; short enough that the user is unlikely
    /// to notice the gap.
    /// </remarks>
    private static readonly TimeSpan ConfigPollRetryDelay = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Persists the settings object; invoked after this class mutates settings. Injected as a
    /// callback so this class needs no reference to the Dalamud config shell.
    /// </summary>
    // `Action` is the built-in type for a parameterless, void-returning function value — the
    // closest React analog is a `() => void` callback handed down as a prop: the caller decides
    // what "persist" concretely does, and this class just calls it.
    private readonly Action saveSettings;

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
    /// True while the wizard is owed a config answer: set when it asks for one, cleared the moment any
    /// poll answers.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="firstConfigAnswerSeen"/>, which latches true for the rest of the load. A
    /// wizard that waited on the latch would wait only once, and a second Verify press — perfectly
    /// ordinary, after pasting a different token — would sail straight past the wait it exists for.
    /// Volatile: raised on the draw thread when the wizard asks for a config, lowered by whichever thread
    /// answers (the poll task, or the framework-thread marshal when teardown beat it to the request), and
    /// read by the draw thread on every frame.
    /// </remarks>
    private volatile bool onboardingConfigPending;

    /// <summary>
    /// True once a collection pass has run for the character currently logged in.
    /// </summary>
    /// <remarks>
    /// The settings window's read-status panel reports, per enabled collection, whether the pass could
    /// read it — and "no skip reason" is what it reads as "read". Before any pass has run there are no
    /// skip reasons yet, so without this flag every enabled collection would claim to have been read
    /// when nothing had been. Cleared on logout with the rest of the character-scoped snapshots, since
    /// a pass for the character who just left says nothing about the next one.
    /// <para>
    /// The source notes cannot stand in for this flag: a user who has switched the item category off
    /// produces no source notes at all, yet their other collections have still been read and have a
    /// status worth showing.
    /// </para>
    /// <para><c>volatile</c>: written by the framework thread, read by the window's draw call.</para>
    /// </remarks>
    private volatile bool hasCollected;

    /// <summary>
    /// The manifest version the truncation warning was last logged for, or null when none has
    /// been. Keeps the warning at one line per oversized manifest instead of one per sweep —
    /// the condition only changes when the server serves a different manifest.
    /// </summary>
    /// <remarks>
    /// Framework thread only (written and read inside the collection pass), so it follows the
    /// same single-writer discipline as the identity fields: one writer, one thread, no locks.
    /// </remarks>
    private string? truncationWarnedForManifest;

    /// <summary>
    /// True once the quest-sequence truncation warning has been logged this session. A plain
    /// once-per-session latch (unlike the item warning's once-per-manifest-version gate) because
    /// the quest-sequence manifest carries no version hash to compare against.
    /// </summary>
    /// <remarks>
    /// Framework thread only, same single-writer discipline as
    /// <see cref="truncationWarnedForManifest"/>.
    /// </remarks>
    private bool questTruncationWarned;

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

    /// <summary>
    /// Per-source scan status from the most recent collection pass that reported any, keyed by
    /// source (see <see cref="SourceKeys"/>) — for example, "the saddlebag was read from a
    /// cache, not live". For the settings window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="lastSkipped"/>, this is REPLACED wholesale rather than merged, and only
    /// when the new pass actually produced something. A source note describes a physical storage
    /// location as seen by the pass that reported it, not a per-category fact — an unlock pass that
    /// reran only, say, the mounts collector knows nothing about the item sources and would report
    /// none, so merging its (empty) notes over the last real observation would erase a true "the
    /// armoire is unscanned" hint the user still needs to see. Replacing only on a non-empty result
    /// keeps the window showing the last pass that actually looked at the item sources, which is
    /// the truthful thing to show — not a snapshot of whichever pass happened to run most recently.
    /// </para>
    /// <para>
    /// <c>volatile</c>, and never mutated once handed out, for the same cross-thread reason as
    /// <see cref="lastSkipped"/>: the writer is the collection pass on the framework thread, the
    /// reader is the window's draw call.
    /// </para>
    /// </remarks>
    private volatile IReadOnlyDictionary<string, ItemSourceStatus> lastSourceNotes =
        new Dictionary<string, ItemSourceStatus>();

    /// <summary>Wires the manager to the game. Subscribes only; uploads nothing.</summary>
    public SyncManager(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IUnlockState unlockState,
        IPluginLog log,
        ApiClient apiClient,
        PluginSettings settings,
        Action saveSettings,
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
        this.saveSettings = saveSettings;
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
    /// True while the wizard is still owed the config answer it asked for when the token verified.
    /// </summary>
    /// <remarks>
    /// The wizard holds its consent step shut while this is true, so that the step opens with every
    /// checkbox it will ever show already on it. It is lowered by an ANSWER, not by a success: a poll
    /// that fails has settled the question of whether more is coming, and the answer is no. A wait only
    /// a successful poll could end would trap the user in the wizard whenever the server was
    /// unreachable; a failure simply means the consent step shows no group checkboxes, which the
    /// one-time consent migration is there to make good later.
    /// </remarks>
    public bool OnboardingConfigPending => onboardingConfigPending;

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

    /// <summary>
    /// Per-source scan status from the most recent collection pass that reported any (see
    /// <see cref="lastSourceNotes"/> for why this replaces rather than merges). Empty before the
    /// first such pass.
    /// </summary>
    /// <remarks>The returned dictionary is never mutated, so it is safe to read from any thread.</remarks>
    public IReadOnlyDictionary<string, ItemSourceStatus> LastSourceNotes => lastSourceNotes;

    /// <summary>
    /// True once a collection pass has run for the current character, so the settings window's
    /// read-status panel has something truthful to report. See <see cref="hasCollected"/>.
    /// </summary>
    /// <remarks>A volatile bool read, so safe from the draw call; at worst one frame stale.</remarks>
    public bool HasCollected => hasCollected;

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
        // Volatile, so this write is safe from any thread — and it should clear immediately, on
        // the press itself, rather than waiting behind the marshal below.
        blockedPendingUserAction = false;

        // Marshaled rather than run inline: this method is called from the window's draw call,
        // which Dalamud does not promise to invoke on the framework thread, and the identity-rearm
        // fields below belong to the framework thread's single-writer discipline (loginSettledAt
        // is a struct field the framework tick reads every frame). The same pattern, for the same
        // reason, as RequestOnboardingConfigPoll's marshal. The scheduler request rides inside the
        // marshal too, so the rearm always lands before the sweep it exists to unblock.
        _ = framework.RunOnFrameworkThread(() =>
        {
            // A marshal queued just before teardown still runs; starting work against a cancelled
            // lifetime would only be discarded a moment later.
            if (lifetimeToken.IsCancellationRequested)
                return;

            var now = timeProvider.GetUtcNow();

            // Identity capture can give up (a home world that never resolved), and only a fresh
            // login re-arms it. Without this, "Sync now" would queue a sweep that the
            // `identity is null` guard silently drops on every frame, and the button would appear
            // to do nothing at all.
            if (identity is null && clientState.IsLoggedIn)
            {
                loginSettledAt = now;
                identityAttempts = 0;
            }

            scheduler.Request(SyncTrigger.Manual, now);
        });
    }

    /// <summary>
    /// Fetches <c>/config</c> once, in direct response to the user verifying their token — the one
    /// request path that runs before onboarding is complete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the gate is bypassed.</b> Every automatic request funnels through
    /// <see cref="UploadGate.CanContactServer"/>, which requires <c>OnboardingComplete</c> — that is
    /// what keeps a fresh install silent. This method skips that check, and Dalamud's rules are the
    /// reason it may: what they forbid is <i>unprompted</i> contact and <i>non-consensual data
    /// collection</i>, and this is neither. It runs only as the immediate consequence of the user
    /// pressing Verify — the same click that already sends <c>GET /me</c> — it uploads nothing, and
    /// it reads only the server's own switches, cadence, and item manifest. Nothing about the player
    /// leaves the machine.
    /// </para>
    /// <para>
    /// <b>What it buys.</b> The wizard's consent list is built from the server's manifest groups, so
    /// without this the first-run user is asked to consent to a list the plugin has not fetched yet,
    /// and would meet the group checkboxes for the first time in the settings — wearing "New" badges,
    /// on an install minutes old. The <see cref="RemoteConfig"/> this fills in is the single source
    /// of truth for the config; the window reads it back rather than keeping a copy of its own.
    /// </para>
    /// <para>
    /// Safe to call as often as the user presses Verify: a poll already in flight wins, and a token
    /// that is not even well formed is refused here rather than earning an opaque 401.
    /// </para>
    /// </remarks>
    public void RequestOnboardingConfigPoll()
    {
        // The bypass is justified by the wizard and nowhere else, so it refuses to run outside it
        // rather than trusting its caller to check. Past onboarding, the ordinary gate governs every
        // poll — a request from here would be one the user's master switch never authorized.
        if (settings.OnboardingComplete)
            return;

        // A credential is still required. This is the one part of CanContactServer that survives the
        // bypass: with no usable token the request could only ever come back 401, so the round trip
        // would tell the user nothing a local shape check could not.
        if (!settings.HasUsableToken())
            return;

        // The wizard holds its consent step shut while this is true, so it is raised here — on the press
        // itself — rather than when the poll eventually starts. Raising it later would leave a window in
        // which the request is on its way and the wizard believes it has nothing to wait for.
        //
        // Every path out of here lowers it again. A poll that starts lowers it when it answers, and the
        // HTTP client's own timeout means even an unreachable server answers; a request that arrives
        // while a poll is already in flight is satisfied by that poll's answer; and a marshal that lands
        // after teardown lowers it on the spot. The wait can therefore always end.
        onboardingConfigPending = true;

        // Marshaled rather than started inline. This runs from the window's draw call, which Dalamud
        // does not promise to invoke on the framework thread, and StartConfigPoll writes
        // `nextConfigPollAt` — a struct field the framework tick reads every frame. Keeping every
        // write to it on the framework thread is the same single-writer discipline the identity
        // fields document above, and it is what makes those per-frame reads safe without locks.
        //
        // `_ =` discards the returned task deliberately, like the Task.Run calls elsewhere in this
        // class: nothing awaits it, and StartConfigPoll cannot throw.
        _ = framework.RunOnFrameworkThread(() =>
        {
            // The marshal can land after teardown has begun: Dispose unsubscribes the event handlers,
            // but a delegate already queued still runs. Starting a fresh request against a cancelled
            // lifetime would only be cancelled a moment later. The wait is lowered on the way out, so
            // it cannot be left standing by a poll that will now never run.
            if (lifetimeToken.IsCancellationRequested)
            {
                onboardingConfigPending = false;
                return;
            }

            StartConfigPoll(timeProvider.GetUtcNow());
        });
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

        // Source notes describe THIS character's inventory, retainers, and other storage — none of
        // which belongs to whoever logs in next. Carrying them across a character switch would show
        // scan status for storage the new character does not own.
        lastSourceNotes = new Dictionary<string, ItemSourceStatus>();

        // Nothing has been read for the NEXT character yet. Left set, the read-status panel would
        // report the departing character's collections as read — and, with their skip reasons just
        // cleared above, report every enabled collection as healthy before a single pass had run.
        hasCollected = false;

        // Queued work belongs to the character that queued it. Uploading it after a character switch
        // would attribute one character's unlocks to another.
        scheduler.Reset();

        // The upload log's entries describe the departing character's uploads, and its change-diff
        // baselines on the nearest older entry per category — left in place, the next character's
        // first sync would be compared against the PREVIOUS character's counts and light up gold
        // "(changed)" marks for differences no one earned. Character-scoped, like everything above.
        uploadLog.Clear();
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

            // A pass has now looked at the game for this character, so the settings window's
            // read-status panel may report on it. Set from the snapshot, not from the upload's
            // outcome: the panel describes what the plugin could READ, which is settled here and is
            // true whether or not the resulting payload ever reaches the server (it may be empty,
            // rejected, or halted).
            hasCollected = true;

            // The manifest cap clips silently at the point of use (a bounded scan must not depend
            // on anyone noticing), so this is where the clip becomes visible: without it, a server
            // bug serving an oversized manifest would read as mysteriously missing counts. Warned
            // once per manifest version — the sweep cadence would otherwise repeat it every pass.
            // The wording states the manifest fact rather than describing this pass's scan: the
            // flag is config-derived, so it can be true on a pass that ran no item scan at all
            // (an unlock pass for another category, or the items category switched off).
            if (snapshot.ManifestTruncated && truncationWarnedForManifest != config?.ManifestVersion)
            {
                truncationWarnedForManifest = config?.ManifestVersion;
                log.Warning(
                    $"The server's item manifest exceeds the {CollectContext.MaxManifestItems}-id " +
                    $"ceiling; ids past the first {CollectContext.MaxManifestItems} will not be " +
                    "scanned or reported.");
            }

            // The quest-sequence manifest clips under the same ceiling, made visible the same way.
            if (snapshot.QuestSequenceManifestTruncated && !questTruncationWarned)
            {
                questTruncationWarned = true;
                log.Warning(
                    $"The server's quest-sequence manifest exceeds the " +
                    $"{CollectContext.MaxManifestItems}-id ceiling; quests past the first " +
                    $"{CollectContext.MaxManifestItems} will not be looked up or reported.");
            }

            // Bound every category to the contract's caps before anything downstream sees it.
            // An over-cap payload is rejected whole by the server (400), losing every category
            // in it; truncating here keeps the rest of the upload alive. `snapshot with { ... }`
            // rebuilds the record with the bounded dictionary in place, so everything below —
            // the cost log, the skip reasons, the request build, and the upload-log draft — sees
            // the same bounded snapshot without any of them needing to know capping happened.
            var (boundedCollections, droppedByCap) = PayloadCaps.Bound(snapshot.Collections);
            if (droppedByCap.Count > 0)
            {
                snapshot = snapshot with { Collections = boundedCollections };

                foreach (var line in droppedByCap)
                    log.Warning($"Payload cap: {line}.");
            }

            LogCollectionCost(snapshot, due.Trigger);

            // The full facts of every category. This is the QA and support surface for collectors
            // whose values matter and not just their counts — harvesting a quest's sequence bytes
            // for the server's curated table, checking an item count against a container. Logged
            // from the bounded snapshot, so it shows what actually ships. Facts stay on the
            // user's machine: /xllog is local, and everything here is data the user consented to
            // uploading anyway. A generic loop over whatever categories the pass produced —
            // never a peek at one category by name.
            //
            // Verbose rather than Debug, deliberately: a quests line is tens of kilobytes, and at
            // Debug it would drown the short timing/scheduling lines someone raising the level is
            // usually after. Payload dumps sit one notch deeper in the /xllog dropdown.
            //
            // The level check guards eager work: C# interpolation is eager, so without it every
            // pass would serialize every category to JSON (~100KB of throwaway strings) on the
            // framework thread just to have Verbose discard the result at the default level.
            // Guarded, the cost is zero unless the user has deliberately raised the plugin's
            // level. (Serilog orders levels Verbose < Debug < Information, so "<= Verbose"
            // means Verbose is actually enabled.)
            if (log.MinimumLogLevel <= LogEventLevel.Verbose)
            {
                foreach (var (categoryKey, facts) in snapshot.Collections)
                    log.Verbose($"{due.Trigger} facts for {categoryKey}: {facts.ToJsonString()}");
            }

            // Kept for the settings window. An unlock pass runs only the collectors whose categories
            // changed, so it can speak for those and no others; replacing the whole map would erase
            // every absent category's reason. Merging keeps the last thing each category said about
            // itself.
            RememberSkipReasons(snapshot);
            RememberSourceNotes(snapshot);

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
    /// Replaces the settings window's source-notes snapshot with this pass's, but only when this
    /// pass actually reported any.
    /// </summary>
    /// <remarks>
    /// See the reasoning on <see cref="lastSourceNotes"/> for why this replaces instead of merging
    /// like <see cref="RememberSkipReasons"/> does, and why an empty result from this pass leaves
    /// the field untouched rather than clearing it: a pass that never touched the item sources (for
    /// example an unlock sweep for a different category) has nothing truthful to say about them, so
    /// the last pass that did look stays the answer.
    /// </remarks>
    private void RememberSourceNotes(CollectionSnapshot snapshot)
    {
        if (snapshot.SourceNotes.Count > 0)
            lastSourceNotes = snapshot.SourceNotes;
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
                    ProvenSteps = response.Value?.ProvenSteps,
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
        if (nextConfigPollAt is { } dueAt && now < dueAt)
            return;

        StartConfigPoll(now);
    }

    /// <summary>
    /// Starts a <c>/config</c> poll and schedules the next one. The single place a poll begins,
    /// whether the frame tick found one due or the user's Verify press asked for one (see
    /// <see cref="RequestOnboardingConfigPoll"/>).
    /// </summary>
    /// <remarks>
    /// <b>Framework thread only.</b> <c>nextConfigPollAt</c> is a struct field with no
    /// synchronization, written here and read by every frame's tick, so it follows the same
    /// single-writer discipline as the identity fields: one writer, one thread, no locks.
    /// </remarks>
    private void StartConfigPoll(DateTimeOffset now)
    {
        // The one authoritative in-flight check. Both callers reach it on the framework thread, so a
        // due poll and a Verify press landing in the same frame cannot both get through: the second
        // one sees the flag the first just set.
        if (configPollInFlight)
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
        // Decides, in the finally below, whether the next poll waits the full interval or the short
        // retry. Declared out here so every exit — a failed response, a throw — is covered by one rule.
        var answered = false;

        try
        {
            var response = await apiClient.GetConfigAsync(lifetimeToken).ConfigureAwait(false);
            answered = response.IsSuccess;

            if (response.IsSuccess)
            {
                // Applied even when the session generation has moved on: the config describes the
                // server and the token, not the character, so it is just as valid for whoever logs
                // in next.
                var config = response.Value!;

                remoteConfig = config;
                scheduler.ApplyIntervals(config.Intervals);

                // The one-time pre-group consent migration (flag-guarded inside PluginSettings, so
                // every poll after the first groups-bearing one is a no-op). Running it here means
                // the first config a user ever receives that carries groups migrates their
                // pre-group items consent before the next collection pass unions the enabled
                // groups — without it, an existing user's item scan would go silent until they
                // re-opted in by hand. The ordering is practical, not strict: the delegate is
                // queued rather than awaited, but the login sweep's settle delay gives it ample
                // room to land first, and losing that race would only delay the first item upload
                // by one cycle (server writes are monotonic), never lose anything.
                //
                // Held back until onboarding is complete, because the migration exists to speak for a
                // user who was never shown a group checkbox — and a user still inside the wizard is in
                // the middle of being shown exactly that, from this very config. Reading their half-made
                // category choice here could enable a legacy group they are about to leave off. The
                // wizard settles the flag itself when it finishes (PluginSettings.SettleItemGroupConsent).
                //
                // Marshaled to the framework thread rather than run inline, deliberately. This poll
                // completes on a background task, but PluginSettings' lists are not thread-safe and
                // the framework thread reads EnabledItemGroupKeys on every collection pass
                // (CollectorRunner copies it into the collect context). Keeping every settings
                // MUTATION on the framework thread — the same single-writer discipline the
                // identity fields document above — is what makes those reads safe without locks.
                //
                // `_ =` discards the returned task on purpose, like the Task.Run calls above:
                // nothing awaits it, and the try/catch inside keeps anything from escaping into
                // an unobserved-task exception.
                _ = framework.RunOnFrameworkThread(() =>
                {
                    // The marshal can land after teardown has begun — Dispose unsubscribes the
                    // event handlers, but a queued delegate still runs. A cancelled lifetime means
                    // the plugin is going away, and a settings write during teardown is not worth
                    // racing Dispose for; the migration simply runs on the next load's first poll.
                    if (lifetimeToken.IsCancellationRequested)
                        return;

                    try
                    {
                        // `Count: > 0` matters as much as the null test: an empty groups array
                        // must not burn the one-time flag, or a later config carrying the real
                        // groups could never migrate this user's consent.
                        if (settings.OnboardingComplete
                            && config.ItemManifestGroups is { Count: > 0 } groups
                            && settings.MigrateItemGroupConsent(
                                groups,
                                ManifestConsent.AnyManifestCategoryEnabled(collectors, settings)))
                        {
                            saveSettings();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Same last-resort net as UploadAsync: a throw in a discarded task would
                        // surface as an unobserved-task exception long after the context is gone.
                        log.Error(ex, "Item group consent migration failed.");
                    }
                });

                return;
            }

            // The halt, by contrast, is scoped like the upload's: a failure that arrives after the
            // character who caused it left must not halt the next one's session. Nor does a poll made
            // during onboarding halt anything — the user is still at the token box, being told what the
            // server thinks of their token by the wizard itself, and a halt raised here would outlive
            // the wizard and suppress the first sweep of a session that has yet to begin.
            if (settings.OnboardingComplete
                && startedFor == sessionGeneration
                && RetryPolicy.RequiresUserAction(response.Status))
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
            // Any answer — success, failure, even cancellation at unload — releases the
            // first-sweep hold. A failed poll must unblock syncing rather than strand it: the
            // upload itself will surface whatever is actually wrong with the server.
            firstConfigAnswerSeen = true;

            // Whatever the wizard was waiting for, this was it. ANY answer lowers the wait — a failure
            // and a cancellation as much as a success — because a wait only a successful poll could end
            // would leave the consent step shut behind a Continue button that never enables.
            onboardingConfigPending = false;

            configPollInFlight = false;

            // A poll that brought nothing back leaves the plugin without the manifest it needs, so the
            // next one is pulled in to the short retry rather than left a full interval away. Marshaled
            // because nextConfigPollAt belongs to the framework thread, which reads it every frame.
            if (!answered)
                RescheduleConfigPollSoon();
        }
    }

    /// <summary>Brings the next <c>/config</c> poll forward to the short retry delay.</summary>
    private void RescheduleConfigPollSoon()
    {
        // `_ =` discards the returned task deliberately, as elsewhere in this class: nothing awaits it,
        // and the delegate cannot throw.
        _ = framework.RunOnFrameworkThread(() =>
        {
            // The marshal can land after teardown has begun; there is no next poll to schedule then.
            if (lifetimeToken.IsCancellationRequested)
                return;

            nextConfigPollAt = timeProvider.GetUtcNow() + ConfigPollRetryDelay;
        });
    }
}
