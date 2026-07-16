# Currency read coverage

Which classes of in-game currency the item scan can resolve by item id, and through which
mechanism. Established by an in-game sweep on 2026-07-12 (live client, one character):
each id below was resolved through the same two-step read the real scan uses — the
container walk first, then the `CurrencyManager` fallback for ids the walk did not find —
and the result recorded from the log.

This table exists for whoever curates manifest groups server-side: an id in a covered
class will auto-fill once it joins a group; nothing here requires plugin changes to extend
to other ids of the same class.

## Mechanisms

- **Container walk** — the scan reads the `InventoryType.Currency` container (among the 20
  live containers) directly. Covers the "classic" currencies the in-game Currency window
  lists. A zero balance may occupy no container slot; the id then simply reports the
  honest explicit zero.
- **CurrencyManager fallback** — for manifest ids the walk did not find, the scan probes
  the game's `CurrencyManager` (membership check first, then the count). Covers the
  bucket-tracked currencies. Notably, buckets answer even at zero balance, so these ids
  report zeros verified live (on the wire it is the same explicit zero as any other —
  the payload carries no distinction).

## Verified in-game (2026-07-12)

| Currency | Item id | Mechanism | Result |
| --- | --- | --- | --- |
| Gil | 1 | container walk | ✓ resolved |
| Storm Seals (GC) | 20 | container walk | ✓ resolved |
| Serpent Seals (GC) | 21 | container walk | ✓ resolved |
| Flame Seals (GC) | 22 | (zero balance) | mechanism unconfirmed at 0 — class proven by ids 20/21 |
| Wolf Marks | 25 | container walk | ✓ resolved |
| Allied Seals | 27 | container walk | ✓ resolved |
| Allagan Tomestone of Poetics | 28 | container walk | ✓ resolved |
| MGP | 29 | container walk | ✓ resolved |
| Centurio Seal | 10307 | CurrencyManager | ✓ resolved |
| White Crafters' Scrip | 25199 | CurrencyManager | ✓ resolved (authoritative 0; currency discontinued in-game) |
| White Gatherers' Scrip | 25200 | CurrencyManager | ✓ resolved (authoritative 0; currency discontinued in-game) |
| Sack of Nuts | 26533 | CurrencyManager | ✓ resolved |
| Bicolor Gemstone | 26807 | CurrencyManager | ✓ resolved |
| Skybuilders' Scrip | 28063 | CurrencyManager | ✓ resolved |
| Purple Crafters' Scrip | 33913 | CurrencyManager | ✓ resolved |
| Purple Gatherers' Scrip | 33914 | CurrencyManager | ✓ resolved |
| Trophy Crystal | 36656 | CurrencyManager | ✓ resolved |
| Orange Crafters' Scrip | 41784 | CurrencyManager | ✓ resolved |
| Orange Gatherers' Scrip | 41785 | CurrencyManager | ✓ resolved |

Every owned id resolved through at least one mechanism — no coverage gap was found.
The purple and orange scrips are the ones current relic-tool costs reference (white scrips
are discontinued in-game); all four resolved with live balances.

## Expected-covered, not yet individually swept

These share a verified id's documented `CurrencyManager` bucket or its container class, so
the same read is expected to resolve them; they have not been individually confirmed
in-game:

- Ventures (documented in the same bucket tables as verified ids)
- Island Sanctuary materials (documented bucket)
- Other current-patch tomestones (same container class as Poetics — a container-walk
  expectation, not a bucket one)

When one of these joins a manifest group, its first live sync is the confirmation — an
unexpected zero for a currency the character demonstrably holds is the signal to re-check
here.

## Notes for the website's curation

- Only real **Item-sheet ids** are resolvable. Key items (`EventItem` sheet) are a
  different id namespace and are not readable through either mechanism.
- Zero-balance container currencies (like the Flame Seals row above) report a plain
  explicit zero rather than proving which mechanism would carry them — harmless either
  way, since the zero is honest.
