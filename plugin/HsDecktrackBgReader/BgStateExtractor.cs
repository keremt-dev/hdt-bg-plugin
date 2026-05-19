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
    ///   { banned: int[],   // Firestone tribe ids — see AllTribes below
    ///     heroes: string[] // Hearthstone heroCardId strings (TB_BaconShop_HERO_*, BG*_HERO_*)
    ///   }
    ///
    /// After the Stage 1 spike we know exactly which HDT properties carry
    /// the data we want:
    ///   • Core.Game.BattlegroundsHeroPickState.OfferedHeroDbfIds (int[])
    ///   • Core.Game.BattlegroundsDb.Races (HashSet&lt;int&gt;) — playable tribes
    /// Both are dictionary-style state populated during hero pick, *before*
    /// OnTurnStart fires, which is exactly when we need them. We map DbfId →
    /// CardId via HearthDb.Cards.GetFromDbfId, and bans = AllTribes − Races.
    ///
    /// The old reflection-driven heuristics are kept as fallbacks so future
    /// HDT renames degrade gracefully instead of going silent.
    /// </summary>
    internal class BgStateExtractor
    {
        // Firestone tribe IDs, kept in sync with index.html `TRIBES`.
        private static readonly int[] AllTribes = new[] { 11, 14, 15, 17, 20, 23, 24, 26, 28, 29 };

        // Fallback regex when DbfId mapping isn't available (e.g. card DB
        // miss for a brand-new patch hero).
        private static readonly Regex HeroCardIdRegex =
            new Regex(@"^(TB_BaconShop_HERO_\d+|BG\d+_HERO_\d+)$", RegexOptions.Compiled);

        private readonly EntityDumper _dumper;
        private bool _deepProbeDone;

        public BgStateExtractor(EntityDumper dumper)
        {
            _dumper = dumper;
        }

        /// <summary>
        /// Convenience: build a snapshot and serialize. Returns null when there
        /// is nothing to serve.
        /// </summary>
        public string TryBuildJson()
        {
            var snap = TryBuild();
            return snap == null ? null : SerializeLobby(snap);
        }

        public LobbySnapshot TryBuild()
        {
            try
            {
                var game = Hearthstone_Deck_Tracker.Core.Game;
                if (game == null) return null;
                if (!IsBattlegrounds(game)) { _dumper?.Write("extract_skip_not_bg", new { }); return null; }

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
            // Spike confirmed Core.Game exposes IsBattlegroundsMatch as a bool.
            var v = GetMember(game, "IsBattlegroundsMatch");
            if (v is bool b) return b;
            // Defensive: if the property doesn't exist on this HDT version,
            // let the extractor proceed — race/hero accessors will just return
            // empty data and we'll short-circuit later.
            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // HEROES
        //   Primary:  Core.Game.BattlegroundsHeroPickState.OfferedHeroDbfIds
        //             → mapped to CardIds via HearthDb.Cards.GetFromDbfId
        //   Fallback: walk Core.Game.Entities for entities whose CardId
        //             matches the BG hero regex (works post-pick, since
        //             entities don't appear until the player picks)
        // ──────────────────────────────────────────────────────────────────

        private List<string> ExtractOfferedHeroes(object game)
        {
            var heroes = HeroesFromPickState(game);
            if (heroes.Count > 0) return heroes;

            return HeroesFromEntities(game);
        }

        private List<string> HeroesFromPickState(object game)
        {
            var result = new List<string>();
            var pickState = GetMember(game, "BattlegroundsHeroPickState");
            if (pickState == null) return result;

            var offered = GetMember(pickState, "OfferedHeroDbfIds") as IEnumerable;
            if (offered == null) return result;

            foreach (var item in offered)
            {
                if (item == null) continue;
                int dbfId;
                try { dbfId = Convert.ToInt32(item); }
                catch { continue; }
                if (dbfId <= 0) continue;

                var cardId = DbfIdToCardId(dbfId);
                if (!string.IsNullOrEmpty(cardId)) result.Add(cardId);
            }
            return result;
        }

        private List<string> HeroesFromEntities(object game)
        {
            var result = new List<string>();
            var entities = GetMember(game, "Entities");
            if (entities == null) return result;

            IEnumerable values = GetMember(entities, "Values") as IEnumerable ?? (entities as IEnumerable);
            if (values == null) return result;

            var seen = new HashSet<string>();
            foreach (var entity in values)
            {
                var cardId = GetMember(entity, "CardId") as string;
                if (string.IsNullOrEmpty(cardId)) continue;
                if (!HeroCardIdRegex.IsMatch(cardId)) continue;
                if (seen.Add(cardId))
                {
                    result.Add(cardId);
                    if (result.Count >= 4) break;
                }
            }
            return result;
        }

        private string DbfIdToCardId(int dbfId)
        {
            // Use HearthDb directly. Reflection so we don't fail to compile
            // if HearthDb's surface shifts; the lookup is stable in practice.
            try
            {
                var cardsType = typeof(HearthDb.Cards);
                var method = cardsType.GetMethod("GetFromDbfId", new[] { typeof(int) })
                          ?? cardsType.GetMethod("GetFromDbfId", new[] { typeof(int), typeof(bool) });
                if (method == null) return null;

                object card;
                if (method.GetParameters().Length == 1)
                    card = method.Invoke(null, new object[] { dbfId });
                else
                    card = method.Invoke(null, new object[] { dbfId, false });
                if (card == null) return null;

                return GetMember(card, "Id") as string;
            }
            catch (Exception ex)
            {
                _dumper?.Write("dbfid_lookup_error", new { dbfId, message = ex.Message });
                return null;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // BANNED TRIBES
        //   Primary:   Core.Game.BattlegroundsDb.Races (HashSet<int>) holds
        //              the playable tribes; bans = AllTribes − playable.
        //   Fallback:  legacy reflection probe across a handful of candidate
        //              property names (older HDT builds).
        // ──────────────────────────────────────────────────────────────────

        private List<int> ExtractBannedTribes(object game)
        {
            var playable = PlayableFromBgDb(game) ?? PlayableFromLegacyProbe(game);
            if (playable == null)
            {
                _dumper?.Write("banned_unresolved", new { });
                RunDeepRaceProbeOnce();
                return new List<int>();
            }
            return AllTribes.Where(t => !playable.Contains(t)).ToList();
        }

        /// <summary>
        /// Diagnostic: when we can't resolve playable races by the known
        /// paths, enumerate every static property on every BG-named type in
        /// the HDT assembly that returns a HashSet/List/IEnumerable of ints,
        /// and log its current value. Runs exactly once per plugin load so
        /// the JSONL doesn't bloat. The output is what we use to add a new
        /// path to PlayableFromBgDb on the next iteration.
        /// </summary>
        private void RunDeepRaceProbeOnce()
        {
            if (_deepProbeDone) return;
            _deepProbeDone = true;
            try
            {
                var asm = typeof(Hearthstone_Deck_Tracker.API.GameEvents).Assembly;
                var hits = new List<object>();
                foreach (var t in asm.GetTypes())
                {
                    var n = t.Name;
                    if (n.IndexOf("Battleground", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Race",         StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Lobby",        StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("Tribe",        StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var staticGetters = new List<object>();
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                    {
                        object val = null; string err = null;
                        try { val = p.GetValue(null); } catch (Exception ex) { err = ex.GetBaseException().Message; }
                        staticGetters.Add(new
                        {
                            name = p.Name,
                            type = p.PropertyType.FullName,
                            value = SummarizeForProbe(val),
                            error = err,
                        });
                    }
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
                    {
                        object val = null; string err = null;
                        try { val = f.GetValue(null); } catch (Exception ex) { err = ex.GetBaseException().Message; }
                        staticGetters.Add(new
                        {
                            name = f.Name + "(field)",
                            type = f.FieldType.FullName,
                            value = SummarizeForProbe(val),
                            error = err,
                        });
                    }
                    if (staticGetters.Count > 0)
                        hits.Add(new { type = t.FullName, members = staticGetters });
                }
                _dumper?.Write("deep_race_probe", new { typeCount = hits.Count, hits });
            }
            catch (Exception ex)
            {
                _dumper?.Write("deep_race_probe_error", new { message = ex.Message });
            }
        }

        private static object SummarizeForProbe(object v)
        {
            if (v == null) return null;
            if (v is string s) return s.Length > 120 ? s.Substring(0, 120) : s;
            var type = v.GetType();
            if (type.IsPrimitive || type.IsEnum) return v.ToString();
            if (v is IEnumerable en && !(v is string))
            {
                var list = new List<object>();
                int i = 0;
                foreach (var item in en)
                {
                    if (i++ >= 30) { list.Add("..."); break; }
                    list.Add(item?.ToString());
                }
                return new { kind = "enumerable", items = list, count = i };
            }
            return type.FullName + ": " + v.ToString();
        }

        private HashSet<int> PlayableFromBgDb(object game)
        {
            // BattlegroundsDb.Races (HashSet<int>) is HDT's authoritative list
            // of playable tribes for the current lobby — but the BattlegroundsDb
            // instance hides behind several access patterns depending on the
            // HDT version. Try them all and return the first non-empty Races.
            var asm = typeof(Hearthstone_Deck_Tracker.API.GameEvents).Assembly;

            // a) Direct property on Core.Game
            var candidate = GetMember(game, "BattlegroundsDb");
            var races = RacesFrom(candidate, "Core.Game.BattlegroundsDb");
            if (races != null) return races;

            // b) Static singletons on the BattlegroundsDb type itself
            var bgDbType = asm.GetType("Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsDb");
            if (bgDbType != null)
            {
                foreach (var n in new[] { "Instance", "Default", "Current" })
                {
                    var inst = GetMember(bgDbType, n);
                    races = RacesFrom(inst, "BattlegroundsDb." + n);
                    if (races != null) return races;
                }
            }

            // c) BattlegroundsDbSingleton.Instance (HDT also has this wrapper)
            var singletonType = asm.GetType("Hearthstone_Deck_Tracker.Utility.Battlegrounds.BattlegroundsDbSingleton");
            if (singletonType != null)
            {
                foreach (var n in new[] { "Instance", "Default", "Current", "Get" })
                {
                    var inst = GetMember(singletonType, n);
                    if (inst == null)
                    {
                        // Try as method
                        try
                        {
                            var m = singletonType.GetMethod(n, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                            if (m != null && m.GetParameters().Length == 0) inst = m.Invoke(null, null);
                        }
                        catch { }
                    }
                    if (inst == null) continue;
                    races = RacesFrom(inst, "BattlegroundsDbSingleton." + n);
                    if (races != null) return races;
                    // Maybe singleton has an inner .Instance field/property
                    var inner = GetMember(inst, "Instance") ?? GetMember(inst, "Value") ?? GetMember(inst, "Db");
                    if (inner != null)
                    {
                        races = RacesFrom(inner, "BattlegroundsDbSingleton." + n + ".(inner)");
                        if (races != null) return races;
                    }
                }
            }

            // d) HearthMirror lobby info provider — per-lobby tribe set may
            //    live on BattlegroundsLobbyInfo as AvailableRaces or similar.
            var providerType = asm.GetType("Hearthstone_Deck_Tracker.Hearthstone.HearthMirrorBattlegroundsLobbyInfoProvider");
            if (providerType != null)
            {
                // Look for a static instance
                foreach (var n in new[] { "Instance", "Default", "Current" })
                {
                    var inst = GetMember(providerType, n);
                    if (inst == null) continue;
                    var lobby = GetMember(inst, "BattlegroundsLobbyInfo");
                    if (lobby == null) continue;
                    foreach (var prop in new[] { "AvailableRaces", "Races", "PlayableRaces" })
                    {
                        races = TryAsIntSet(GetMember(lobby, prop));
                        if (races != null) { _dumper?.Write("races_source", new { from = "HearthMirrorBattlegroundsLobbyInfoProvider." + n + ".BattlegroundsLobbyInfo." + prop }); return races; }
                    }
                }
            }
            return null;
        }

        private HashSet<int> RacesFrom(object source, string sourceLabel)
        {
            if (source == null) return null;
            var races = TryAsIntSet(GetMember(source, "Races"));
            if (races != null)
            {
                _dumper?.Write("races_source", new { from = sourceLabel, count = races.Count });
                return races;
            }
            return null;
        }

        private HashSet<int> PlayableFromLegacyProbe(object game)
        {
            var names = new[] { "BattlegroundsRaces", "AvailableRaces", "AvailableTribes", "PlayableRaces" };
            foreach (var name in names)
            {
                var v = GetMember(game, name);
                var s = TryAsIntSet(v);
                if (s != null && s.Count > 0) return s;
            }
            return null;
        }

        private static HashSet<int> TryAsIntSet(object v)
        {
            if (v == null || v is string) return null;
            if (!(v is IEnumerable en)) return null;

            var set = new HashSet<int>();
            foreach (var item in en)
            {
                if (item == null) continue;
                int? id = null;
                if (item is int i) id = i;
                else if (item is Enum) { try { id = Convert.ToInt32(item); } catch { } }
                else if (item is IConvertible) { try { id = Convert.ToInt32(item); } catch { } }
                if (id.HasValue) set.Add(id.Value);
            }
            return set.Count > 0 ? set : null;
        }

        private static object GetMember(object target, string name)
        {
            try
            {
                if (target == null) return null;
                var t = target.GetType();
                var p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (p != null) return p.GetValue(target);
                var f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (f != null) return f.GetValue(target);
            }
            catch { }
            return null;
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
    }

    internal class LobbySnapshot
    {
        public int[] banned { get; set; }
        public string[] heroes { get; set; }
    }
}
