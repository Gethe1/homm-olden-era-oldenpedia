using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace OldenPedia
{
    /// <summary>
    /// Dumps full detail (base type + fields + properties + methods, incl. static)
    /// for the types named in [Probe] InspectTypes (default cjw,cnn,cjv), then
    /// scans every Hex.* type for members whose type IS one of the targets — i.e.
    /// who holds the catalog statically. Between the two we learn how to obtain a
    /// live cjw instance and how to enumerate a cjv catalog.
    /// </summary>
    public static class TypeDetailProbe
    {
        // DeclaredOnly(2)|Instance(4)|Static(8)|Public(16)|NonPublic(32) = 62
        private static readonly Il2CppSystem.Reflection.BindingFlags Flags =
            (Il2CppSystem.Reflection.BindingFlags)62;

        public static void DumpTypeDetails()
        {
            try
            {
                var baseTargets = Plugin.InspectTypes;
                // Always also dump the unit/hero/artifact configs so we can locate
                // visual/asset references and exact field names for each category.
                string[] extra =
                {
                    "UnitViewConfig", "UnitLogicConfig", "HeroConfig", "FractionConfig",
                    "ItemConfig", "SkillConfig", "SkillLogicConfig", "SkillViewConfig",
                    "PerkConfig", "AbilityLogicConfig", "UnitPassiveConfig",
                    "BonusConfig", "HeroStat", "HeroStatsRollConfig", "SkillParameter",
                    "LocKit", "LocInfo", "LocData", "Localization"
                };
                var targets = new string[baseTargets.Length + extra.Length];
                Array.Copy(baseTargets, targets, baseTargets.Length);
                Array.Copy(extra, 0, targets, baseTargets.Length, extra.Length);
                var sb = new StringBuilder();
                var asms = Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies();

                // 1) Full detail of each target type.
                foreach (var asm in asms)
                {
                    if (!IsHex(asm)) continue;
                    foreach (var t in SafeGetTypes(asm))
                    {
                        if (t == null) continue;
                        string nm; try { nm = Strip(t.Name); } catch { continue; }
                        if (In(targets, nm)) DumpType(t, sb);
                    }
                }

                // 2) Holders: members whose type is one of the targets.
                sb.AppendLine("===== HOLDERS (members whose type is a target) =====");
                foreach (var asm in asms)
                {
                    if (!IsHex(asm)) continue;
                    foreach (var t in SafeGetTypes(asm))
                    {
                        if (t == null) continue;
                        string owner; try { owner = t.FullName; } catch { continue; }
                        try
                        {
                            foreach (var f in t.GetFields(Flags))
                            {
                                if (f == null) continue;
                                if (In(targets, Safe(() => Strip(f.FieldType.Name))))
                                    sb.AppendLine($"{owner}.{f.Name} : {Safe(() => f.FieldType.Name)} {(f.IsStatic ? "[static]" : "")}");
                            }
                            foreach (var p in t.GetProperties(Flags))
                            {
                                if (p == null) continue;
                                if (In(targets, Safe(() => Strip(p.PropertyType.Name))))
                                    sb.AppendLine($"{owner}.{p.Name} (prop) : {Safe(() => p.PropertyType.Name)}");
                            }
                        }
                        catch { }
                    }
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "type_detail.txt");
                File.WriteAllText(path,
                    $"# OldenPedia {Plugin.Version} type-detail — targets: {string.Join(",", targets)} — " +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" + sb);
                Plugin.Log.LogInfo($"Type detail written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"TypeDetailProbe failed: {ex}");
            }
        }

        private static void DumpType(Il2CppSystem.Type t, StringBuilder sb)
        {
            sb.AppendLine($"===== {Safe(() => t.FullName)} =====");
            sb.AppendLine($"  base: {Safe(() => t.BaseType != null ? t.BaseType.FullName : "-")}");
            try
            {
                foreach (var f in t.GetFields(Flags))
                    if (f != null)
                        sb.AppendLine($"  {(f.IsStatic ? "static " : "")}field {Safe(() => f.FieldType.Name)} {f.Name}");
            }
            catch { }
            try
            {
                foreach (var p in t.GetProperties(Flags))
                    if (p != null)
                        sb.AppendLine($"  prop  {Safe(() => p.PropertyType.Name)} {p.Name}");
            }
            catch { }
            try
            {
                foreach (var m in t.GetMethods(Flags))
                {
                    if (m == null) continue;
                    var ps = new List<string>();
                    try { foreach (var pi in m.GetParameters()) ps.Add(Safe(() => pi.ParameterType.Name)); }
                    catch { }
                    sb.AppendLine($"  {(m.IsStatic ? "static " : "")}method {Safe(() => m.ReturnType.Name)} {m.Name}({string.Join(", ", ps)})");
                }
            }
            catch { }
            sb.AppendLine();
        }

        private static IEnumerable<Il2CppSystem.Type> SafeGetTypes(Il2CppSystem.Reflection.Assembly asm)
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types;
            try { types = asm.GetTypes(); }
            catch { yield break; }
            foreach (var t in types) yield return t;
        }

        private static bool IsHex(Il2CppSystem.Reflection.Assembly asm)
        {
            try { var n = asm.GetName().Name; return !string.IsNullOrEmpty(n) && n.StartsWith("Hex"); }
            catch { return false; }
        }

        private static bool In(string[] targets, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < targets.Length; i++)
                if (string.Equals(targets[i], name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int bt = name.IndexOf('`');
            return bt >= 0 ? name.Substring(0, bt) : name;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "?"; } catch { return "?"; }
        }
    }
}
