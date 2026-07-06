using System;
using System.IO;
using System.Text;
using BepInEx;

namespace OldenPedia
{
    /// <summary>
    /// Finds the runtime container(s) that hold the content configs.
    ///
    /// We now know the definition types (Hex.Configs.UnitLogicConfig, HeroConfig,
    /// FractionConfig, …) but they aren't UnityEngine.Objects, so we can't find
    /// instances via FindObjectsOfTypeAll. They're loaded into a provider/manager
    /// (probably by Hex.ResManager) keyed by SID. This scans every Hex.* type for
    /// a field/property whose type is a Dictionary/List/array of those configs —
    /// the declaring type is the catalog we read the pedia from.
    ///
    /// Uses Instance+Static+Public+NonPublic so a private/static catalog dict is
    /// still found.
    /// </summary>
    public static class ContainerProbe
    {
        private static readonly string[] Targets =
        {
            "UnitLogicConfig", "HeroConfig", "FractionConfig", "SkillConfig",
            "BuildingMainConfig", "BuildingConfigBase", "ArtifactCost",
        };

        public static void DumpConfigContainers()
        {
            try
            {
                var sb = new StringBuilder();
                int hits = 0;
                // Instance(4)|Static(8)|Public(16)|NonPublic(32) = 60
                var flags = (Il2CppSystem.Reflection.BindingFlags)60;

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
                        string owner;
                        try { owner = t.FullName; } catch { continue; }
                        if (string.IsNullOrEmpty(owner)) continue;

                        try
                        {
                            foreach (var f in t.GetFields(flags))
                            {
                                if (f == null) continue;
                                var tn = Safe(() => f.FieldType.FullName);
                                if (References(tn) && IsContainer(tn))
                                {
                                    sb.AppendLine($"{owner}\n   field {f.Name} : {tn}\n");
                                    hits++;
                                }
                            }
                            foreach (var p in t.GetProperties(flags))
                            {
                                if (p == null) continue;
                                var tn = Safe(() => p.PropertyType.FullName);
                                if (References(tn) && IsContainer(tn))
                                {
                                    sb.AppendLine($"{owner}\n   prop  {p.Name} : {tn}\n");
                                    hits++;
                                }
                            }
                        }
                        catch { }
                    }
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "config_containers.txt");
                File.WriteAllText(path,
                    $"# OldenPedia {Plugin.Version} config-container scan — {hits} hits — " +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    "# Members holding collections of content configs. The one on a\n" +
                    "# provider/manager/storage type (a Dictionary keyed by SID) is the\n" +
                    "# master catalog we read the pedia from.\n\n" + sb);

                Plugin.Log.LogInfo($"Config-container scan: {hits} hits written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ContainerProbe failed: {ex}");
            }
        }

        private static bool References(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            for (int i = 0; i < Targets.Length; i++)
                if (typeName.IndexOf(Targets[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool IsContainer(string typeName)
        {
            // Constructed generics' FullName contains the bracketed type args, and
            // arrays end in "[]"; a plain single-config field has neither.
            return typeName.IndexOf("Dictionary", StringComparison.Ordinal) >= 0
                || typeName.IndexOf("List", StringComparison.Ordinal) >= 0
                || typeName.IndexOf("[", StringComparison.Ordinal) >= 0;
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch { return ""; }
        }
    }
}
