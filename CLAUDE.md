# CLAUDE.md — XIV Shinies Sync (Dalamud plugin)

Guidance for working in **this** repo. This is the C# / .NET 10 Dalamud plugin that syncs
in-game collection progress to XIV Shinies. It is a **separate repo** from the main
`xiv-shinies` web app.

## Working style — baked-in rules (read first)

The maintainer is an experienced **frontend React developer with no prior C# experience**.
Two rules are non-negotiable for all work in this repo:

1. **Learning summaries (opt-in per session).** This project can end every substantial
   change with a two-part "Learning summary" — plain-English first, then a bit more technical
   with React/TypeScript comparisons — so the codebase doubles as C# learning material.
   It's **opt-in per developer, per session**: the first time in a session you're about to
   write one, ask the developer once whether they want them this session and honor that for
   the rest of the session; skip the question when a standing preference is set (the
   maintainer defaults to **on**). The **`/learning-summary` skill**
   (`.claude/skills/learning-summary/`) is the authority on the format and depth once
   enabled, and it's the file the maintainer refines over time. Skip summaries for trivial
   edits regardless.

2. **Comment heavily, for a beginner _and_ for future contributors.** This is an
   open-source project that may take contributions from other developers, so comments serve
   two audiences at once: the maintainer learning C#, and any contributor reading the code
   cold. Comment everywhere it helps understanding, not just the tricky bits: what a
   construct *is*, why it's here, and what the C#/Dalamud-specific syntax means. Favor short
   plain-language comments; when a C# concept has no React analog, explain it. Keep comments
   professional and accurate (they ship in a public repo) — avoid "note to self" phrasing;
   write them as durable documentation. Prefer clarity over brevity — this codebase doubles
   as the maintainer's C# learning material and as onboarding for contributors.

## What this is

A Dalamud plugin (API level 15, `Dalamud.NET.Sdk/15.0.0`, .NET 10) scaffolded from
goatcorp's SamplePlugin. It reads completion facts from the local game client and uploads
them to the deployed XIV Shinies API. The plugin is a **dumb fact-reader**: it sends only
raw facts the game knows (completed quest IDs, unlocked achievement/mount/minion IDs, owned
item counts) and never computes app concepts like relic steps or derivations — the server
does all derivation. This keeps the plugin stable as the server grows new collections and
rules.

## The API contract is the source of truth

The client-facing wire format — auth, endpoints, payload schema, status codes, character
binding — is documented in [`docs/api-contract.md`](docs/api-contract.md). Work from it when
building or changing any request/response type; never implement payloads/DTOs from memory or
guess field names.

The **deployed XIV Shinies server** defines and enforces the contract and is the ultimate
authority: if the live API and that doc ever disagree, the server wins and the doc must be
corrected. Treat any plugin/server mismatch as a bug in the plugin.

## Layout

```
XIVShinies.SyncPlugin.slnx            solution (.slnx — .NET 10 default format)
src/XIVShinies.SyncPlugin/            the plugin
  XIVShinies.SyncPlugin.csproj        <Project Sdk="Dalamud.NET.Sdk/15.0.0">
  XIVShinies.SyncPlugin.json          plugin manifest (Name, Punchline, Tags, RepoUrl)
  Plugin.cs                           IDalamudPlugin entry point, /shinies command
  Configuration.cs                    IPluginConfiguration (persisted settings)
  PluginMeta.cs                       pure constants/helpers (unit-tested)
  Windows/                            ImGui windows (WindowSystem)
tests/XIVShinies.SyncPlugin.Tests/    xUnit — pure logic only
```

The manifest's descriptive fields live in `XIVShinies.SyncPlugin.json`; the SDK merges in
`AssemblyVersion` (from `<Version>` in the csproj), `InternalName` (assembly name), and
`DalamudApiLevel` (from the SDK version) at build time. Bump `<Version>` for releases (the
release workflow will automate this).

## Build & test

```powershell
dotnet build      # Release produces the loadable DLL + merged manifest under bin/Release/
dotnet test       # xUnit suite
```

- Requires the **.NET 10 SDK** and a **XIVLauncher** install that has launched the game
  once — the build references the Dalamud dev libraries under
  `%AppData%\XIVLauncher\addon\Hooks\dev` (resolved automatically by the SDK).
- `bin/`, `obj/`, and `dist/` are git-ignored. A Release build also emits a
  DalamudPackager `latest.zip` — build artifacts, never committed.

### Loading in-game (manual QA)

`/xlsettings` → Experimental → Dev Plugin Locations → point it at the **built DLL**,
`src\XIVShinies.SyncPlugin\bin\Release\XIVShinies.SyncPlugin.dll` (Dalamud reads the manifest
`.json` sitting next to it), then **click Save** — until it is saved the location never
registers and `/xlplugins` shows no Dev Tools section at all. Then enable the plugin in
`/xlplugins` → Dev Tools → Installed Dev Plugins and run `/shinies`. Use `/xllog` to see the
plugin's log output.

## Testing philosophy — pure logic vs. game surfaces

Be honest about this split; do not fake it.

- **xUnit covers pure logic only**: payload/DTO (de)serialization round-trips, the auth
  state machine, diffing, debounce/interval scheduling (abstract the clock), kill-switch
  precedence, armoire bit→item mapping. Keep this logic in Dalamud-free classes (like
  `PluginMeta`) so the test project links it without pulling in game services. The test
  project references the plugin project directly and shares its `net10.0-windows` TFM.
- **Game-API surfaces get in-game QA, not unit tests**: anything that touches
  `IUnlockState`, `IPlayerState`, `IDataManager`, `InventoryManager`, `ItemFinderModule`,
  or live HTTP. These are exercised via in-game QA (the user drives the game, Claude
  verifies the resulting data), because Dalamud services can't be instantiated outside the
  game process.
- **TDD where the logic is pure**: failing test → run (expect FAIL) → minimal code → run
  (expect PASS).

## Dalamud / FFXIVClientStructs conventions

- **Injected services** use `[PluginService]` static properties on `Plugin` (see
  `Plugin.cs`); construct-time wiring, disposed in reverse in `Dispose()`. Unregister
  every handler/event you add (command handlers, `UiBuilder` callbacks, `WindowSystem`).
- **ImGui** is `Dalamud.Bindings.ImGui` (not `ImGuiNET`) at API 15.
- **`ImplicitUsings` is off** — add explicit `using System;` etc. Match the existing files:
  file-scoped namespaces, `using`s outside the namespace, `System.*` directives first. The
  `.editorconfig` and `.gitignore` are goatcorp's SamplePlugin baselines (the Dalamud-standard
  configs), so formatting and ignores match what reviewers expect.
- **Never block the framework thread.** Read game state (`IUnlockState`, `IPlayerState`,
  inventory) on the framework thread; do HTTP and other heavy work OFF it (async). Never
  `.Wait()`/`.Result` on a framework-thread task — it deadlocks the game. Marshal with
  `IFramework.RunOnFrameworkThread`/`RunOnTick` when you must touch game state from elsewhere.
- **No unprompted windows** — open UI only from `/shinies`, the installer's open/config
  buttons, or the first-run wizard, never automatically on load. Use the Windowing API
  (`WindowSystem`) for every window.

## Dalamud compliance (gates official-repo approval)

The plugin targets eventual submission to the official Dalamud repo, whose review enforces
the following. Treat them as hard requirements — a miss can mean rejection or a ban, not a
nit (sources: dalamud.dev `plugin-publishing/restrictions`, `plugin-development/technical-considerations`):

- **Local player only.** The rule: your plugin must not "collect account IDs of player
  characters beyond your own **in any form, regardless of the intended use**". It is
  ban-enforced. In practice: never read the object table or party list; only the local
  player (`IPlayerState` / `ClientState.LocalContentId`).
- **Hash player identifiers client-side.** Dalamud's wording is a recommendation —
  "whenever feasible, plugins **should** hash information about the local player (such as
  the player's Content ID or name) on the client side" — but this project treats it as a
  hard requirement: SHA-256 the ContentId before it leaves the process, the raw ulong never
  travels and never lands in logs, config, or request bodies, and the digest stays
  deterministic across sessions (fixed byte representation).
- **Network (documented rules):** HTTPS only, with the server's certificate "issued from a
  trusted certificate authority such as Let's Encrypt"; connect by **DNS hostname, never a raw
  IP** (no loopback exemption is stated — use `localhost`, not `127.0.0.1`); minimize the data
  sent; and keep the backend URL **user-overridable**. Base URL `https://xiv-shinies.com`.
  *Our own convention, not a Dalamud rule:* send `User-Agent: XIVShinies.SyncPlugin/<version>`.
- **Explicit opt-in before any upload.** Dalamud's documented rules are two distinct things:
  never interact with the **game servers** without direct user action, and users must
  **explicitly opt in** to non-essential data collection. Both apply here: no silent first-run
  upload, and uploads are user-chosen (onboarding consent) and login/event/interval-driven,
  never a tight poll. Disclose exactly what is sent.
- **No plugin-usage fingerprinting.** Any analytics identifier must be pseudo-random or
  absent and user-resettable; send nothing that lets a third party detect plugin usage beyond
  what the user authorizes. (The auth token is a user-supplied credential, not analytics.)
- **Full teardown.** `Dispose()` must reverse everything the constructor wired — command
  handlers, `UiBuilder` callbacks, `WindowSystem`, events.
- **Reproducible from public source.** No obfuscation, no downloading or loading external
  code/native binaries at runtime, no self-updating, and no timestamp/auto-increment version
  numbers.

## Monotonic-write awareness

The server treats every upload as monotonic: collections only grow, absence never clears a
flag, partial uploads are always safe. The plugin must reflect this — send what was
readable, omit what wasn't (e.g. omit `achievements` when the list isn't loaded rather than
sending an empty array), and never treat an absent category as "cleared".

## Extensibility contract

Adding a collection must be **one new `ICollector` class** and nothing else. The
orchestrator and settings UI iterate the registered collectors and must contain **zero
category-name branches** (e.g. the achievements "open your list" hint surfaces via a
collector's skip reason, not a special case). Guard this with a test that runs a fake
registered collector through payload assembly and settings enumeration.

## Commits, PRs, releases

Project-scoped skills live in `.claude/skills/`:

- **committing-code** — conventional commits, gated on `dotnet build` + `dotnet test`.
- **opening-pull-requests** — draft-by-default PR flow with a dotnet test plan.
- **reviewing-code-changes** — parallel-subagent quality gate: C# correctness + contract
  conformance, Dalamud compliance, comment quality, and test coverage.
- **learning-summary** — the two-part learning-summary format (see the working-style rules
  at the top of this file).
- **releasing** — two-phase release flow (changelog entry → version/repo.json release PR,
  each gated on user approval), then a `vX.Y.Z` tag pushed to `main` after the squash merge;
  the tag-triggered Release workflow verifies every version surface agrees and publishes the
  GitHub Release that `repo.json` points at. Releases are never built or published from a
  developer machine. Official-repo submission (later) still needs a `manifest.toml` placed
  under `testing/live/` in DalamudPluginsD17 (new plugins start on the testing track), one
  plugin per PR, and the AI-use level (**copilot**) disclosed in that PR's description; the
  required `images/icon.png` (1:1, 64–512px, hand-made — **not** AI-generated, per Dalamud's
  AI policy) already ships in the repo.

**Never commit without the user's express approval.** Never add `Co-Authored-By`,
`Signed-off-by`, "Generated with", or any AI/authorship signature to commits or PRs — AI
involvement is disclosed centrally in [`AI-DECLARATION.md`](AI-DECLARATION.md) (following
Dalamud's AI policy and the AI-DECLARATION.md standard), so per-commit/PR trailers are
redundant and would drift out of sync with that single source of truth.

## Environment (Windows)

- Shell is **Windows PowerShell 5.1**. `git` and `gh` are installed but may not be on the
  spawned shell's PATH — prefix `$env:PATH = "C:\Program Files\Git\cmd;" + $env:PATH;` when
  a shell can't find `git`, and invoke `gh` by full path
  (`& "C:\Program Files\GitHub CLI\gh.exe"`).
- `dotnet build` / `dotnet test` / `dotnet restore` are allowlisted in
  `.claude/settings.local.json` (git-ignored, per-developer — so the allowlist is never
  committed to the public repo) to cut permission prompts.
