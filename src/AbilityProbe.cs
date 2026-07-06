using System;
using System.Collections.Generic;
using System.Text;

namespace OldenPedia
{
    // F2: exploratory probe (not wired into the live UI) for turning a unit's
    // abilities[]/passives[] into displayable names. Units reference abilities via
    // an AbilityID struct rather than a string id, so this dumps:
    //   1) every field/property of AbilityLogicConfig, UnitPassiveConfig, and
    //      whatever type their id-like member turns out to be (so we can see the
    //      AbilityID struct's own shape),
    //   2) a handful of live units' actual abilities[]/passives[] arrays, printing
    //      each element's fields, to look for a stable value (an enum name, a
    //      string, or a small int we can map) that turns into a loc key like
    //      "base_passive_<x>_name".
    // Read-only field/property reads only, all guarded — this is a manual,
    // developer-triggered probe, not part of the always-on render path.
    public static class AbilityProbe
    {
        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# OldenPedia ability probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try
            {
                DumpConfigShape(sb, "Hex.Configs.AbilityLogicConfig");
                sb.AppendLine();
                DumpConfigShape(sb, "Hex.Configs.UnitPassiveConfig");
                sb.AppendLine();
                DumpConfigShape(sb, "Hex.Configs.BuffData");
                sb.AppendLine();
                DumpConfigShape(sb, "Hex.Configs.BuffConfig");
                sb.AppendLine();
                DumpLiveUnitAbilities(sb);
            }
            catch (Exception ex) { sb.AppendLine("probe error: " + ex.Message); }

            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "OldenPedia");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "ability_probe.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[ability] probe written to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[ability] write: " + ex.Message); }
        }

        private static void DumpConfigShape(StringBuilder sb, string fullName)
        {
            sb.AppendLine($"=== {fullName} ===");
            Il2CppSystem.Type type = null;
            try { type = DataExtractor.FindType(fullName); } catch { }
            if (type == null) { sb.AppendLine("  (type not found)"); return; }

            try
            {
                foreach (var f in type.GetFields(DataExtractor.FR))
                {
                    string tn = DataExtractor.Safe(() => f.FieldType.Name);
                    string fulln = DataExtractor.Safe(() => f.FieldType.FullName);
                    sb.AppendLine($"  field {tn} {f.Name}   ({fulln})");
                }
            }
            catch (Exception ex) { sb.AppendLine("  fields error: " + ex.Message); }

            try
            {
                foreach (var p in type.GetProperties(DataExtractor.FR))
                {
                    string tn = DataExtractor.Safe(() => p.PropertyType.Name);
                    string fulln = DataExtractor.Safe(() => p.PropertyType.FullName);
                    sb.AppendLine($"  prop  {tn} {p.Name}   ({fulln})");
                }
            }
            catch (Exception ex) { sb.AppendLine("  props error: " + ex.Message); }
        }

        // Reads every field/prop of an arbitrary object and stringifies what it
        // safely can. For fields/props whose declared type is itself a
        // Hex.Configs.* type (e.g. BuffData, BuffConfig), recurses one extra level
        // so we can see THEIR fields too (id-like values often live one level
        // deeper than the object we started from) — capped at depth 1 to keep
        // output bounded and avoid runaway recursion into unrelated graphs.
        private static string DumpObjectShallow(Il2CppSystem.Type type, Il2CppSystem.Object obj, int depth = 0)
        {
            if (type == null || obj == null) return "(null)";
            var parts = new List<string>();
            try
            {
                foreach (var f in type.GetFields(DataExtractor.FR))
                {
                    string val = TryStringify(f.FieldType, () => f.GetValue(obj), depth);
                    parts.Add($"{f.Name}={val}");
                }
            }
            catch { }
            try
            {
                foreach (var p in type.GetProperties(DataExtractor.FR))
                {
                    string val = TryStringify(p.PropertyType, () => p.GetValue(obj), depth);
                    parts.Add($"{p.Name}={val}");
                }
            }
            catch { }
            return "{ " + string.Join(", ", parts.ToArray()) + " }";
        }

        private static string TryStringify(Il2CppSystem.Type valType, Func<Il2CppSystem.Object> getter, int depth = 0)
        {
            try
            {
                var v = getter();
                if (v == null) return "null";
                string tn = DataExtractor.Safe(() => valType.Name);
                string fulln = DataExtractor.Safe(() => valType.FullName);
                switch (tn)
                {
                    case "Int32": return v.Unbox<int>().ToString();
                    case "UInt32": return v.Unbox<uint>().ToString();
                    case "Int16": return v.Unbox<short>().ToString();
                    case "Int64": return v.Unbox<long>().ToString();
                    case "Single": return v.Unbox<float>().ToString();
                    case "Boolean": return v.Unbox<bool>().ToString();
                    case "String": return v.ToString();
                    default:
                        // One extra level into nested Hex.Configs.* objects (e.g.
                        // UnitPassiveConfig.data -> BuffData, AbilityLogicConfig's
                        // cnfo/cnfp/cnfq -> BuffConfig) since the useful id/name
                        // fields often live there, not on the outer object.
                        if (depth < 1 && !string.IsNullOrEmpty(fulln) && fulln.StartsWith("Hex.Configs.", StringComparison.Ordinal))
                            return DumpObjectShallow(valType, v, depth + 1);
                        try { return "enum?" + v.Unbox<int>(); } catch { }
                        try { return v.ToString(); } catch { return "(" + tn + ")"; }
                }
            }
            catch (Exception ex) { return "(err:" + FirstLine(ex.Message) + ")"; }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf('\n');
            return i >= 0 ? s.Substring(0, i) : s;
        }

        // For the first few units, read their abilities[]/passives[] arrays and
        // dump each element's full shape, so we can see how an AbilityID (or
        // whatever the array element type is) actually looks on a live instance.
        private static void DumpLiveUnitAbilities(StringBuilder sb)
        {
            sb.AppendLine("=== live unit abilities/passives (first 5 units with any) ===");
            var idx = DataExtractor.BuildIdIndex("Hex.Configs.UnitLogicConfig", out var unitType);
            if (unitType == null || idx.Count == 0) { sb.AppendLine("  (no units found)"); return; }

            int shown = 0;
            foreach (var kv in idx)
            {
                if (shown >= 5) break;
                string id = kv.Key;
                var uobj = kv.Value;

                var abilities = DataExtractor.ReadObjArray(unitType, uobj, "abilities", out var abilityType);
                var passives = DataExtractor.ReadObjArray(unitType, uobj, "passives", out var passiveType);
                if (abilities.Count == 0 && passives.Count == 0) continue;
                shown++;

                sb.AppendLine($"-- {id} -- abilities={abilities.Count} passives={passives.Count}");
                for (int i = 0; i < abilities.Count && i < 4; i++)
                    sb.AppendLine($"   ability[{i}]: {DumpObjectShallow(abilityType, abilities[i])}");
                for (int i = 0; i < passives.Count && i < 4; i++)
                    sb.AppendLine($"   passive[{i}]: {DumpObjectShallow(passiveType, passives[i])}");
            }
            if (shown == 0) sb.AppendLine("  (no units with non-empty abilities/passives found in first pass)");
        }
    }
}
