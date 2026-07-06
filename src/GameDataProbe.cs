using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OldenPedia
{
    public readonly struct CensusEntry
    {
        public readonly string TypeName;
        public readonly int Count;
        public CensusEntry(string typeName, int count)
        {
            TypeName = typeName;
            Count = count;
        }
    }

    public static class GameDataProbe
    {
        public static List<CensusEntry> LastCensus = new List<CensusEntry>();

        /// <summary>
        /// Censuses every loaded ScriptableObject, grouped by its true IL2CPP
        /// runtime class name, sorted by count. In HoMM-style games the unit,
        /// spell, hero, building and faction definitions are almost always
        /// ScriptableObjects, so the high-count rows here ARE your databases.
        ///
        /// Because the community BepInEx pack applies a deobfuscation regex,
        /// the names you see are stable signature-based names rather than the
        /// per-patch 2–4 letter garbage. Note the ones that look like data
        /// (counts in the dozens/hundreds), then reference Hex.dll and read
        /// their fields in step 3.
        /// </summary>
        public static void DumpScriptableObjectCensus()
        {
            try
            {
                var soType = Il2CppType.Of<ScriptableObject>();

                // FindObjectsOfTypeAll returns assets that aren't on an active
                // scene object too — important, since data assets usually are.
                var all = Resources.FindObjectsOfTypeAll(soType);

                var counts = new Dictionary<string, int>();
                int scanned = 0;

                foreach (var o in all)
                {
                    if (o == null) continue;
                    scanned++;

                    // Real runtime type read from the native pointer — GetType()
                    // returns the managed wrapper ("UnityEngine.Object") here.
                    string name = Il2CppUtil.RuntimeTypeName(o);

                    counts.TryGetValue(name, out var c);
                    counts[name] = c + 1;
                }

                LastCensus = counts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => new CensusEntry(kv.Key, kv.Value))
                    .ToList();

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "census.txt");

                var lines = new List<string>
                {
                    $"# OldenPedia {Plugin.Version} census — {scanned} ScriptableObjects, " +
                    $"{LastCensus.Count} distinct types — {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                };
                lines.AddRange(LastCensus.Select(e => $"{e.Count}\t{e.TypeName}"));
                File.WriteAllLines(path, lines);

                Plugin.Log.LogInfo(
                    $"Census: {LastCensus.Count} distinct types across {scanned} ScriptableObjects. " +
                    $"Written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Census failed: {ex}");
            }
        }

        // ---------------------------------------------------------------------
        // STEP 3 SKETCH — reading a real database once you've identified it.
        //
        // After the census tells you e.g. "Hex.Data.UnitDefinition" has 180
        // instances, uncomment the Hex.dll reference in the .csproj, then:
        //
        //   var defs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UnitDefinition>());
        //   foreach (var d in defs.Cast<UnitDefinition>())
        //   {
        //       string id    = d.name;              // Unity asset name
        //       int    atk   = d.Attack;            // real fields off Hex.dll
        //       string locKey = d.DisplayNameKey;   // feed into the loc system
        //       // … collect into your own PediaEntry model and render it.
        //   }
        //
        // For display names/descriptions, find the localization manager the
        // same way (it'll show in the census or in UnityExplorer) and resolve
        // the loc keys rather than reading raw strings.
        // ---------------------------------------------------------------------
    }
}
