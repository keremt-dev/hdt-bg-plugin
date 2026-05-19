# Spike notes — BG state discovery

Place to record what we actually find in the dump file once the plugin is built
and run inside HDT at a BG hero-selection screen. This file is the input for
Stage 2 (extraction + HTTP server).

## Run log

(Fill in per session.)

- Date:
- HDT version:
- Hearthstone build:
- Dump file:

## Offered heroes

- Where do the 2–4 offered heroes show up?
  - Entity tag combo that identifies them: `CARDTYPE = ?`, `ZONE = ?`, …
  - Any flag that distinguishes "offered to me" vs "offered to opponents"?
  - CardId format (e.g. `TB_BaconShop_HERO_*`, `BG31_HERO_*`)?
- Alternative source: which `Battlegrounds…ViewModel` exposes the picks?
  - Class fully-qualified name:
  - Property name and shape:
  - Static or instance? How to reach the instance?

## Banned / available tribes

- Where do tribe bans show up?
  - Entity-level tag (which one)?
  - GAME entity with a list of `AvailableRaces` / `BannedRaces`?
  - Power.log fallback marker (which substring to grep)?
- Tribe ID encoding — does HDT use the same integer IDs as Firestone?
  (14=Beast, 15=Demon, 17=Mech, 20=Dragon, 23=Pirate, 24=Elemental, 26=Quilboar,
  28=Naga, 29=Undead, 11=Murloc.)

## Reflection probe hits

(Paste the JSON `reflection_probe` entries that looked promising.)

## bg_viewmodel_types output

(Paste the most useful classes found by `WriteBgViewModelProbe`.)

## Decision for Stage 2

- Primary extraction path: `Core.Game.Entities` walk / reflection on view model / both / fallback to Power.log.
- Risk left after spike:
- Open questions for the user:
