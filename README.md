# XIV Shinies Sync

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that syncs
your in-game collection progress to your [XIV Shinies](https://xiv-shinies.com) account —
so you stop hand-marking quests, relics, achievements, mounts, and minions.

> **Status:** early development. This scaffold builds and loads; account linking and the
> actual sync arrive later.

## What it does

The plugin reads completion facts directly from your game client and uploads them over
HTTPS to XIV Shinies, which does all the derivation server-side. A plugin upload is
first-party evidence from inside the game client, so it verifies character ownership and
outranks a Lodestone scrape.

## What gets sent

Only your **own local character's** collection facts, and only for the categories you opt
into:

- Completed **quest** IDs
- Unlocked **achievement**, **mount**, and **minion** IDs
- **Possession counts** for the specific relic-stage items the server asks for

Your character's ContentId is **hashed on your machine** before anything leaves the game —
the raw ID never travels. Nothing about other players is ever read or sent.

The exact wire format is documented in [`docs/api-contract.md`](docs/api-contract.md); the
deployed XIV Shinies server is its authority.

## Opt-in and consent

The plugin is **fully opt-in**. On first launch it walks you through what gets sent and
asks you to choose categories explicitly. You can toggle any category, or turn syncing off
entirely, at any time from the settings window (`/shinies`).

## Installing

### From the custom plugin repository (once released)

1. In-game, open `/xlsettings` → **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:
   `https://raw.githubusercontent.com/noranda/xiv-shinies-plugin/main/repo.json`
3. Save, then install **XIV Shinies Sync** from `/xlplugins`.

_(The `repo.json` pluginmaster and tagged release zips land in a later task.)_

### For development

1. `dotnet build -c Release`
2. In-game, open `/xlsettings` → **Experimental** → **Dev Plugin Locations**.
3. Add the built manifest path:
   `src\XIVShinies.SyncPlugin\bin\Release\XIVShinies.SyncPlugin.json`
4. Enable the plugin in `/xlplugins` → **Dev Tools** and run `/shinies`.

## Building

```
dotnet build      # compile the plugin (Release produces the loadable DLL + manifest)
dotnet test       # run the xUnit suite (pure logic only; game surfaces are QA'd in-game)
```

Requires the **.NET 10 SDK** and a **XIVLauncher** install that has launched the game once
(the Dalamud dev libraries the build references live under
`%AppData%\XIVLauncher\addon\Hooks\dev`).

## License

[MIT](LICENSE) © Noranda
