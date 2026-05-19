# hdt-bg-plugin

Hearthstone Battlegrounds lobby analyzer that reads the live game state
from Hearthstone Deck Tracker and tells you which of your offered heroes
is best for this lobby's tribe set.

Two parts that talk over `localhost`:

| Side       | What it is                                                                 | Where it lives                                |
| ---------- | -------------------------------------------------------------------------- | --------------------------------------------- |
| Analyzer   | Single-page vanilla-JS app (no build step, no framework, no server).       | `index.html`                                  |
| HDT plugin | C# / .NET Framework 4.7.2 DLL hosted inside HDT. Serves the lobby state on | `plugin/HsDecktrackBgReader/`                 |
|            | `GET http://localhost:9876/lobby`.                                         |                                               |

When the **Live** toggle in the analyzer is on, the page polls the plugin
every 2 s and fills the tribe ban chips + hero offer slots automatically.
Press **Analyze** (or flip **Auto-Analyze** for hands-free) and you get:

- A per-hero estimated average placement, derived from
  `hero baseline + Σ tribe impact (available tribes)`. Lower is better.
- A list of playable comps sorted by average placement at the chosen
  MMR bucket, with tier / difficulty badges and core-card chips that
  show full card art on hover.

Stats come from the public Firestone CDN
([static.zerotoheroes.com](https://www.firestoneapp.com/)). Nothing in
this repo phones home or stores your data.

## Quick start

### Just the analyzer (no plugin)

```
git clone https://github.com/keremt-dev/hdt-bg-plugin.git
cd hdt-bg-plugin
python -m http.server     # or open index.html directly in a browser
```

Manually click banned tribes, type the 2–4 hero offers, press **Analyze**.

### Full live mode (with HDT plugin)

Prerequisites: Windows, Hearthstone Deck Tracker installed somewhere
standard, .NET 8 SDK (or any modern `dotnet` CLI). No msbuild, no Visual
Studio, no .NET Framework Dev Pack — the build script auto-stages HDT's
binaries and the NuGet package pulls the net472 reference assemblies.

```
git clone https://github.com/keremt-dev/hdt-bg-plugin.git
cd hdt-bg-plugin
dotnet build plugin/HsDecktrackBgReader/HsDecktrackBgReader.csproj -c Debug
```

The Debug `AfterBuild` step drops the DLL into
`%AppData%\HearthstoneDeckTracker\Plugins\HsDecktrackBgReader\`. Launch
HDT, enable the plugin in **Options → Tracker → Plugins**, queue into a
Battlegrounds match, open `index.html` and flip **Live**. Tribe chips and
hero slots populate within ~1.5 s.

If HDT lives somewhere unusual:

```
dotnet build … -p:HdtRoot="D:\Games\HDT\app-1.32.5"
```

For Release (lean log, plugin-only verbose dumps off):

```
dotnet build … -c Release
```

## How it works

The plugin reads three things out of HDT's in-memory state and exposes
them on `GET /lobby` as

```json
{ "banned": [14, 17, 23, 28], "heroes": ["TB_BaconShop_HERO_36", "BG31_HERO_802"] }
```

- **Offered heroes** come from `BattlegroundsHeroPickState.OfferedHeroDbfIds`.
  DBF ids are resolved to canonical card ids via `HearthDb`, skin /
  transform variants (`_SKIN_L`, trailing `t`) are normalized, and a
  per-lobby accumulator keeps the original 4 even as the in-game array
  shrinks on reroll.
- **Banned tribes** come from `BattlegroundsUtils.GetAvailableRaces()`
  (subtracted from the canonical 10-tribe set), with several fallbacks
  including `BattlegroundsMinionsViewModel.Db.Races` and the HearthMirror
  lobby-info provider.
- HDT doesn't fire events during the hero-pick window, so the plugin
  runs a 1.5 s `System.Threading.Timer` poll independent of game events.

See `plugin/SPIKE_NOTES.md` for the full set of HDT internals and how we
found them.

## Repo layout

```
index.html                                 single-file analyzer (UI + Firestone fetch + render)
CLAUDE.md                                  architecture notes for AI / new contributors
plugin/HsDecktrackBgReader/                C# HDT plugin source
  HsDecktrackBgReader.csproj               SDK-style net472 x86, auto-stages HDT refs
  HsDecktrackBgReaderPlugin.cs             IPlugin entry, timer poll, HTTP wiring
  BgStateExtractor.cs                      HDT → lobby snapshot conversion
  LocalHttpServer.cs                       HttpListener serving /lobby
  EntityDumper.cs                          Debug-only verbose dump for development
  stage-hdt-refs.ps1                       Build-time HDT install discovery
plugin/SPIKE_NOTES.md                      HDT BG internals reference
plugin/test-sidecar/mock-sidecar.js        Node mock of the /lobby contract (test browser side without HDT)
bgs_comps.json, bgs_hero_stats.json, …     Reference fixtures of the Firestone feeds (not loaded at runtime)
```

## Caveats

- Names of Hearthstone heroes / tribes in the UI mix Turkish and English
  on purpose.
- The plugin reflects into HDT internals that have no public stability
  guarantee. We try multiple paths and degrade gracefully, but a future
  HDT release could rename `BattlegroundsHeroPickState` or move
  `Races` and break extraction. The `deep_race_probe` diagnostic in
  Debug logs makes finding the new home cheap.
- `HEROES` in `index.html` is the source of truth for hero
  autocomplete. When Blizzard ships new BG heroes refresh it from the
  hero-stats feed (cross-checked with `hearthstonejson.com`).
