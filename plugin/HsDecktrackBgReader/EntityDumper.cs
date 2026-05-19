using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HsDecktrackBgReader
{
    /// <summary>
    /// Spike-only dumper. Writes one JSON object per line into a timestamped file under
    /// %LOCALAPPDATA%\HsDecktrackBgReader\dump-yyyyMMdd-HHmmss.jsonl.
    /// Goal: find which tags/entities/view models expose tribe bans and offered heroes
    /// during BG hero selection.
    /// </summary>
    internal class EntityDumper : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new object();

        public EntityDumper(string dir)
        {
            var path = Path.Combine(dir, "dump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl");
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            _writer.AutoFlush = true;
        }

        public void Write(string kind, object payload)
        {
            lock (_lock)
            {
                _writer.WriteLine("{\"t\":\"" + DateTime.Now.ToString("HH:mm:ss.fff") + "\",\"kind\":\"" + kind + "\",\"data\":" + ToJson(payload) + "}");
            }
        }

        public void WriteEntitySnapshot(string trigger, object game)
        {
            var entitiesObj = SafeGet(game, "Entities");
            if (entitiesObj == null)
            {
                Write("entities_unavailable", new { trigger });
                return;
            }

            var rows = new List<object>();
            try
            {
                IEnumerable values = null;
                var valuesProp = entitiesObj.GetType().GetProperty("Values");
                if (valuesProp != null) values = valuesProp.GetValue(entitiesObj) as IEnumerable;
                if (values == null && entitiesObj is IEnumerable iEnum) values = iEnum;
                if (values == null) { Write("entities_unenumerable", new { trigger, type = entitiesObj.GetType().FullName }); return; }

                foreach (var entity in values)
                {
                    if (entity == null) continue;
                    var row = new Dictionary<string, object>();
                    row["id"]         = SafeGet(entity, "Id");
                    row["cardId"]     = SafeGet(entity, "CardId");
                    row["name"]       = SafeGet(SafeGet(entity, "Card"), "Name");
                    row["isPlayer"]   = SafeGet(entity, "IsPlayer");
                    row["isHero"]     = SafeGet(entity, "IsHero");
                    row["isMinion"]   = SafeGet(entity, "IsMinion");
                    row["isHeroPow"]  = SafeGet(entity, "IsHeroPower");
                    row["isInZone"]   = SafeGet(entity, "Zone");

                    // Dump full Tags dictionary if exposed
                    var tags = SafeGet(entity, "Tags");
                    if (tags is IDictionary tagDict)
                    {
                        var tagOut = new Dictionary<string, object>();
                        foreach (DictionaryEntry de in tagDict)
                            tagOut[de.Key?.ToString() ?? "?"] = de.Value;
                        row["tags"] = tagOut;
                    }
                    rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                Write("entities_enum_error", new { trigger, message = ex.Message });
            }

            Write("entities_snapshot", new { trigger, count = rows.Count, rows });
        }

        /// <summary>
        /// Walks the public+nonpublic properties of Core.Game looking for anything
        /// that smells like BG / lobby / mulligan / tribe / hero state.
        /// We do this with reflection because the surface isn't documented and
        /// will likely differ between HDT versions.
        /// </summary>
        public void WriteReflectionProbe(string trigger, object game)
        {
            if (game == null) { Write("game_null", new { trigger }); return; }
            var keywords = new[] { "battleground", "bg", "lobby", "mulligan", "tribe", "race", "hero", "pick", "trinket", "anomaly", "scenario" };
            var hits = new List<object>();
            try
            {
                var props = game.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in props)
                {
                    var nameLower = p.Name.ToLowerInvariant();
                    if (!keywords.Any(k => nameLower.Contains(k))) continue;
                    object val = null;
                    try { val = p.GetValue(game); } catch (Exception ex) { val = "<get_error:" + ex.Message + ">"; }
                    hits.Add(new { name = p.Name, type = p.PropertyType.FullName, value = SummarizeValue(val) });
                }
            }
            catch (Exception ex)
            {
                Write("reflection_probe_error", new { trigger, message = ex.Message });
            }
            Write("reflection_probe", new { trigger, hits });
        }

        /// <summary>
        /// Scans the HDT assembly for types named like BattlegroundsHeroPickingViewModel
        /// and probes their static / instance members. Spike-only, very chatty.
        /// </summary>
        public void WriteBgViewModelProbe(string trigger)
        {
            try
            {
                var asm = typeof(Hearthstone_Deck_Tracker.API.GameEvents).Assembly;
                var types = asm.GetTypes()
                    .Where(t => t.Name.IndexOf("Battleground", StringComparison.OrdinalIgnoreCase) >= 0
                             || t.Name.IndexOf("HeroPick",     StringComparison.OrdinalIgnoreCase) >= 0
                             || t.Name.IndexOf("Mulligan",     StringComparison.OrdinalIgnoreCase) >= 0
                             || t.Name.IndexOf("TrinketPick",  StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(40)
                    .ToList();

                var summary = types.Select(t => new
                {
                    fullName = t.FullName,
                    isAbstract = t.IsAbstract,
                    publicProps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                                   .Select(p => p.Name + ":" + p.PropertyType.Name).Take(30).ToArray(),
                    staticProps = t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                                   .Where(p => p.GetGetMethod(true)?.IsStatic == true)
                                   .Select(p => p.Name + ":" + p.PropertyType.Name).Take(30).ToArray(),
                }).ToList();
                Write("bg_viewmodel_types", new { trigger, types = summary });
            }
            catch (Exception ex)
            {
                Write("bg_viewmodel_probe_error", new { trigger, message = ex.Message });
            }
        }

        private static object SummarizeValue(object v)
        {
            if (v == null) return null;
            if (v is string s) return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum) return v.ToString();
            if (v is IEnumerable en && !(v is string))
            {
                var list = new List<object>();
                int i = 0;
                foreach (var item in en)
                {
                    if (i++ >= 25) { list.Add("…"); break; }
                    list.Add(item?.ToString());
                }
                return list;
            }
            return v.ToString();
        }

        private static object SafeGet(object target, string name)
        {
            try
            {
                if (target == null) return null;
                var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p.GetValue(target);
                var f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(target);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Minimal JSON serializer to avoid pulling in Newtonsoft (HDT ships one but the
        /// reference is fragile across versions). Supports primitives, strings, IEnumerable,
        /// IDictionary, and anonymous/plain objects via reflection.
        /// </summary>
        private static string ToJson(object value)
        {
            var sb = new StringBuilder();
            WriteJsonValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteJsonValue(StringBuilder sb, object value, int depth)
        {
            if (depth > 6) { sb.Append("\"<truncated>\""); return; }
            if (value == null) { sb.Append("null"); return; }
            switch (value)
            {
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: WriteJsonString(sb, s); return;
                case int _: case long _: case short _: case byte _: case sbyte _: case uint _: case ulong _: case ushort _:
                    sb.Append(value.ToString()); return;
                case float f: sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case Enum e: WriteJsonString(sb, e.ToString()); return;
            }

            if (value is IDictionary dict)
            {
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry de in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(sb, de.Key?.ToString() ?? "null");
                    sb.Append(':');
                    WriteJsonValue(sb, de.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            if (value is IEnumerable en)
            {
                sb.Append('[');
                var first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            // anonymous / plain object
            var type = value.GetType();
            if (type.Namespace != null && (type.IsPrimitive || type.IsEnum))
            {
                WriteJsonString(sb, value.ToString());
                return;
            }
            try
            {
                sb.Append('{');
                var first = true;
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v;
                    try { v = p.GetValue(value); }
                    catch { v = null; }
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(sb, p.Name);
                    sb.Append(':');
                    WriteJsonValue(sb, v, depth + 1);
                }
                sb.Append('}');
            }
            catch
            {
                WriteJsonString(sb, value.ToString());
            }
        }

        private static void WriteJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public void Dispose()
        {
            try { _writer?.Dispose(); } catch { }
        }
    }
}
