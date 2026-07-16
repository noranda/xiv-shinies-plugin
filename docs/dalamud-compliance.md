# Dalamud compliance, rule by rule

How this plugin satisfies [Dalamud's plugin guidelines](https://dalamud.dev/plugin-publishing/restrictions/)
and [technical considerations](https://dalamud.dev/plugin-development/technical-considerations/)
— with the evidence for each. This is a living document: it must
be updated whenever a change touches one of these surfaces, and it exists both to keep us
honest and to make official-repository review straightforward.

Last full audit: 2026-07-15, via a four-reviewer pre-release pass including whole-repository
censuses (teardown symmetry; category-generic rendering; group-key literals; per-category
response fields) and a field-by-field contract conformance audit.

| Rule | How this plugin complies | Where |
|---|---|---|
| **Local player only** — never collect account IDs or data of other player characters, in any form, regardless of intended use (ban-enforced) | Only the local player is ever read: identity via `IPlayerState`, unlocks via `IUnlockState`, both of which expose no other-player data. No object-table or party-list access exists anywhere in the codebase. The item scan reads the player's own storage only: their retainers' inventories through `ItemFinderModule.RetainerInventories` **values** (the retainer-ID keys are never read and never leave the process; `RetainerManager` supplies a count only), and their currency balances through the game's `Currency` inventory container plus `CurrencyManager` for currencies held outside it. | `Sync/CharacterIdentity.cs`, `Collectors/ItemCollector.cs` |
| **Hash player identifiers client-side** | The ContentId is SHA-256-hashed on the machine with a fixed byte representation (deterministic across sessions). The raw ulong never travels, is never logged, and is never persisted. Verified in-game: logs and config contain no raw ContentId. | `Sync/ContentIdHash.cs` |
| **HTTPS only, trusted CA, DNS hostname (never a raw IP)** | The backend URL is normalized and validated before any request: raw IP addresses are refused in every spelling (dotted, and the numeric/hex encodings the OS still resolves); plaintext HTTP is refused for remote hosts (tolerated only for loopback development); auto-redirects are disabled so the server cannot hand the token to an unvalidated host. The production server uses a Let's Encrypt certificate. A response body larger than a few MB is refused before it is buffered, so a hostile backend cannot exhaust the game's memory. | `Api/BackendUrl.cs`, `Api/ApiClient.cs` |
| **Backend URL user-overridable** | The base URL is a persisted setting, overridable by editing the plugin's config file (there is deliberately no UI for it). Because the token is sent to whatever host is configured, a non-default backend additionally requires setting an acknowledgment flag in the same config file — until it is set, the client refuses to send anything (unit tests prove zero requests leave, not merely an error status). | `PluginSettings.BaseUrl`, `Api/ApiClient.cs`, `tests/…/ApiClientTests.cs` |
| **Minimize data sent** | Uploads carry ID numbers and item counts only — no names of things, no timestamps of acquisition, no inventory contents beyond the counts of items the server explicitly asked about. Currency balances (gil included) travel only for currency ids the server's manifest names AND the user's opted-in group covers, disclosed in the consent copy. Per-source scan states (`itemSources`) are status words and counts only — nothing identifies an individual retainer or container slot. Categories and groups the user did not opt into are never collected. | `Api/SyncRequest.cs`, `Collectors/CollectorGate.cs`, `Collectors/ManifestConsent.cs` |
| **Explicit opt-in before non-essential data collection; no silent first-run behavior** | Consent is enforced in code, not just reflected in UI: `UploadGate.CanContactServer` requires completed onboarding **and** the master switch **and** a usable token before any request — including the config poll. A fresh install talks to nobody. The first-run wizard discloses exactly what each category sends before the user can enable it. | `Sync/UploadGate.cs`, `Windows/MainWindow.Wizard.cs` |
| **No interaction with game servers without direct user action** | The plugin never interacts with the game's servers at all: it reads the local client's memory and speaks HTTPS to the XIV Shinies server only. | whole design; `Collectors/` |
| **No plugin-usage fingerprinting** | There is no analytics identifier of any kind. The auth token is a user-supplied credential, revocable on the website. The upload log is in-memory only and clears on unload. | `Sync/UploadLog.cs` |
| **Never block the framework thread** | Game state is read on the framework thread — every collector asserts this at runtime and refuses to run elsewhere. HTTP, JSON serialization, and retries run on background tasks (`Task.Run`); nothing calls `.Wait()`/`.Result` on a framework-thread task. Results cross back via volatile fields and atomic reference swaps. The item manifest, which drives per-frame container scans, is capped (`CollectContext.MaxManifestItems`) so a hostile `/config` cannot freeze the loop. | `Collectors/GameThread.cs`, `Sync/SyncManager.cs`, `Collectors/CollectContext.cs` |
| **Full teardown** | `Dispose()` mirrors the constructor exactly: every event subscription, command handler, window registration, and owned resource (fonts, HTTP client, cancellation sources) is released, in dependency order. Borrowed/framework-owned handles (the icon font, the shared mascot texture, injected services) are deliberately **not** disposed. Verified by a whole-repository census. | `Plugin.cs`, `Windows/MainWindow.cs` (the class spans `MainWindow.*.cs` partials; lifecycle lives here), `Sync/SyncManager.cs` |
| **Windowing API; no unprompted windows** | All UI goes through `WindowSystem`. The window opens only from `/shinies` (or its alias `/xivshinies`), the installer's open/settings buttons, or by the user's own navigation — never automatically on load or login. | `Plugin.cs`, `Windows/MainWindow.cs` |
| **Reproducible from public source** | No obfuscation, no downloading or loading of external code or native binaries at runtime, no self-updating, no timestamp/auto-increment versioning. Everything the plugin ships is in this repository. | whole repository |
| **Icon and imagery policy** | The plugin icon (512×512 PNG) and all shipped imagery are hand-made, not AI-generated, per Dalamud's AI policy. AI involvement in the *code* is disclosed centrally in [`AI-DECLARATION.md`](../AI-DECLARATION.md) (level: copilot), and will be declared in the official-repository submission PR. | `src/XIVShinies.SyncPlugin/images/icon.png`, `AI-DECLARATION.md` |

## Project conventions that go beyond the letter of the rules

- **Hashing is treated as a hard requirement.** Dalamud phrases client-side hashing as a
  recommendation ("whenever feasible, plugins should hash…"); this project treats it as
  non-negotiable.
- **Monotonic writes.** The server treats every upload as append-only: absence never clears a
  flag. The plugin reflects this — a category that could not be read is omitted from the
  payload, never sent as an empty list, so no partial upload can erase anything.
- **Consent is code, not UI.** The gates (`UploadGate`, `CollectorGate`) are pure, unit-tested
  classes on the request path. Unchecking a box does not merely hide a button; it makes the
  request impossible.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** is sent on every request — our own
  convention, not a Dalamud rule, so the server can tell plugin traffic apart.

## Keeping this document true

Any PR that adds a network call, reads a new game surface, registers a new event or window,
or touches identity data must update the relevant row here. A row that drifts from the code
is worse than no row at all.
