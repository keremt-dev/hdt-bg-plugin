# Spike findings — HDT BG state surface

Where the plugin gets its data, and how we worked it out. Anything in
`BgStateExtractor.cs` that looks magic should make sense after reading this.

## Run context

- HDT version observed: **1.52.7.7300**
- Hearthstone build path: standard Squirrel install at
  `%LocalAppData%\HearthstoneDeckTracker\app-1.52.7\`
- Solo BG, hero-pick window — that's the moment the extractor has to be
  useful.

## Offered heroes

**Primary source**:
`Core.Game.BattlegroundsHeroPickState.OfferedHeroDbfIds` — `Int32[]` of card
DBF ids the player is being offered. Populated during the hero-pick window;
**no game event fires while the player sits on this screen**, so we have to
poll it on a timer (HDT's `OnGameStart` / `OnTurnStart` / `OnModeChanged`
all fire either before this array is filled or only after pick is done).

DBF id → card id mapping: `HearthDb.Cards.GetFromDbfId(int).Id`. Reflected
in `BgStateExtractor.DbfIdToCardId` so HearthDb surface drift doesn't
break compile.

**Gotchas**:

1. The array **shrinks** as the player rerolls/picks (4 → 3 → 2 in a real
   run we captured). The accumulator in `BgStateExtractor`
   (`_heroAccumSet` / `_heroAccumOrder`) unions every distinct DBF id seen
   during the lobby's lifetime so the analyzer always shows the full
   original offer set. Reset on `OnModeChanged` out of BACON/GAMEPLAY.

2. Skin and transform variants show up in the DBF lookup:
   - `TB_BaconShop_HERO_57_SKIN_L` (Alexstrasza skin)
   - `TB_BaconShop_HERO_59t` (Aranna's transformed form)

   `HEROES` in `index.html` only knows canonical ids, so
   `NormalizeHeroCardId` regex-captures the canonical prefix
   (`TB_BaconShop_HERO_<N>` or `BG<NN>_HERO_<N>`) and discards the rest.
   `BattlegroundsUtils.TransformableHeroCardidTable` and the explicit
   `Untransformed*Cardid` / `Transformed*Cardid` static fields confirm
   the transform suffixes — useful if we ever want full bidirectional
   mapping instead of "strip and pray".

**Fallback path** (`HeroesFromEntities`): walks
`Core.Game.Entities.Values` filtering CardIds against the hero regex.
Works post-pick because entities don't appear until the player picks.
Kept for resilience if HDT renames the pick-state property.

## Banned tribes

**Primary source**:
`BattlegroundsUtils.GetAvailableRaces()` — a parameterless static method
on `Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsUtils`. Returns the
playable race-id set for the current lobby. Bans are computed as
`AllTribes − playable` where `AllTribes` is the canonical Firestone tribe
id list in `BgStateExtractor` (matches `index.html` `TRIBES`):

```
11=Murloc 14=Beast 15=Demon 17=Mech 20=Dragon
23=Pirate 24=Elemental 26=Quilboar 28=Naga 29=Undead
```

We invoke it via reflection (also handles a 1-arg overload taking a
default Guid, since the per-game cache is keyed by Guid internally).
First successful invocation writes a `races_source` diagnostic line so
the dump tells us which path won.

**Fallback chain** (in `PlayableFromBgDb`):

1. `BattlegroundsMinionsViewModel.Db` (static) → `.Races: HashSet<int>` —
   this is the same `BattlegroundsDb` instance HDT uses for its own
   minion overlay. It's the "global" race set, not necessarily filtered
   to this lobby, but it beats returning nothing.
2. `Core.Game.BattlegroundsDb` — confirmed not present on Core.Game
   directly in HDT 1.52, kept as a cheap defensive try in case future
   versions expose it that way.
3. `HearthMirrorBattlegroundsLobbyInfoProvider` (static) →
   `BattlegroundsLobbyInfo` (HearthMirror type) → `AvailableRaces` /
   `Races` / `PlayableRaces`. Not actually inspected in our spike (data
   only flows here when HearthMirror reads the Hearthstone process
   successfully) but the type exists and looked promising.

**Diagnostic safety net**: when all paths return null,
`RunDeepRaceProbeOnce` (fires exactly once per plugin load) enumerates
every BG/Race/Lobby/Tribe-named type in the HDT assembly and dumps:
- every static property/field with its current value
- every parameterless static method whose name contains
  race/tribe/ban/available (just the name — not invoked, since some
  could mutate state)

The output goes to a `deep_race_probe` JSONL line, which is what we
used to find `BattlegroundsUtils.GetAvailableRaces` after the first
two iterations missed it.

## Non-obvious HDT internals worth knowing

From the type catalogue we built via `WriteBgViewModelProbe`:

- **`BattlegroundsHeroPickState.PickedHeroDbfId : int?`** — the player's
  final pick. Not currently used by the extractor but useful if we want
  to dim the unchosen offers in the analyzer once the pick is done.
- **`BattlegroundsLobbyDetails.LobbyRawHeroDbfIds : List<int>`** — looks
  like the **full 8-player lobby** hero list, not just the local
  player's offers. Useful for an "opponent heroes" feature later.
- **`IsBattlegroundsHeroPickingDone : bool`** on Core.Game flips True
  the moment the player commits. Currently we just lean on
  `IsBattlegroundsMatch` for the BG gate, but this flag would let us
  freeze the served snapshot at pick-completion if we ever want to
  stop polling once the lobby is finalized.
- **`BattlegroundsTrinketPickStates : List<BattlegroundsTrinketPickState>`**
  with `ChosenTrinketDbfId : int?` and a `Params` payload — Tier 7
  trinket picks. Out of scope for the current analyzer.

## Decisions baked into the extractor

1. **Always non-destructive**: every reflection get / method invoke is
   wrapped in try/catch. A surface drift in a future HDT version
   degrades to "no bans" or "no heroes", never crashes the plugin.
2. **Per-lobby accumulator over snapshots**: see "Gotchas" — the live
   array isn't stable, but the union across a single match is.
3. **One-shot deep diagnostic**: `RunDeepRaceProbeOnce` only fires on
   the first failure, so it never spams the log. In Release builds,
   `DumpGameSnapshot` (entities / type catalogue / reflection probe) is
   compiled out entirely via `#if DEBUG`; the diagnostic and the
   normal `extract_*` / `races_source` lines remain.
