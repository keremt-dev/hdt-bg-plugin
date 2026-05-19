using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace HsDecktrackBgReader
{
    /// <summary>
    /// Pulls a lobby snapshot out of HDT's in-memory game state.
    ///
    /// Returns a small DTO that the LocalHttpServer can serialize:
    ///   { banned: int[],   // Firestone tribe ids — see TribeId
    ///     heroes: string[] // Hearthstone heroCardId strings (TB_BaconShop_HERO_*, BG*_HERO_*)
    ///   }
    ///
    /// IMPORTANT — Stage 1 spike has not been run yet, so the extraction logic
    /// below is best-effort. The hero pattern-match is solid (cardId prefixes
    /// are stable across patches). The banned-tribes side is speculative: we
    /// reflect over Core.Game looking for an "AvailableRaces" /
    /// "BattlegroundsRaces" / similar property and infer bans by subtracting
    /// from the canonical tribe set. SPIKE_NOTES.md is where the confirmed
    /// property names land, and this file should be revised once we know.
    /// </summary>
    internal class BgStateExtractor
    {
        // Firestone tribe IDs, kept in sync with index.html `TRIBES`. The HTTP
        // contract sends these integers; the browser maps them to the chip UI.
        private static readonly int[] AllTribes = new[] { 11, 14, 15, 17, 20, 23, 24, 26, 28, 29 };

        // Hearthstone heroCardId prefixes for BG heroes. Both legacy
        // TB_BaconShop_HERO_NN and the modern BG##_HERO_NNN naming are valid;
        // we match either.
        private static readonly Regex HeroCardIdRegex =
            new Regex(@"^(TB_BaconShop_HERO_\d+|BG\d+_HERO_\d+)$", RegexOptions.Compiled);

        // Reflection probe — property names on Core.Game (or any singleton
        // hanging off it) that plausibly carry the BG race list. First hit wins.
        private static readonly string[] RaceListPropertyCandidates = new[]
        {
            "BattlegroundsRaces",
            "AvailableRaces",
            "AvailableBattlegroundsRaces",
            "AvailableTribes",
            "PlayableRaces",
        };

        private readonly EntityDumper _dumper;

        public BgStateExtractor(EntityDumper dumper)
        {
            _dumper = dumper;
        }

        /// <summary>
        /// Convenience: build a snapshot and serialize to the JSON the HTTP
        /// contract expects. Returns null when there is nothing to serve.
        /// </summary>
        public string TryBuildJson()
        {
            var snap = TryBuild();
            return snap == null ? null : SerializeLobby(snap);
        }

        private static string SerializeLobby(LobbySnapshot s)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"banned\":[");
            if (s.banned != null)
                for (int i = 0; i < s.banned.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(s.banned[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            sb.Append("],\"heroes\":[");
            if (s.heroes != null)
                for (int i = 0; i < s.heroes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var h = s.heroes[i] ?? "";
                    sb.Append('"');
                    foreach (var c in h)
                    {
                        if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                        else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                    }
                    sb.Append('"');
                }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Build a snapshot. Returns null when we have nothing worth serving;
        /// the HTTP server interprets null as 204.
        /// </summary>
        public LobbySnapshot TryBuild()
        {
            try
            {
                var game = Hearthstone_Deck_Tracker.Core.Game;
                if (game == null) return null;

                if (!IsBattlegrounds(game))
                {
                    _dumper?.Write("extract_skip_not_bg", new { });
                    return null;
                }

                var heroes = ExtractOfferedHeroes(game);
                var banned = ExtractBannedTribes(game);

                if (heroes.Count == 0 && banned.Count == 0)
                {
                    _dumper?.Write("extract_empty", new { reason = "no heroes and no banned tribes inferred" });
                    return null;
                }

                _dumper?.Write("extract_ok", new { heroes, banned });
                return new LobbySnapshot { banned = banned.ToArray(), heroes = heroes.ToArray() };
            }
            catch (Exception ex)
            {
                _dumper?.Write("extract_error", new { message = ex.Message, type = ex.GetType().FullName });
                return null;
            }
        }

        private bool IsBattlegrounds(object game)
        {
            // Try a few likely flags; HDT's API name has drifted across versions.
            foreach (var name in new[] { "IsBattlegroundsMatch", "IsBattlegroundsMode", "IsInBattlegrounds" })
            {
                var v = GetProp(game, name);
                if (v is bool b && b) return true;
            }
            // Fall back to game-mode enum / property
            foreach (var name in new[] { "CurrentGameMode", "CurrentGameType" })
            {
                var v = GetProp(game, name);
                if (v != null && v.ToString().IndexOf("Battleground", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            // If we can't prove it isn't BG, be permissive — the heroes regex
            // will reject non-BG cards anyway and the racelist will be empty.
            return true;
        }

        private List<string> ExtractOfferedHeroes(object game)
        {
            var result = new List<string>();
            var entities = GetProp(game, "Entities");
            if (entities == null) return result;

            IEnumerable values = GetProp(entities, "Values") as IEnumerable ?? (entities as IEnumerable);
            if (values == null) return result;

            // De-dup by cardId, keep insertion order, cap at 4 (BG offers ≤4).
            var seen = new HashSet<string>();
            foreach (var entity in values)
            {
                var cardId = GetProp(entity, "CardId") as string;
                if (string.IsNullOrEmpty(cardId)) continue;
                if (!HeroCardIdRegex.IsMatch(cardId)) continue;

                // Skip the player's *chosen* hero so we don't dilute the offer
                // list. Best signal we have without the spike: an entity that's
                // both a hero and "belongs to" the player has IsPlayer / Controller
                // set. The spike will give us a stronger filter.
                var isPlayerHero = GetProp(entity, "IsPlayer") as bool? == true
                                    && GetProp(entity, "IsHero")   as bool? == true;
                // Heuristic: keep all distinct BG heroCardIds we see and let
                // the browser show them; once spike data lands we tighten this.

                if (seen.Add(cardId))
                {
                    result.Add(cardId);
                    if (result.Count >= 4) break;
                }
                _ = isPlayerHero; // marked unused; will gate once spike confirms
            }
            return result;
        }

        private List<int> ExtractBannedTribes(object game)
        {
            // 1) Best case: HDT exposes a "playable races" list. Subtract from
            //    the full tribe set to get bans.
            var playable = FindPlayableRaces(game);
            if (playable != null)
            {
                var playableSet = new HashSet<int>(playable);
                return AllTribes.Where(t => !playableSet.Contains(t)).ToList();
            }

            // 2) No signal found — return empty list. The UI will show no
            //    tribes banned and the user can click them manually. This is
            //    the safe failure mode until the spike confirms a source.
            _dumper?.Write("banned_unresolved", new { tried = RaceListPropertyCandidates });
            return new List<int>();
        }

        private List<int> FindPlayableRaces(object game)
        {
            // Direct candidates on Core.Game
            foreach (var name in RaceListPropertyCandidates)
            {
                var v = GetProp(game, name);
                var ids = TryAsIntList(v);
                if (ids != null && ids.Count > 0) return ids;
            }
            // One level deeper — try anything named "Battlegrounds*" and probe
            // its candidate properties too.
            foreach (var p in game.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.Name.IndexOf("Battleground", StringComparison.OrdinalIgnoreCase) < 0) continue;
                object owner;
                try { owner = p.GetValue(game); } catch { continue; }
                if (owner == null) continue;
                foreach (var name in RaceListPropertyCandidates)
                {
                    var v = GetProp(owner, name);
                    var ids = TryAsIntList(v);
                    if (ids != null && ids.Count > 0) return ids;
                }
            }
            return null;
        }

        private static List<int> TryAsIntList(object v)
        {
            if (v == null) return null;
            if (!(v is IEnumerable en) || v is string) return null;

            var ids = new List<int>();
            foreach (var item in en)
            {
                if (item == null) continue;
                int? id = null;
                if (item is int i) id = i;
                else if (item is Enum) { try { id = Convert.ToInt32(item); } catch { } }
                else if (item is IConvertible) { try { id = Convert.ToInt32(item); } catch { } }
                else
                {
                    // CardRace-like object — try a numeric property
                    var v2 = GetProp(item, "Value") ?? GetProp(item, "Id") ?? GetProp(item, "Race");
                    if (v2 is int i2) id = i2;
                    else if (v2 is IConvertible) { try { id = Convert.ToInt32(v2); } catch { } }
                }
                if (id.HasValue) ids.Add(id.Value);
            }
            return ids;
        }

        private static object GetProp(object target, string name)
        {
            try
            {
                if (target == null) return null;
                var p = target.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (p != null) return p.GetValue(target);
                var f = target.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return f.GetValue(target);
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// DTO for /lobby — public fields match the JSON contract exactly so the
    /// JSON serializer in EntityDumper round-trips them by name.
    /// </summary>
    internal class LobbySnapshot
    {
        public int[] banned { get; set; }
        public string[] heroes { get; set; }
    }
}
