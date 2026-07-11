# Contributing to XIV Shinies Sync

Thanks for considering a contribution! This document covers everything you need to build,
test, and change the plugin — and the ground rules that keep it approvable for the official
Dalamud repository.

## The one non-negotiable: Dalamud's guidelines

This plugin follows [Dalamud's plugin guidelines](https://dalamud.dev/plugin-publishing/restrictions/)
**strictly and precisely** — some of those rules are enforced with bans, and all of them gate
acceptance into the official repository. Every pull request is reviewed against them.

Before writing code, read [`docs/dalamud-compliance.md`](docs/dalamud-compliance.md): it maps
each rule to how this codebase satisfies it and where. The short version of the rules you are
most likely to brush against:

- **Local player only.** Never read the object table, party lists, or anything about another
  player — in any form, regardless of intent.
- **Player identifiers are hashed client-side.** The raw ContentId must never travel, be
  logged, or be persisted.
- **Explicit opt-in before any upload.** No new network path may fire before the user has
  consented; automatic requests must pass through `UploadGate`.
- **Never block the framework thread.** Game state is read on the framework thread; HTTP and
  heavy work happen off it. `.Wait()`/`.Result` on a framework-thread task deadlocks the game.
- **Full teardown.** Everything the constructor wires — events, commands, windows, owned
  resources — is reversed in `Dispose()`.

If a change you want to make seems to require bending one of these, open an issue first.

## Prerequisites

- The **.NET 10 SDK**
- **[XIVLauncher](https://goatcorp.github.io/)**, installed and having launched the game at
  least once — the build references the Dalamud dev libraries under
  `%AppData%\XIVLauncher\addon\Hooks\dev`, resolved automatically by `Dalamud.NET.Sdk`

## Building and testing

```powershell
dotnet build      # Release produces the loadable DLL + merged manifest under bin/Release/
dotnet test       # the xUnit suite
```

The build must stay **warning-clean**, and all tests must pass.

### Loading the dev plugin in-game

1. `dotnet build -c Release`
2. In-game: `/xlsettings` → **Experimental** → **Dev Plugin Locations** → point it at
   `src\XIVShinies.SyncPlugin\bin\Release\XIVShinies.SyncPlugin.dll` (Dalamud reads the
   manifest `.json` sitting next to it), then **click Save** — the location does not register
   until you do.
3. Enable the plugin in `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**, then run
   `/shinies`. Use `/xllog` to watch the plugin's log output.

## Testing philosophy — be honest about the split

- **Pure logic gets xUnit tests**, written test-first where practical: payload/DTO
  round-trips, scheduling and debounce (the clock is abstracted), gate precedence, diffing,
  text formatting. This logic lives in Dalamud-free classes precisely so the test project can
  link it without a running game.
- **Game-API surfaces get in-game QA, not unit tests**: anything touching `IUnlockState`,
  `IPlayerState`, `IDataManager`, `InventoryManager`, `ItemFinderModule`, or live HTTP.
  Dalamud services cannot be instantiated outside the game process — do not fake a unit test
  around one. If a game-touching method holds a decision that *could* be a pure class, extract
  it and test that instead ("the window holds no decisions" is the working rule).

## Architecture

The plugin is deliberately a **dumb fact-reader**. It sends only raw facts the game knows
(completed quest IDs, unlocked achievement/mount/minion IDs, owned item counts) and never
computes site concepts like relic steps — the server does all derivation. This keeps the
plugin stable as the site grows new collections and rules.

The pipeline, in one pass:

1. **Collectors** (`Collectors/`) each read one category from the game client. They are
   registered in exactly one place, `CollectorRegistry`, and describe themselves: wire key,
   display name, and a plain-language disclosure of what they send.
2. **`CollectorRunner`** runs the registered collectors on the framework thread and produces a
   `CollectionSnapshot` — facts per category, plus a reason for every category that could not
   be read.
3. **`SyncPayloadBuilder`** turns a snapshot into the wire payload. A category that could not
   be read is simply **absent** — the server treats absence as "no information", never as
   "cleared", so partial uploads are always safe (writes are monotonic; collections only grow).
4. **`SyncManager`** orchestrates: it listens for login/unlock events and a periodic interval,
   collects on the framework thread, then uploads on a background task. Every path out of an
   event checks **`UploadGate`** (consent + credential) first, and per-category consent is
   checked by `CollectorGate`. Server kill switches and the item manifest arrive via a
   periodic `/config` poll.
5. **The window** (`Windows/`) renders state and holds no decisions — anything with logic in
   it is extracted to a pure, tested class.

**Threading model:** game state is read only on the framework thread (collectors verify this
at runtime); HTTP, JSON serialization, and retries run on background tasks; results cross back
via volatile fields and atomic reference swaps, never locks on the draw path.

### Adding a collection — the extensibility contract

Adding a collection must be **one new `ICollector` class and nothing else.** The orchestrator,
payload, settings UI, and upload log all iterate registered collectors generically and contain
**zero category-name branches** — if you find yourself writing `if (key == "quests")`, the
design has gone wrong. Hints and special behavior hang off collector-declared data (skip
reasons, display names), never off category names. A test runs a fake registered collector
through payload assembly and settings enumeration to guard exactly this.

## Code style

- **Comment heavily, for a beginner and for future contributors.** This codebase doubles as
  C# learning material for its maintainer and onboarding for cold readers: comments explain
  what a construct *is*, why it is here, and what C#/Dalamud-specific syntax means. Match the
  density you see around you. Keep comments professional and durable — no session notes, no
  references to removed designs.
- `ImplicitUsings` is **off**: explicit `using`s, outside the namespace, `System.*` first.
  File-scoped namespaces. ImGui is `Dalamud.Bindings.ImGui` (not `ImGuiNET`).
- Use `ImRaii` scopes for every ImGui push/pop pair — no bare `Begin*/End*`.
- American English everywhere, including user-facing strings (.NET API names like
  `Cancellation` are exempt).
- The `.editorconfig` is [goatcorp's SamplePlugin](https://github.com/goatcorp/SamplePlugin)
  baseline — formatting matches what Dalamud reviewers expect.

## Commits and pull requests

- Conventional commit messages (`feat:`, `fix:`, `docs:`, …) with a short imperative subject
  and a bulleted body describing user-visible changes.
- **No AI or authorship trailers** (`Co-Authored-By`, "Generated with", etc.): AI involvement
  is disclosed centrally and honestly in [AI-DECLARATION.md](AI-DECLARATION.md), and
  per-commit trailers would drift out of sync with it.
- Keep PRs focused; describe what changed and how you verified it. Changes to game-touching
  code should say what in-game QA you performed — the unit suite cannot prove those paths.
