# HsDecktrackBgReader — HDT plugin

Stage-1 spike. Reads Hearthstone Battlegrounds lobby state from HDT and dumps it
to disk so we can figure out which entity tags / view models actually carry the
banned tribes and the offered heroes. Once we know that, Stage 2 will turn the
extraction into a small localhost HTTP service that `index.html` polls.

**Status: spike. Not useful end-to-end yet.**

## Prerequisites

- Windows + Visual Studio 2019/2022 (or `msbuild` from Build Tools)
- Hearthstone Deck Tracker installed locally — we need two of its binaries on disk
- .NET Framework 4.7.2 Developer Pack

## Build

1. Copy HDT references into `plugin/HsDecktrackBgReader/refs/`:
   - `HearthstoneDeckTracker.exe`
   - `HearthDb.dll`

   These usually live under `%LocalAppData%\HearthstoneDeckTracker\app-<version>\`
   (Squirrel install). The `refs/` folder is intentionally `.gitignore`-d.

2. Open `HsDecktrackBgReader.csproj` in Visual Studio and build **x86 Debug**, or:

   ```
   msbuild plugin/HsDecktrackBgReader/HsDecktrackBgReader.csproj /p:Configuration=Debug /p:Platform=x86
   ```

   The post-build target auto-copies the DLL to
   `%AppData%\HearthstoneDeckTracker\Plugins\HsDecktrackBgReader\`.

## Enable in HDT

1. Start HDT.
2. Options → Tracker → Plugins. You should see `HS Decktrack BG Reader (spike)`.
3. Check the box to enable. Restart HDT once if it doesn't pick it up.

## Use (spike)

1. Start Hearthstone, queue into a Battlegrounds match.
2. While at the hero-selection screen, click **Dump now** on the plugin row (or
   wait for `OnGameStart` / `OnTurnStart` to fire on its own).
3. Inspect the JSONL file at
   `%LocalAppData%\HsDecktrackBgReader\dump-<timestamp>.jsonl`.
   Look for:
   - `entities_snapshot` rows where `tags.CARDTYPE == HERO` and zone is
     hero-pick-ish → those are the offered heroes.
   - `reflection_probe` hits with names like `AvailableRaces`,
     `AvailableTribes`, `BannedTribes`, `BattlegroundsRaces`.
   - `bg_viewmodel_types` listing HDT's own BG view models — these are gold,
     they tell us exactly which class to reach into in Stage 2.
4. Record findings in `plugin/SPIKE_NOTES.md`.

## Test the browser side without HDT

The Live toggle in `index.html` only needs *something* on
`http://localhost:9876/lobby`. A canned mock is provided:

```
node plugin/test-sidecar/mock-sidecar.js
```

Then open `index.html` in a browser and flip the **Live** switch. The mock
cycles through a few canned lobby states every ~6 seconds; you should see
tribe chips going banned and hero slots filling in without doing anything in
the UI yourself. Stop the mock with Ctrl-C.

## Contract (what the real plugin must serve in Stage 2)

- `GET http://localhost:9876/lobby`
- `200 application/json` when a lobby is known:
  ```json
  { "banned": [14, 17, 23], "heroes": ["TB_BaconShop_HERO_36", "BG31_HERO_802"] }
  ```
  - `banned`: array of Firestone tribe IDs (subset of
    `{11, 14, 15, 17, 20, 23, 24, 26, 28, 29}`)
  - `heroes`: array of Hearthstone `heroCardId` strings, length 1–4, order is
    the order shown to the player.
- `204 No Content` when the plugin is alive but no lobby data yet.
- Must send `Access-Control-Allow-Origin: *` so a browser opening `index.html`
  via `file://` can fetch it.

