# HsDecktrackBgReader — HDT plugin

Stage-1 spike. Reads Hearthstone Battlegrounds lobby state from HDT and dumps it
to disk so we can figure out which entity tags / view models actually carry the
banned tribes and the offered heroes. Once we know that, Stage 2 will turn the
extraction into a small localhost HTTP service that `index.html` polls.

**Status: spike. Not useful end-to-end yet.**

## Prerequisites

- Windows + Visual Studio 2019/2022 (or `msbuild` from Build Tools)
- Hearthstone Deck Tracker installed locally (any normal install works —
  the build script auto-discovers it)
- .NET Framework 4.7.2 Developer Pack — install from
  https://aka.ms/msbuild/developerpacks if `msbuild` complains about MSB3644

## Build

Just build the project — no manual setup. The `AutoStageHdtRefs` MSBuild
target runs `stage-hdt-refs.ps1` before reference resolution, which finds
the most recent HDT install on this machine and copies
`HearthstoneDeckTracker.exe` + `HearthDb.dll` into `refs/`. Standard search
order:

1. `%LocalAppData%\HearthstoneDeckTracker\app-<latest>\` (Squirrel install — typical)
2. `%ProgramFiles%\HearthstoneDeckTracker\`
3. `%ProgramFiles(x86)%\HearthstoneDeckTracker\`

Open `HsDecktrackBgReader.csproj` in Visual Studio (**x86 / Debug**), or:

```
msbuild plugin/HsDecktrackBgReader/HsDecktrackBgReader.csproj /p:Configuration=Debug /p:Platform=x86
```

If HDT lives somewhere unusual, override the discovery:

```
msbuild HsDecktrackBgReader.csproj /p:HdtRoot=D:\Games\HDT\app-1.32.5
```

The Debug `AfterBuild` step then copies the built DLL into
`%AppData%\HearthstoneDeckTracker\Plugins\HsDecktrackBgReader\` so HDT
picks it up on next launch.

The `refs/` folder is `.gitignore`-d — it gets re-staged on each clean
build, and we never commit Blizzard-adjacent binaries.

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

