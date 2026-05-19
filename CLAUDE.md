# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-page Hearthstone Battlegrounds lobby analyzer. The entire app lives in `index.html` — vanilla JS, Tailwind via CDN, no build step, no package manager, no server. Open the file in a browser (or serve the directory statically) and it runs.

UI copy is mixed Turkish / English. Keep that mix when editing user-facing strings — do not "translate" Turkish labels to English unless asked.

## Running / iterating

There is no build, lint, or test command. To preview changes, open `index.html` directly or run any static server from the repo root (e.g. `python -m http.server`). After editing, hard-refresh the browser.

## Architecture

Everything is in `index.html`:

- **Static constants** (top of `<script>`): `TRIBES` (10 BG minion types with Firestone tribe IDs), `HEROES` (hero name → `heroCardId` like `TB_BaconShop_HERO_36` or `BG31_HERO_xxx`), and a `heroArt()` helper that builds image URLs from `art.hearthstonejson.com`.
- **`state` object**: the entire app state — `banned` (Set of tribe IDs, ≤5), `heroes` (4-slot array, nullable), `mmr`, `period`, plus cached `data` / `comps` / `strategies` after fetch.
- **Render functions**: `renderTribes()`, `renderSlots()`, `render()` → `renderLobbyBar()` + `renderHeroRanking()` + `renderComps()`. Each rebuilds its DOM subtree from `state` by setting `innerHTML`; event handlers are re-bound on every render. There is no virtual DOM or framework.
- **Data fetch** is on-demand in the Analyze handler (`analyzeBtn.onclick`), parallel `Promise.all` of three remote JSON files from `static.zerotoheroes.com`:
  - hero stats: `…/api/bgs/hero-stats/mmr-${mmr}/${period}/overview-from-hourly.gz.json`
  - comp stats: `…/api/bgs/comp-stats/${period}/overview-from-hourly.gz.json`
  - comp strategies: `…/hearthstone/data/battlegrounds-strategies/bgs-comps-strategies.gz.json`

  (Despite the `.gz.json` extension, these are served as plain JSON with Content-Encoding gzip — `fetch().json()` handles it.)

### The core calculation

In `render()`:

- **Hero ranking** = for each picked hero, find its row in `heroStats` by `heroCardId`, then `est = row.averagePosition + Σ row.tribeStats[t].impactAveragePosition` over tribes that are *not* banned. Lower est = better. The first row after sorting wins the gold "PICK" badge.
- **Comp playability** = scan `state.comps.compStats`, match tribe names against each comp's `archetype` string (case-insensitive substring of tribe name), drop any comp whose name mentions a banned tribe, drop comps with `dp ≤ 50`, sort by average placement at the selected MMR bucket. Strategies (difficulty, power tier, tip, core cards) are joined in by `archetype` → `compId`.

If you touch the ranking formula, also update the help line in the header of `renderHeroRanking()` ("est = ref + Σ impact (available tribes)") and the footer note under the Analyze button — they're how users learn what the number means.

### Cards / tooltips

Core-card chips have `data-cid` (the Hearthstone card ID). The tooltip handler tries `art.hearthstonejson.com/v1/render/latest/...` first, then falls back to `.../v1/bgs/latest/...` on image error. New card-image surfaces should reuse `.card-chip[data-cid]` so the existing delegated mouseover handler picks them up — don't add per-chip listeners.

## Data files in the repo

`bgs_comps.json`, `bgs_hero_stats.json`, `bgs_top25.json`, `comp_strategies.json`, `heroes.json`, `heroes_active.csv`, `heroes_compact.json`, `kths-decktrackbgs_hero_stats.json` are **snapshots / samples** of the same Firestone feeds the app fetches at runtime. The app does not load any of them — they exist as reference fixtures for understanding the schemas. The `HEROES` constant inside `index.html` is the source of truth for which heroes are selectable in the UI; if you regenerate it from `heroes_compact.json` (which is the same `[name, id]` pair format), keep them in sync.
