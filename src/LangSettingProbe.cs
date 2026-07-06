using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OldenPedia
{
    // L: searches every type in the game's own assemblies (Hex.*, not generic
    // Unity mechanisms) for any field/property whose NAME contains "lang" or
    // "locale", and reads its value where possible (static members read
    // directly; instance members are reported by declaring type so a live
    // holder can be tracked down next, same as PerkConfig/BuffConfig earlier
    // in this project). Read-only, wrapped per-member so one bad reflection
    // call can't take down the whole scan.
    public static class LangSettingProbe
    {
        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# OldenPedia language-setting probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("# Scanning Hex.* types for fields/properties named like 'lang' or 'locale'.");
            sb.AppendLine();

            int scanned = 0, hits = 0;
            try
            {
                foreach (var t in DataExtractor.HexTypes())
                {
                    if (t == null) continue;
                    scanned++;
                    string typeName = DataExtractor.Safe(() => t.FullName);
                    if (string.IsNullOrEmpty(typeName)) continue;

                    try
                    {
                        foreach (var f in t.GetFields(DataExtractor.FR))
                        {
                            string nm = DataExtractor.Safe(() => f.Name);
                            if (!LooksLangy(nm)) continue;
                            hits++;
                            string ftype = DataExtractor.Safe(() => f.FieldType.Name);
                            string val = DataExtractor.Read(t, null, nm);
                            sb.AppendLine($"[field] {typeName}.{nm} : {ftype}  value(static-or-null-target)='{val}'");
                        }
                    }
                    catch { }

                    try
                    {
                        foreach (var p in t.GetProperties(DataExtractor.FR))
                        {
                            string nm = DataExtractor.Safe(() => p.Name);
                            if (!LooksLangy(nm)) continue;
                            hits++;
                            string ptype = DataExtractor.Safe(() => p.PropertyType.Name);
                            string val = DataExtractor.Read(t, null, nm);
                            sb.AppendLine($"[prop ] {typeName}.{nm} : {ptype}  value(static-or-null-target)='{val}'");
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { sb.AppendLine("scan error: " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine($"# scanned {scanned} Hex.* types, {hits} lang/locale-named members found.");
            sb.AppendLine("# A 'value' that resolved to a real folder name (english/german/...) or a short");
            sb.AppendLine("# code (en/de/...) is almost certainly the one; an error/empty value on a static");
            sb.AppendLine("# field usually means it just hasn't been initialized yet at probe time.");
            sb.AppendLine();

            // Targeted follow-up now that a strong candidate is known:
            // Hex.Settings.Data.SettingsData.language (a plain string). It's an
            // INSTANCE field, so find whatever holds a live SettingsData instance
            // (search every Hex.* type's static fields for one typed as
            // SettingsData — the same "find the registry" approach that located
            // the unit/hero/artifact catalog earlier in this project) and read
            // its "language" field directly. Also tries GameSettingsRequest.Locale.
            sb.AppendLine("--- targeted: live SettingsData / GameSettingsRequest instances ---");
            FindLiveInstanceField(sb, "Hex.Settings.Data.SettingsData", "language");
            FindLiveInstanceField(sb, "Hex.SharedLibrary.Services.GameSettingsRequest", "Locale");

            // Round 2: SettingsData turned out to be an INSTANCE field ("bric") on
            // several settings-UI section objects (BhGeneralSection etc.), not
            // held by any static registry. Those sections are UI components, so a
            // live instance may exist in the scene even if the settings screen
            // isn't currently open (Unity often keeps UI objects around inactive
            // rather than destroying them). Search for live instances directly.
            sb.AppendLine();
            sb.AppendLine("--- targeted: live settings-UI section objects (via Resources.FindObjectsOfTypeAll) ---");
            string[] sectionTypes =
            {
                "Hex.Settings.UI.BhGeneralSection", "Hex.Settings.UI.BhHotkeysSection",
                "Hex.Settings.UI.BhSettingsSection", "Hex.Settings.UI.BhSoundSection",
                "Hex.Settings.UI.BhVideoSection", "Hex.Settings.UI.BhVisibilitySection"
            };
            foreach (var stn in sectionTypes) FindLiveSectionInstance(sb, stn, "bric", "language");

            // These manage the WHOLE settings screen (not one tab) — worth
            // checking whether they get the canonical SettingsData as soon as the
            // Settings menu exists, even before any specific tab (like General)
            // has ever been shown. If so, this needs no tab visit and no method
            // call at all — strictly safer than anything involving Show().
            sb.AppendLine();
            sb.AppendLine("--- targeted: settings CONTROLLER/VIEW objects (may populate earlier than per-tab sections) ---");
            FindLiveSectionInstance(sb, "Hex.Settings.UI.SettingsSectionsController", "brjb", "language");
            FindLiveSectionInstance(sb, "Hex.Settings.UI.SettingsSectionsController", "brjc", "language");
            FindLiveSectionInstance(sb, "Hex.Settings.UI.SettingsSectionsView", "brje", "language");

            // Explicit experiment, done ONCE per probe run by user request: every
            // read-only avenue is exhausted, so this actually INVOKES
            // BhGeneralSection.Show(null) and reports what happens. Wrapped so a
            // thrown managed exception (the most likely outcome — Show() probably
            // dereferences the null argument) is caught and reported, not left to
            // crash anything. bric is re-read immediately after either way.
            sb.AppendLine();
            sb.AppendLine("--- EXPERIMENT: BhGeneralSection.Show(null) invocation ---");
            TryInvokeShowNull(sb);

            // New avenue: Unity's PlayerPrefs on Windows/Proton are stored in the
            // Wine prefix's registry files, NOT a plain settings file — which is
            // exactly why the earlier persistentDataPath text-file scan found
            // nothing. This reads those registry files directly (still pure file
            // I/O, no code execution) looking for any key mentioning language.
            sb.AppendLine();
            sb.AppendLine("--- targeted: Proton/Wine registry (PlayerPrefs storage) ---");
            SearchProtonRegistry(sb);

            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "lang_setting_probe.txt");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[langsetting] {hits} hits across {scanned} types written to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[langsetting] write: " + ex.Message); }
        }

        private static bool LooksLangy(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("lang") || n.Contains("locale");
        }

        // Unity's PlayerPrefs on Windows (and thus under Proton) are stored in
        // the registry, not a file our earlier text-file scan would have found.
        // First attempt: locate the Proton prefix relative to the game's own
        // install path. Confirmed to fail in this session — GameRootPath comes
        // back as a WINE VIRTUAL DRIVE PATH ("S:\..."), not a real filesystem
        // path, since from inside the game (running under Wine) we only see
        // whatever drive letters Wine maps, not the host directory structure
        // that contains "steamapps"/"compatdata" at all.
        // Second attempt: Wine's default configuration maps a special drive
        // (conventionally "Z:") directly to the real Linux filesystem root ("/").
        // If that convention holds here, we can reach the SAME registry file via
        // "Z:\home\<user>\.local\share\Steam\steamapps\compatdata\...\pfx\user.reg"
        // instead — still pure file I/O, no registry API, no code execution.
        private static void SearchProtonRegistry(StringBuilder sb)
        {
            try
            {
                string gameRoot = BepInEx.Paths.GameRootPath;
                sb.AppendLine($"GameRootPath = {gameRoot}");

                string steamapps = null;
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string dir = gameRoot;
                    for (int i = 0; i < 8 && dir != null; i++)
                    {
                        string candidate = Path.Combine(dir, "steamapps");
                        if (Directory.Exists(candidate)) { steamapps = candidate; break; }
                        if (Path.GetFileName(dir) == "steamapps") { steamapps = dir; break; }
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                if (steamapps != null) { sb.AppendLine($"steamapps (via GameRootPath) = {steamapps}"); ScanCompatData(sb, Path.Combine(steamapps, "compatdata")); }
                else sb.AppendLine("GameRootPath-relative search: no 'steamapps' folder found (expected under Wine — see Z: attempt below)");

                // Z: drive attempt.
                sb.AppendLine();
                sb.AppendLine("trying Wine's Z: -> / mapping...");
                string zHome = "Z:\\home";
                if (!Directory.Exists(zHome)) { sb.AppendLine($"'{zHome}' not accessible — Z: doesn't map to / in this Wine config, or a different drive letter is used"); return; }

                foreach (var userDir in Directory.GetDirectories(zHome))
                {
                    string compat = Path.Combine(userDir, ".local", "share", "Steam", "steamapps", "compatdata");
                    if (!Directory.Exists(compat)) continue;
                    sb.AppendLine($"found compatdata via Z: = {compat}");
                    ScanCompatData(sb, compat);
                }
            }
            catch (Exception ex) { sb.AppendLine("SearchProtonRegistry error: " + ex.Message); }
        }

        private static void ScanCompatData(StringBuilder sb, string compatdata)
        {
            if (!Directory.Exists(compatdata)) { sb.AppendLine($"'{compatdata}' does not exist"); return; }
            var appDirs = Directory.GetDirectories(compatdata);
            sb.AppendLine($"compatdata has {appDirs.Length} app prefix folder(s)");

            foreach (var appDir in appDirs)
            {
                string appId = Path.GetFileName(appDir);
                foreach (var regName in new[] { "user.reg", "system.reg" })
                {
                    string regPath = Path.Combine(appDir, "pfx", regName);
                    if (!File.Exists(regPath)) continue;

                    int hits = 0;
                    try
                    {
                        foreach (var line in File.ReadLines(regPath))
                        {
                            if (line.IndexOf("lang", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            hits++;
                            if (hits <= 15) sb.AppendLine($"  [{appId}/{regName}] {line.Trim()}");
                        }
                    }
                    catch (Exception ex) { sb.AppendLine($"  [{appId}/{regName}] read error: {ex.Message}"); continue; }

                    if (hits > 0) sb.AppendLine($"  [{appId}/{regName}] {hits} line(s) mentioning 'lang' total (showing up to 15 above)");
                }
            }
        }

        // The explicit, user-requested experiment: call Show(null) on the live
        // BhGeneralSection instance and report exactly what happens. Read-only
        // avenues are exhausted (neither the tab sections nor the controller/view
        // have a populated SettingsData before any tab is shown), so this is a
        // genuine step beyond "just reading" — the one thing we haven't tried.
        private static void TryInvokeShowNull(StringBuilder sb)
        {
            try
            {
                Il2CppSystem.Type sectionType = null;
                foreach (var t in DataExtractor.HexTypes())
                    if (DataExtractor.Safe(() => t?.FullName) == "Hex.Settings.UI.BhGeneralSection") { sectionType = t; break; }
                if (sectionType == null) { sb.AppendLine("BhGeneralSection type not found"); return; }

                var all = UnityEngine.Resources.FindObjectsOfTypeAll(sectionType);
                if (all == null || all.Length == 0) { sb.AppendLine("no live BhGeneralSection instance found"); return; }
                var inst = all[0];
                if (inst == null) { sb.AppendLine("instance[0] is null"); return; }

                string before = DataExtractor.Read(sectionType, inst, "bric");
                sb.AppendLine($"before: bric field-read = '{before}' (expected 'err' or similar — bric is a reference type, Read() isn't meant for it, just confirming it's still unset)");
                var dataObjBefore = DataExtractor.ReadObj(sectionType, inst, "bric");
                sb.AppendLine($"before: bric is {(dataObjBefore == null ? "null" : "NON-NULL (already populated?!)")}");

                var showMethod = sectionType.GetMethod("Show", DataExtractor.FR);
                if (showMethod == null) { sb.AppendLine("Show method not found via reflection"); return; }

                sb.AppendLine("invoking Show(null) now...");
                try
                {
                    var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1); // single null argument
                    showMethod.Invoke(inst, args);
                    sb.AppendLine("Show(null) returned without throwing.");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Show(null) threw (caught safely): " + ex.GetType().Name + ": " + ex.Message);
                    if (ex.InnerException != null) sb.AppendLine("  inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }

                var dataObjAfter = DataExtractor.ReadObj(sectionType, inst, "bric");
                if (dataObjAfter == null)
                {
                    sb.AppendLine("after: bric is still null — Show(null) did not populate it.");
                }
                else
                {
                    Il2CppSystem.Type dataType = null;
                    foreach (var t in DataExtractor.HexTypes())
                        if (DataExtractor.Safe(() => t?.FullName) == "Hex.Settings.Data.SettingsData") { dataType = t; break; }
                    string lang = dataType != null ? DataExtractor.Read(dataType, dataObjAfter, "language") : "(SettingsData type missing)";
                    sb.AppendLine($"after: bric is NON-NULL. bric.language = '{lang}'");
                }
            }
            catch (Exception ex) { sb.AppendLine("TryInvokeShowNull outer error: " + ex.Message); }
        }

        // Finds a live instance of "holderTypeFullName" by searching every Hex.*
        // type's static fields/properties for one whose declared type matches it
        // exactly (the same "find the registry" pattern used elsewhere in this
        // project — e.g. the unit/hero catalog was found this way), then reads
        // "targetFieldName" off that instance.
        private static void FindLiveInstanceField(StringBuilder sb, string holderTypeFullName, string targetFieldName)
        {
            int found = 0;
            try
            {
                foreach (var t in DataExtractor.HexTypes())
                {
                    if (t == null) continue;
                    string tn = DataExtractor.Safe(() => t.FullName);
                    if (string.IsNullOrEmpty(tn)) continue;

                    try
                    {
                        foreach (var f in t.GetFields(DataExtractor.FR))
                        {
                            string ftn = DataExtractor.Safe(() => f.FieldType.FullName);
                            if (ftn != holderTypeFullName) continue;
                            try
                            {
                                var inst = f.GetValue(null); // only succeeds if f is actually static
                                if (inst == null) { sb.AppendLine($"[holder] {tn}.{f.Name} : {holderTypeFullName} = null"); continue; }
                                found++;
                                string val = DataExtractor.Read(t, inst, targetFieldName);
                                // Read() expects (declaring type of the field we want, instance) — but here
                                // "inst" IS the holder instance, and targetFieldName lives on holderType, so
                                // re-resolve against the holder's own type for a correct read.
                                Il2CppSystem.Type holderType = null;
                                foreach (var ht in DataExtractor.HexTypes())
                                    if (DataExtractor.Safe(() => ht?.FullName) == holderTypeFullName) { holderType = ht; break; }
                                if (holderType != null) val = DataExtractor.Read(holderType, inst, targetFieldName);
                                sb.AppendLine($"[holder] {tn}.{f.Name} : {holderTypeFullName}  ->  {targetFieldName}='{val}'");
                            }
                            catch (Exception ex) { sb.AppendLine($"[holder-try] {tn}.{f.Name} : {ex.Message}"); }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { sb.AppendLine($"FindLiveInstanceField({holderTypeFullName}) error: " + ex.Message); }

            if (found == 0) sb.AppendLine($"(no static field of type {holderTypeFullName} found anywhere in Hex.*)");
        }

        // Finds live instances of "sectionTypeFullName" via Resources.FindObjectsOfTypeAll
        // — passing the Il2CppSystem.Type we already have from reflection directly
        // (no compile-time reference to the game's obfuscated type needed). For
        // each instance found, reads "dataFieldName" (expected to hold a
        // SettingsData reference) then "finalFieldName" off THAT object.
        private static void FindLiveSectionInstance(StringBuilder sb, string sectionTypeFullName, string dataFieldName, string finalFieldName)
        {
            try
            {
                Il2CppSystem.Type sectionType = null;
                foreach (var t in DataExtractor.HexTypes())
                    if (DataExtractor.Safe(() => t?.FullName) == sectionTypeFullName) { sectionType = t; break; }
                if (sectionType == null) { sb.AppendLine($"{sectionTypeFullName}: type not found"); return; }

                var all = UnityEngine.Resources.FindObjectsOfTypeAll(sectionType);
                int n = all != null ? all.Length : 0;
                sb.AppendLine($"{sectionTypeFullName}: {n} live instance(s) found");
                if (n == 0) return;

                for (int i = 0; i < n; i++)
                {
                    var inst = all[i];
                    if (inst == null) continue;
                    var dataObj = DataExtractor.ReadObj(sectionType, inst, dataFieldName);

                    // Diagnostics needed to know whether SetActive-toggling could
                    // ever work here: is the object (and its ancestors) already
                    // active? If activeInHierarchy is already true and bric is
                    // still null, toggling won't help — something else (a method
                    // call from a controller, likely) is what actually populates it.
                    var comp = inst.TryCast<UnityEngine.Component>();
                    if (comp != null)
                    {
                        var go = comp.gameObject;
                        sb.AppendLine($"  [{i}] gameObject.activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy}");
                        var parent = comp.transform.parent;
                        int depth = 0;
                        while (parent != null && depth < 5)
                        {
                            sb.AppendLine($"      parent[{depth}] '{parent.gameObject.name}' activeSelf={parent.gameObject.activeSelf}");
                            parent = parent.parent; depth++;
                        }
                    }

                    if (dataObj == null) { sb.AppendLine($"  [{i}] {dataFieldName} is null"); continue; }

                    Il2CppSystem.Type dataType = null;
                    foreach (var t in DataExtractor.HexTypes())
                        if (DataExtractor.Safe(() => t?.FullName) == "Hex.Settings.Data.SettingsData") { dataType = t; break; }
                    string val = dataType != null ? DataExtractor.Read(dataType, dataObj, finalFieldName) : "(SettingsData type missing)";
                    sb.AppendLine($"  [{i}] {dataFieldName}.{finalFieldName} = '{val}'");
                }

                // Read-only method-name scan (no invocation) — candidates for
                // whatever actually populates bric, in case SetActive can't do it.
                sb.AppendLine($"  -- {sectionTypeFullName} method names containing init/bind/show/setup/refresh/select/open --");
                try
                {
                    foreach (var m in sectionType.GetMethods(DataExtractor.FR))
                    {
                        string mn = DataExtractor.Safe(() => m.Name);
                        string mnl = mn.ToLowerInvariant();
                        if (mnl.Contains("init") || mnl.Contains("bind") || mnl.Contains("show") ||
                            mnl.Contains("setup") || mnl.Contains("refresh") || mnl.Contains("select") || mnl.Contains("open"))
                        {
                            var ptypes = new List<string>();
                            try
                            {
                                foreach (var p in m.GetParameters())
                                    ptypes.Add(DataExtractor.Safe(() => p.ParameterType.FullName));
                            }
                            catch { }
                            string rt = DataExtractor.Safe(() => m.ReturnType.Name);
                            sb.AppendLine($"     {rt} {mn}({string.Join(", ", ptypes)})");
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine("     method scan error: " + ex.Message); }
            }
            catch (Exception ex) { sb.AppendLine($"{sectionTypeFullName}: error " + ex.Message); }
        }
    }
}
