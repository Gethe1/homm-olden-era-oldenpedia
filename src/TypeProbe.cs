using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace OldenPedia
{
    /// <summary>
    /// Scans the Hex.* assemblies for TYPE DEFINITIONS whose names look like game
    /// content (unit / creature / spell / hero / fraction / …).
    ///
    /// The SO census proved the unit/spell/hero stats are NOT ScriptableObjects —
    /// they're a catalog of plain objects loaded by Hex.ResManager, keyed by the
    /// string SIDs seen in the network DTOs. Plain objects don't show up in
    /// FindObjectsOfTypeAll, but their CLASS definitions always exist in the
    /// loaded assemblies, so this name-scan finds them regardless of game state.
    ///
    /// Hex.* type names are largely un-obfuscated on this build, so a keyword
    /// match on the full type name is enough to surface the catalog classes.
    /// </summary>
    public static class TypeProbe
    {
        private static readonly string[] Keywords =
        {
            "unit", "creature", "pawn", "troop", "stack",
            "spell", "hero", "fraction", "faction",
            "building", "artifact", "skill", "perk",
            "catalog", "database", "registry", "roster", "gamedata",
        };

        public static void DumpContentTypes()
        {
            try
            {
                var sb = new StringBuilder();
                int matched = 0;

                var asms = Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in asms)
                {
                    string aname;
                    try { aname = asm.GetName().Name; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(aname) || !aname.StartsWith("Hex")) continue;

                    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type> types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        string full;
                        try { full = t.FullName; } catch { continue; }
                        if (string.IsNullOrEmpty(full) || !MatchesKeyword(full)) continue;

                        matched++;
                        DumpType(t, full, sb);
                    }
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "content_types.txt");
                File.WriteAllText(path,
                    $"# OldenPedia {Plugin.Version} content-type scan — {matched} matches — " +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    "# Hex.* type definitions whose name matches a content keyword.\n" +
                    "# Look for one with combat-stat members (attack/defence/hp/damage/speed).\n\n" +
                    sb);

                Plugin.Log.LogInfo($"Content-type scan: {matched} types written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"TypeProbe failed: {ex}");
            }
        }

        private static bool MatchesKeyword(string full)
        {
            for (int i = 0; i < Keywords.Length; i++)
                if (full.IndexOf(Keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static void DumpType(Il2CppSystem.Type t, string full, StringBuilder sb)
        {
            sb.AppendLine($"== {full} ==");
            try
            {
                foreach (var p in t.GetProperties())
                {
                    if (p == null) continue;
                    sb.AppendLine($"   prop  {Safe(() => p.PropertyType.Name)} {p.Name}");
                }
                foreach (var f in t.GetFields())
                {
                    if (f == null) continue;
                    sb.AppendLine($"   field {Safe(() => f.FieldType.Name)} {f.Name}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"   <member enumeration failed: {ex.Message}>");
            }
            sb.AppendLine();
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "?"; } catch { return "?"; }
        }
    }
}
