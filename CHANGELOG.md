# Changelog

All user-facing changes to XIV Shinies Sync, newest first.

Each `## vX.Y.Z — YYYY-MM-DD` section below is written for players, not developers, and doubles
as the GitHub release notes for that version: the release workflow copies the top section
verbatim into the release it publishes. Sections are added by the release flow (see
`.claude/skills/releasing/`), one per release, immediately under this line.

## v0.2.0 — 2026-07-15

- Sync your relic materials and currency balances (gil included) — the website's forge tray now fills and updates itself as you gather and spend
- Choose exactly which item groups to share: relic gear, materials, and currencies each get their own consent checkbox, and newly offered groups arrive switched off with a New badge
- The upload log reports relic steps the server actually proved ("2 new steps proven") instead of guessing from count changes
- A new "Reading from" panel shows what each sync could read, and names the container to open once when something can't be
- Item counts track high-quality and collectable copies separately
- The item scan is around ten times faster
- The upload log is now per character — it clears on logout

## v0.1.0 — 2026-07-11

- First release: your FFXIV collections sync to xiv-shinies.com automatically
- New quest, achievement, mount, and minion unlocks appear on the site within seconds
- Relic progress is proven by items you own — inventory, armoire, dresser, saddlebag, retainers
- Fully opt-in: a short setup shows what each collection sends before anything uploads
- A Recent uploads log shows every upload's outcome and exactly what was sent
- Your character is identified only by a one-way fingerprint computed on your machine
