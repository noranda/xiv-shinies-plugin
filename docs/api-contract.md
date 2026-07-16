# XIV Shinies plugin-sync API contract (client-facing)

This is the contract the **XIV Shinies Sync** plugin implements: how it authenticates and
what the `/api/plugin/v1/*` endpoints accept and return. It is the reference a contributor
uses when building or changing any request/response type.

> **Authority.** The **deployed XIV Shinies server** defines and enforces this contract; it
> is the ultimate source of truth. If the live API and this document ever disagree, the
> server wins and this document must be corrected. Anything the plugin sends or receives
> must match the server exactly — never implement payloads from memory or guess field names.
>
> This document is deliberately limited to the client-facing wire format. Server
> implementation (data model, derivation logic, internal design rationale) is intentionally
> not covered here.

## Overview

The plugin reads completion facts directly from the game client — completed quest IDs,
achievement/mount/minion unlock IDs, possession counts for server-requested items — and
uploads them over HTTPS. **The server does all derivation** (quest completion, relic steps);
the plugin never computes app concepts, so new relic series, quest links, and proof rules
ship without a plugin update.

Two principles govern every upload:

- **First-party evidence.** A plugin upload comes from inside the game client, so it both
  verifies character ownership and outranks a Lodestone scrape; it cannot be erased by a
  manual unmark on the website.
- **Monotonic writes.** Collections only grow. An ID absent from a snapshot means "not read
  this time" (list not loaded, category disabled) — never "lost" — so a partial upload is
  always safe. Acquisition flags are set, never auto-unset, and rows are never deleted by a
  sync.

## Transport basics

- **Base URL:** `https://xiv-shinies.com` — user-overridable for local development.
- **`User-Agent: XIVShinies.SyncPlugin/<version>`** on every request.
- All endpoints live under `/api/plugin/v1/`. Every response body is JSON. Every
  authenticated success carries `Cache-Control: no-store` (per-token private data, and a
  cached kill switch would be a stale kill switch).
- Using the wrong method (POST to `/me` or `/config`, GET to `/sync`) returns **405**.

## Authentication

Users generate a token on their XIV Shinies **profile settings page** ("Game plugin" section): the
raw token is shown **once** and never recoverable (only its hash is stored server-side).
Tokens are revocable, and revoking permanently deletes the token. Each account may hold at
most **10 tokens**.

- **Format:** `xvs_` followed by 43 base64url characters (32 random bytes). The `xvs_`
  prefix lets a leaked value be recognized; strings without it are rejected before any
  lookup.
- **Transport:** `Authorization: Bearer <token>` on every request. The scheme name is
  case-insensitive; exactly one space separates scheme and token. The plugin endpoints are
  **bearer-only** — there is no cookie-session fallback.
- **401 semantics:** every auth failure — missing header, malformed header, unknown or
  revoked token — returns the same opaque body:

  ```text
  401  {"error": "invalid_token"}    WWW-Authenticate: Bearer
  ```

  A 401 never heals on retry. On a 401 the plugin should stop syncing and tell the user to
  generate a new token.

## Endpoints

### GET /api/plugin/v1/me

The status/link probe: given only its token, the plugin learns which user it belongs to and
every character that user has **claimed** (favorites are invisible — see
[Character binding](#character-binding)).

```jsonc
200
{
  "characters": [
    {
      "id": "12345678",        // Lodestone id — a BigInt, so it travels as a string
      "name": "Some Name",
      "pluginLinked": true,    // a ContentId hash is already bound to this character
      "verified": true,        // the claim is verified (bio code or plugin upload)
      "world": "Excalibur"
    }
  ],
  "user": {"id": "<user uuid>"}
}
```

Characters are ordered alphabetically by name. Statuses: **200**, **401**, **405** (non-GET).

### GET /api/plugin/v1/config

Remote config + item manifest. The plugin polls this roughly every 30 minutes. Values are
read per request, so a flipped kill switch reaches the plugin on its next poll.

```jsonc
200
{
  "categories": {              // per-category kill switches (true = enabled)
    "achievements": true,
    "items": true,
    "minions": true,
    "mounts": true,
    "quests": true
  },
  "enabled": true,             // global kill switch
  "intervals": {
    "fullSyncMinutes": 30,     // full-sweep upload cadence
    "unlockDebounceSeconds": 5 // debounce after an Unlock event before uploading
  },
  "itemManifest": [7851, 7852], // the flat manifest: proof item IDs, kept for clients without group support
  "itemManifestGroups": [       // named consent groups; when present, these define what may be scanned
    {"key": "relic-proofs", "label": "Relic weapons, tools & armor", "ids": [7851, 7852], "legacy": true},
    {"key": "relic-materials", "label": "Relic materials", "ids": [5106]},
    {"key": "relic-currencies", "label": "Currencies (including gil)", "ids": [1, 28]}
  ],
  "manifestVersion": "a1b2c3d4e5f6"
}
```

- **Kill switches.** `enabled` is the global switch; `categories` is per-category. **The
  client must honor both**: stop uploading entirely when `enabled` is false, and skip
  collecting/sending disabled categories. The server enforces them too, but a compliant
  client saves the round trips.
- **Item manifest.** The item IDs the server wants possession counts for. The plugin checks
  possession of **only** these items. When `itemManifestGroups` is present it takes
  precedence; the flat list stays in the config permanently for clients without group
  support, and serves proof ids only.
- **Item manifest groups.** Named consent groups splitting the manifest: `key` is a stable
  consent identifier (a rename is a NEW group and re-prompts consent); `label` is
  user-facing; `legacy: true` marks a group whose scope pre-group items consent already
  covered — the plugin's one-time migration auto-enables exactly those. Everything else
  defaults OFF until the user opts in per group. The plugin scans the union of the enabled
  groups, deduplicated in first-seen order (an id may legitimately appear in more than one
  group). A config with no groups field — or an empty array — falls back to the flat
  `itemManifest`.
- **`manifestVersion`.** A content hash (the first 12 hex characters of the SHA-256 of the
  serialized manifest array). It changes iff the manifest changes, so the plugin can skip
  re-scanning inventory when the version it last scanned against is unchanged. Echo it back
  in the sync payload's optional `manifestVersion` field. Compare for equality only — it is
  a hash, not a counter.

Statuses: **200**, **401**, **405** (non-GET).

### POST /api/plugin/v1/sync

A full or incremental collection snapshot for one character, applied monotonically.

#### Request

`Content-Type: application/json`, and a **`Content-Length` header is required** — a chunked
request without it is rejected with **413**. Maximum body size is **1 MiB** by default.

```jsonc
{
  "characterContentIdHash": "…64 lowercase hex chars…",
  "characterName": "Some Name", // first-upload binding + friendly 403s only
  "homeWorld": "Excalibur",
  "pluginVersion": "1.0.0",
  "manifestVersion": "a1b2c3d4e5f6", // optional — the /config value the items list was built from
  "trigger": "login", // "interval" | "login" | "manual" | "unlock"
  "collections": {
    // EVERY key optional — send what was readable
    "achievements": [1, 2],
    "minions": [2, 8],
    "mounts": [1, 5],
    "quests": [65575, 66216], // Quest Excel row ids == the server's Quest.id
    "items": [{"id": 7851, "count": 1, "hqCount": 2, "fresh": true}]
  },
  "itemSources": { // optional — how each storage source was read this pass
    "inventory": {"state": "live"},
    "saddlebag": {"state": "cached"},
    "retainers": {"state": "cached", "count": 3, "total": 5},
    "armoire": {"state": "loaded"},
    "glamourDresser": {"state": "unscanned"}
  }
}
```

Field constraints:

| Field                    | Constraints                                                                              |
| ------------------------ | ---------------------------------------------------------------------------------------- |
| `characterContentIdHash` | matches `^[0-9a-f]{64}$` (lowercase hex SHA-256)                                          |
| `characterName`          | trimmed, 1–100 chars                                                                      |
| `homeWorld`              | 1–100 chars                                                                               |
| `pluginVersion`          | 1–50 chars                                                                                |
| `manifestVersion`        | optional, ≤ 100 chars                                                                     |
| `trigger`                | `interval` \| `login` \| `manual` \| `unlock`                                            |
| id-list categories       | arrays of positive integers, **max 50,000 ids per category**                             |
| `items`                  | `{id: positive int, count: non-negative int, hqCount?: positive int, collectableCount?: positive int, fresh: boolean}[]`, **max 10,000 entries** |
| `itemSources`            | optional object keyed by source name; each value `{state: "live"\|"cached"\|"unscanned"\|"loaded", count?: int, total?: int}` |

- **Unknown `collections` keys are stripped and logged, never rejected** — a plugin newer
  than the server keeps working (payload evolution is additive-only). An older plugin simply
  omits keys, which is safe under monotonic writes.
- **Ids the server's catalog does not recognize are ignored, never an error.** The
  plugin is a dumb fact-reader and should send every id the game reports; the server's
  catalog tables are deliberately pruned subsets (quests especially) and can trail the
  game after a patch. Each category's ids are filtered against its catalog before
  writing: unknown ids are dropped (and logged server-side), the known ids in the same
  payload still write, and the upload succeeds. Nothing is lost — the plugin re-sends
  everything on every sweep, so a dropped id lands as soon as the catalog imports it.
  Dropped ids are simply absent from the `written` counts.
- An **empty array carries no facts** and writes nothing (absence and emptiness are both "no
  information").
- **Explicit zeros.** An `items` entry PRESENT — even with `count: 0` — is a reported fact
  for that id; an id ABSENT from the list was not scanned and carries no information. What
  a count *means* is decided per id by which manifest group the id belongs to — see the
  proof vs. count-tracked split under [Behavior](#behavior-the-plugin-author-should-know).
  Uploads are filtered to the served manifest at apply time, so stale-manifest or
  out-of-catalog ids are dropped before writing.
- **Per-quality counts.** `count` is normal-quality copies only; optional `hqCount` and
  `collectableCount` are omitted when zero. The plugin never sums qualities; whether HQ
  satisfies a requirement is the server's policy.
- **`itemSources`** tells the server which storage sources contributed to the counts (a
  zero while retainers are unscanned is a floor, not truth) and powers "open your saddlebag
  once" hints. The retainer entry's `count` is how many retainers the cache remembers; the
  optional `total` is how many the character has, when the game can say — `3` of `5`
  scanned means two retainers contribute nothing yet. Both are counts only; nothing
  identifies an individual retainer.
- `fresh: false` means the count came from a cache rather than a live container read. The
  server treats a stale positive as a positive (the item *was* there), so the flag does not
  change the outcome.

#### Response (200)

```jsonc
{
  "ok": true,
  "bound": false, // true only when THIS request performed the first-upload bind
  "written": {
    // rows created + promoted per id-list category — always all four keys
    "achievements": 0,
    "minions": 2,
    "mounts": 1,
    "quests": 12
  },
  "achievementsSkipped": "not_sent", // present iff the achievements key was absent or stripped as disabled (an explicit empty array is "sent")
  "provenSteps": 3, // present iff items were applied and relic-proof derivation succeeded
  "itemCounts": 1268, // rows written to item-count storage by this upload's items
  "skippedCategories": ["minions"] // present iff the server stripped disabled categories from this payload
}
```

Optional keys are **omitted rather than null**, so the plugin can feature-detect them.
`items` never appears in `written` (it feeds relic proofs and count storage, not a
collection count). `itemCounts` is informational, like `written`: the plugin ignores both —
no plugin logic may branch on them.

#### Status codes

| Status  | Body                                                            | Plugin behavior                                                                                                       |
| ------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| **200** | see above                                                       | Applied.                                                                                                             |
| **400** | `{"error": "invalid_payload", "issues": {…}}`                   | Validation failed; `issues` is `{fieldErrors, formErrors}`. A non-JSON body gets the same shape with a `formErrors` message. Don't retry unchanged. |
| **401** | `{"error": "invalid_token"}` + `WWW-Authenticate: Bearer`       | Token missing/malformed/unknown. Stop; user must generate a new token.                                               |
| **403** | `{"error": "character_not_claimed", "name": "…", "world": "…"}` | Character resolution failed. Render "claim `<name>` @ `<world>` on the website first". Don't retry until claimed.     |
| **405** | —                                                               | Wrong method (the route accepts only POST).                                                                          |
| **413** | `{"error": "payload_too_large"}`                                | `Content-Length` missing, non-numeric, or over the cap. Don't retry unchanged; split the upload.                    |
| **429** | `{"error": "rate_limited"}` + `Retry-After: <s>`                | Over the per-token limit. Sleep at least `Retry-After` seconds (whole seconds, rounded up).                          |
| **500** | —                                                               | The transactional apply failed. Safe to retry later — writes are idempotent.                                        |
| **503** | `{"error": "sync_disabled"}` + `Retry-After: 3600`              | Global kill switch is off. Back off the full hour.                                                                   |

## Character binding

The plugin identifies a character by a **client-side SHA-256 of its ContentId** — the raw
ContentId (a ulong) never leaves the game client. The server treats the hash as an opaque
stable identifier; the only requirement is that the plugin computes the **same lowercase-hex
digest every session** (fix one byte representation of the ulong and never change it).

Resolution:

1. **Hash first.** A hash already bound to a character resolves directly — it is the durable
   identity, so it **survives renames and world transfers** even when the payload's
   name/world have drifted. The token's user must hold a claim on that character, else 403.
2. **First-upload binding.** An unknown hash falls back to matching `characterName` +
   `homeWorld` (both case-insensitive) against the token owner's **claimed** characters.
   Exactly one candidate → the hash is bound to that character (`bound: true` in the
   response). Zero candidates and ambiguous matches both return the opaque 403 — the server
   never guesses, because binding the wrong character would write another character's data
   under this hash.
3. **Verification side-effect.** The first bound upload promotes the claim to verified: an
   in-game upload carrying the account's token is strong ownership evidence, so plugin users
   skip website bio verification.

**Claims vs. favorites.** Only a *claimed* character is visible to the plugin surface; a
favorite (someone's non-claimed follow) is invisible — `/me` never lists it and the binder
never matches it.

**403 recovery.** `character_not_claimed` deliberately does not distinguish "no such
character" from "not yours". The fix is always the same: **claim the character on the website
first** — the claim flow creates the character record, which the plugin cannot (it has no
Lodestone id, so it never auto-creates characters).

## Behavior the plugin author should know

- **`acquiredAt` timestamps.** An `unlock`-triggered upload stamps the upload moment as the
  acquisition time for every category in it. Snapshot uploads (`interval`/`login`/`manual`)
  stamp the upload time for achievements, minions, and mounts, and leave quests' date null.
  An existing acquisition date is **never overwritten**.
- **Relic proofs from the item manifest.** Possession (`count > 0`) of a proof-scope item
  (the `relic-proofs` group, or the flat manifest) proves that relic stage **and every
  lower-order stage of the same relic**. Proofs are sticky: because possession is volatile
  (the stage-N weapon is consumed by stage N+1), an item absent from a later upload changes
  nothing.
- **Count-tracked items.** For ids in the materials and currencies groups, the reported
  counts are the current total, replacing the stored value — including downward, including
  to zero. This is the one deliberate exception to grow-only semantics, and it is scoped to
  counts: absence still never clears anything, and proof/collection flags remain monotonic.
  GC seals are three independent count-tracked currencies (every Grand Company's balance
  persists in the game and is reported; the website resolves which is spendable from the
  character's Lodestone affiliation). Which currency classes the plugin can read, and
  through which game mechanism, is recorded in [currency-coverage.md](currency-coverage.md)
  — the reference for curating currency ids into manifest groups.
- **Rate limits and backoff.** The default limit is 60 uploads per token per hour. Honor
  `Retry-After` on 429 and 503 and back off — do not tight-loop retries.
- **Kill switches are server-enforced too.** A disabled category is stripped from the payload
  before any write; the stripped keys ride back in `skippedCategories` so the plugin can tell
  the user why a category didn't sync.

## Forward compatibility

The two sides release independently. A newer plugin's unknown payload key is stripped and
logged, never an error; an older plugin simply omits keys, which is safe under monotonic
writes. Adding a collection on the plugin side is one new `ICollector` class (see the repo
`CLAUDE.md`); the per-category toggle and payload key then appear automatically.
