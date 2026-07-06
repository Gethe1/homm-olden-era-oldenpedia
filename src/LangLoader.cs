using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;
using TMPro;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OldenPedia
{
    /// <summary>
    /// Reads the game's own translations from StreamingAssets/Core.zip (the
    /// "Lang" directory) and builds a key->text dictionary. This is the game's
    /// real localization data, read straight from disk — no reflection, and it
    /// follows whatever language file the player has. Format is auto-detected
    /// (JSON / CSV / key=value / PO). Everything is defensive: any failure just
    /// leaves the table empty and callers fall back to humanized ids.
    /// </summary>
    public static class LangLoader
    {
        private static bool _loaded;
        private static string _lastSource = "none";
        private static readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static int Count => _map.Count;

        public static bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            EnsureLoaded();
            return _map.TryGetValue(key, out value) && !string.IsNullOrEmpty(value);
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try { Load(null); }
            catch (Exception ex) { Plugin.Log.LogError("[lang] load: " + ex.Message); }
        }

        // The game's own live language setting (Hex.Settings.UI.BhGeneralSection
        // .bric.language) is only populated once the player has visited the
        // in-game Settings > General screen — which may happen well after the
        // pedia is first opened and already fell back to system language. Call
        // this each time the pedia's data is (re)built; if a manual override
        // isn't set and we haven't already succeeded via the game's own setting,
        // it retries cheaply and reloads if it now succeeds. Returns true if a
        // reload happened (caller should re-extract everything so the new
        // language's text actually gets used).
        public static bool TryUpgradeToGameSettings()
        {
            string cfg = (Plugin.LangCode ?? "").Trim().ToLowerInvariant();
            if (cfg.Length > 0 && cfg != "auto") return false; // manual override — never silently replaced
            if (_lastSource == "GameSettings") return false; // already have the best source

            string gset = TryGameSettingsLanguage();
            if (string.IsNullOrEmpty(gset)) return false;

            _loaded = false;
            _map.Clear();
            try { Load(null); } catch (Exception ex) { Plugin.Log.LogError("[lang] reload: " + ex.Message); }
            _loaded = true;
            return _lastSource == "GameSettings";
        }

        private static string ZipPath()
        {
            try
            {
                var sa = UnityEngine.Application.streamingAssetsPath;
                var p = Path.Combine(sa, "Core.zip");
                if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        // Load language entries; if 'log' is non-null, append diagnostics there.
        private static void Load(StringBuilder log)
        {
            var zip = ZipPath();
            if (zip == null) { Plugin.Log.LogInfo("[lang] Core.zip not found"); log?.AppendLine("Core.zip not found"); return; }
            log?.AppendLine("zip = " + zip);

            using (var fs = File.OpenRead(zip))
            using (var arch = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // gather Lang/ entries
                var langEntries = new List<ZipArchiveEntry>();
                foreach (var e in arch.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // directory
                    var full = e.FullName.Replace('\\', '/');
                    if (full.IndexOf("Lang/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        full.IndexOf("/Lang", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        full.StartsWith("Lang", StringComparison.OrdinalIgnoreCase))
                        langEntries.Add(e);
                }
                log?.AppendLine($"Lang entries: {langEntries.Count}");
                if (log != null)
                    foreach (var e in langEntries)
                        log.AppendLine($"   {e.FullName} ({e.Length} bytes)");

                // distinct language folder names actually present (segment after "Lang/")
                var folders = new List<string>();
                foreach (var e in langEntries)
                {
                    string fld = FolderOf(e.FullName);
                    if (!string.IsNullOrEmpty(fld) && !folders.Contains(fld)) folders.Add(fld);
                }

                string want = (Plugin.LangCode ?? "").Trim().ToLowerInvariant();
                string source = "config";
                if (want.Length == 0 || want == "auto")
                {
                    string detected = DetectLanguage(folders, out source);
                    if (!string.IsNullOrEmpty(detected)) want = detected;
                    Plugin.Log.LogInfo($"[lang] auto-detect via {source}: '{detected}'");
                }
                string folder = ResolveFolder(want, folders);
                _lastSource = source;
                Plugin.Log.LogInfo($"[lang] config='{Plugin.LangCode}' source={source} want='{want}' -> folder='{folder}'; available=[{string.Join(", ", folders.ToArray())}]");
                log?.AppendLine($"config='{Plugin.LangCode}' source={source} want='{want}' resolved folder='{folder}'; available=[{string.Join(", ", folders.ToArray())}]");

                var chosen = new List<ZipArchiveEntry>();
                foreach (var e in langEntries)
                    if (string.Equals(FolderOf(e.FullName), folder, StringComparison.OrdinalIgnoreCase))
                        chosen.Add(e);
                log?.AppendLine($"chosen for folder '{folder}': {chosen.Count}");

                foreach (var e in chosen)
                {
                    string text;
                    try
                    {
                        using (var s = e.Open())
                        using (var r = new StreamReader(s, Encoding.UTF8, true))
                            text = r.ReadToEnd();
                    }
                    catch { continue; }

                    if (log != null)
                    {
                        log.AppendLine($"--- preview {e.FullName} ---");
                        var lines = text.Split('\n');
                        for (int i = 0; i < lines.Length && i < 20; i++)
                            log.AppendLine("   " + lines[i].TrimEnd('\r'));
                    }

                    int before = _map.Count;
                    ParseInto(e.FullName, text, _map);
                    log?.AppendLine($"   parsed +{_map.Count - before} (total {_map.Count})");
                }
            }

            Plugin.Log.LogInfo($"[lang] loaded {_map.Count} entries from Core.zip");
        }

        // Best-effort: figure out the language the GAME is currently set to.
        // Tries PlayerPrefs (several likely keys), the game's settings files, then OS language.
        // Returns a value that ResolveFolder can map, or null. Logs everything it sees.
        private static string DetectLanguage(List<string> folders, out string source)
        {
            source = "none";

            // 0) The game's OWN settings object (Hex.Settings.Data.SettingsData),
            // reached via a live settings-UI section instance's "bric" field —
            // confirmed working via probe (BhGeneralSection.bric.language). This
            // reflects whatever you've actually selected THIS session, which can
            // be more current than other sources — checked first so a live value
            // always wins.
            string gset = TryGameSettingsLanguage();
            if (!string.IsNullOrEmpty(gset) && ResolvesTo(gset, folders))
            { source = "GameSettings"; return gset.Trim().ToLowerInvariant(); }

            // 0a) Read the ACTUAL text currently on screen rather than any stored
            // setting: build a reverse-lookup of every language's translation for
            // a stable, short reference key ("button_exit" -> "Exit" in English),
            // then scan every currently-loaded UI text for an exact match. Since
            // UI text is set once when a screen is built and often stays in
            // memory even while hidden, this doesn't strictly require any
            // specific screen to be open right now — just for it to have loaded
            // at some point this session.
            string uiText = TryUiTextLanguage(folders);
            if (!string.IsNullOrEmpty(uiText) && ResolvesTo(uiText, folders))
            { source = "UiText"; return uiText.Trim().ToLowerInvariant(); }

            // 0b) The game's PlayerPrefs, read directly from the Wine/Proton
            // registry file. DISABLED (kept only as documentation): confirmed via
            // real-world test to be UNRELIABLE for this game — it read
            // "language"="english" while the game was actually running Polish,
            // and the same "english" value showed up identically across every
            // OTHER game's Wine prefix on the same machine. That means this key
            // is a Unity default written once and never updated when the in-game
            // dropdown changes — not a live setting. A stale-but-confident wrong
            // answer is worse than falling through, so this is not called.
            // string preg = TryProtonRegistryLanguage();
            // if (!string.IsNullOrEmpty(preg) && ResolvesTo(preg, folders))
            // { source = "ProtonRegistry"; return preg.Trim().ToLowerInvariant(); }

            // 0c) Unity's own Localization system, if the game uses it (this game
            // doesn't, per probe — kept as a harmless fallback for safety).
            string uloc = TryUnityLocalization();
            if (!string.IsNullOrEmpty(uloc) && ResolvesTo(uloc, folders))
            { source = "UnityLocalization"; return uloc.Trim().ToLowerInvariant(); }

            // 1) PlayerPrefs string keys the game might use
            string[] keys = {
                "Language", "language", "Lang", "lang", "Locale", "locale",
                "CurrentLanguage", "currentLanguage", "SelectedLanguage", "selectedLanguage",
                "game_language", "GameLanguage", "UILanguage", "ui_language", "settings_language"
            };
            foreach (var k in keys)
            {
                string v = null;
                try { v = UnityEngine.PlayerPrefs.GetString(k, ""); } catch { }
                if (!string.IsNullOrEmpty(v))
                {
                    Plugin.Log.LogInfo($"[lang] PlayerPrefs['{k}']='{v}'");
                    if (ResolvesTo(v, folders)) { source = $"PlayerPrefs:{k}"; return v.Trim().ToLowerInvariant(); }
                }
            }

            // 2) scan the game's persistent settings for a language token
            try
            {
                string baseDir = UnityEngine.Application.persistentDataPath;
                if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                {
                    foreach (var path in Directory.GetFiles(baseDir))
                    {
                        var lower = path.ToLowerInvariant();
                        if (!(lower.EndsWith(".json") || lower.EndsWith(".cfg") || lower.EndsWith(".ini") ||
                              lower.EndsWith(".txt") || lower.Contains("setting") || lower.Contains("pref"))) continue;
                        string text;
                        try { text = File.ReadAllText(path); } catch { continue; }
                        if (text.Length > 200000) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(
                            text, "(?i)\"?(?:language|locale)\"?\\s*[:=]\\s*\"?([A-Za-z\\-]{2,20})\"?");
                        if (m.Success)
                        {
                            string v = m.Groups[1].Value;
                            Plugin.Log.LogInfo($"[lang] settings file '{Path.GetFileName(path)}' language='{v}'");
                            if (ResolvesTo(v, folders)) { source = "settingsFile"; return v.Trim().ToLowerInvariant(); }
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogInfo("[lang] settings scan: " + ex.Message); }

            // 3) OS / system language as a last resort
            try
            {
                var sys = UnityEngine.Application.systemLanguage.ToString();
                Plugin.Log.LogInfo($"[lang] systemLanguage={sys}");
                string mapped = SystemLangToFolder(sys);
                if (mapped != null && folders.Contains(mapped)) { source = "systemLanguage"; return mapped; }
            }
            catch { }

            return null;
        }

        // Best-effort: if the game uses Unity's own Localization package, its
        // currently active locale is the single most authoritative signal of what
        // language the game is actually showing right now. Pure reflection (no
        // compile-time reference needed), read-only, and heavily logged so any
        // failure point is visible rather than silently returning nothing.
        // Confirmed working via the L-probe: the game's own SettingsData object
        // (holding the live "language" string) isn't behind any static registry —
        // it's an instance field ("bric") on several settings-UI section objects,
        // populated once the settings screen has been visited this session (some
        // instances may still have it null; try several, first hit wins).
        // Confirmed working via probe: this game's PlayerPrefs are stored in the
        // Wine/Proton registry (Windows PlayerPrefs storage), under the exact
        // Steam App ID's prefix, as a plain line: "language"="english". Locating
        // the prefix takes two attempts, since from inside the game (running
        // under Wine) we only see Wine's own virtual drive letters, not the real
        // Linux path — GameRootPath itself (e.g. "S:\...") doesn't lead anywhere
        // useful, so we also try Wine's conventional "Z: -> /" mapping.
        private static readonly Regex RxLanguageLine = new Regex("^\"language\"\\s*=\\s*\"([^\"]*)\"$", RegexOptions.Compiled);
        private const string KnownAppId = "3105440"; // Heroes of Might and Magic: Olden Era (confirmed)

        // Sidesteps the whole "where is the setting stored" problem: instead of
        // reading a setting value (which can be stale, per the Proton registry
        // dead-end above), this reads what's ACTUALLY DISPLAYED. Uses SEVERAL
        // independent reference keys (mixing short and long phrases) rather than
        // just one — each key gets its own reverse lookup and casts its own
        // "vote" for a folder. This is the redundancy against future language
        // additions: if two languages ever end up with an identical translation
        // for ONE key (increasingly possible as more languages are added), that
        // key simply can't discriminate between them, but the OTHER keys still
        // can, and the folder with the most matching votes wins. Every vote and
        // any collision is logged, so an actual ambiguity is visible rather than
        // silently wrong.
        private static readonly string[] UiRefKeys =
        {
            "button_exit", "button_start_game", "button_cancel", "button_apply",
            "navigationPanel_matchmaking", "lobby_find_placeholder", "fractions", "tab_players_num"
        };

        private static string TryUiTextLanguage(List<string> folders)
        {
            try
            {
                var zip = ZipPath();
                if (zip == null) { Plugin.Log.LogInfo("[lang] UiText: Core.zip not found"); return null; }

                // Per-key reverse lookup (translatedText -> folder), kept SEPARATE
                // per key rather than merged into one shared map, so a collision on
                // one key doesn't affect any other key's ability to discriminate.
                var perKeyReverse = new Dictionary<string, Dictionary<string, string>>();
                foreach (var k in UiRefKeys) perKeyReverse[k] = new Dictionary<string, string>(StringComparer.Ordinal);

                using (var fs = File.OpenRead(zip))
                using (var arch = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var e in arch.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        var full = e.FullName.Replace('\\', '/');
                        if (full.IndexOf("Lang/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!full.EndsWith("menu.json", StringComparison.OrdinalIgnoreCase)) continue;
                        string folder = FolderOf(full);
                        if (string.IsNullOrEmpty(folder)) continue;

                        string text;
                        try { using (var s = e.Open()) using (var r = new StreamReader(s, Encoding.UTF8, true)) text = r.ReadToEnd(); }
                        catch { continue; }

                        var tmp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ParseSidText(text, tmp);
                        foreach (var key in UiRefKeys)
                        {
                            if (!tmp.TryGetValue(key, out var val) || string.IsNullOrEmpty(val)) continue;
                            var m = perKeyReverse[key];
                            if (!m.ContainsKey(val)) m[val] = folder;
                            else if (m[val] != folder)
                                Plugin.Log.LogInfo($"[lang] UiText: key '{key}' collides ('{val}' matches both '{m[val]}' and '{folder}') — relying on other keys to disambiguate");
                        }
                    }
                }

                // Scan every currently-loaded UI text ONCE, tallying a vote for
                // whichever folder each matching reference key/value pair points to.
                var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var matchedKeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                int scanned = 0;
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_Text>());
                foreach (var o in all)
                {
                    if (o == null) continue;
                    var t = o.TryCast<TMP_Text>();
                    if (t == null) continue;
                    scanned++;
                    string disp = t.text;
                    if (string.IsNullOrEmpty(disp)) continue;

                    foreach (var key in UiRefKeys)
                    {
                        if (!perKeyReverse[key].TryGetValue(disp, out var folder)) continue;
                        votes.TryGetValue(folder, out int v);
                        votes[folder] = v + 1;
                        if (!matchedKeys.TryGetValue(folder, out var list)) { list = new List<string>(); matchedKeys[folder] = list; }
                        if (!list.Contains(key)) list.Add(key);
                    }
                }

                Plugin.Log.LogInfo($"[lang] UiText: scanned {scanned} live text component(s)");
                if (votes.Count == 0) { Plugin.Log.LogInfo("[lang] UiText: no reference text matched anything currently displayed"); return null; }

                string best = null; int bestVotes = -1;
                foreach (var kv in votes)
                {
                    Plugin.Log.LogInfo($"[lang] UiText: folder '{kv.Key}' matched {kv.Value} key(s): [{string.Join(",", matchedKeys[kv.Key].ToArray())}]");
                    if (kv.Value > bestVotes) { bestVotes = kv.Value; best = kv.Key; }
                }
                int tieCount = 0;
                foreach (var kv in votes) if (kv.Value == bestVotes) tieCount++;
                if (tieCount > 1)
                    Plugin.Log.LogInfo($"[lang] UiText: WARNING — {tieCount} folders tied at {bestVotes} vote(s); picking '{best}' (would need more/better reference keys to fully disambiguate this pair)");

                Plugin.Log.LogInfo($"[lang] UiText: winner = '{best}' with {bestVotes} vote(s)");
                return best;
            }
            catch (Exception ex) { Plugin.Log.LogInfo("[lang] UiText probe: " + ex.Message); }
            return null;
        }

        // NOTE FOR CONTRIBUTORS: this method has ZERO callers — its call site in
        // DetectLanguage() is deliberately commented out, not deleted. It's kept
        // only as documentation of a dead end: confirmed WORKING as a read (finds
        // and parses the right file), but the value itself proved STALE — it read
        // "english" while the game was actually running a different language, and
        // the identical "english" showed up across every other game's Wine prefix
        // on the same test machine too. It's a Unity default written once, not a
        // live-tracked setting, for this game. Do not re-enable without new
        // evidence that a different key/file in here would behave differently.
        // See README.md's Localization section for the full detection history.
        private static string TryProtonRegistryLanguage()
        {
            try
            {
                var compatDirs = new List<string>();

                string gameRoot = BepInEx.Paths.GameRootPath;
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string dir = gameRoot;
                    for (int i = 0; i < 8 && dir != null; i++)
                    {
                        string candidate = Path.Combine(dir, "steamapps", "compatdata");
                        if (Directory.Exists(candidate)) { compatDirs.Add(candidate); break; }
                        dir = Path.GetDirectoryName(dir);
                    }
                }

                string zHome = "Z:\\home";
                if (Directory.Exists(zHome))
                {
                    try
                    {
                        foreach (var userDir in Directory.GetDirectories(zHome))
                        {
                            string compat = Path.Combine(userDir, ".local", "share", "Steam", "steamapps", "compatdata");
                            if (Directory.Exists(compat)) compatDirs.Add(compat);
                        }
                    }
                    catch { }
                }

                foreach (var compat in compatDirs)
                {
                    // Prefer the confirmed real App ID; only fall back to scanning
                    // every prefix (higher false-positive risk) if it's not there.
                    string preferred = Path.Combine(compat, KnownAppId, "pfx", "user.reg");
                    string val = TryReadLanguageFromReg(preferred);
                    if (!string.IsNullOrEmpty(val)) { Plugin.Log.LogInfo($"[lang] ProtonRegistry: {KnownAppId}/user.reg language='{val}'"); return val; }
                }

                foreach (var compat in compatDirs)
                {
                    string[] appDirs;
                    try { appDirs = Directory.GetDirectories(compat); } catch { continue; }
                    foreach (var appDir in appDirs)
                    {
                        if (Path.GetFileName(appDir) == KnownAppId) continue; // already tried above
                        string val = TryReadLanguageFromReg(Path.Combine(appDir, "pfx", "user.reg"));
                        if (!string.IsNullOrEmpty(val))
                        {
                            Plugin.Log.LogInfo($"[lang] ProtonRegistry: {Path.GetFileName(appDir)}/user.reg language='{val}' (unconfirmed app id, using anyway since it resolved)");
                            return val;
                        }
                    }
                }

                Plugin.Log.LogInfo("[lang] ProtonRegistry: no matching 'language' registry entry found");
            }
            catch (Exception ex) { Plugin.Log.LogInfo("[lang] ProtonRegistry probe: " + ex.Message); }
            return null;
        }

        private static string TryReadLanguageFromReg(string regPath)
        {
            try
            {
                if (!File.Exists(regPath)) return null;
                foreach (var line in File.ReadLines(regPath))
                {
                    var m = RxLanguageLine.Match(line.Trim());
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
            return null;
        }

        private static string TryGameSettingsLanguage()
        {
            try
            {
                var dataType = FindTypeAnywhere("Hex.Settings.Data.SettingsData", "Hex");
                if (dataType == null) { Plugin.Log.LogInfo("[lang] GameSettings: SettingsData type not found"); return null; }

                string[] sectionTypes =
                {
                    "Hex.Settings.UI.BhGeneralSection", "Hex.Settings.UI.BhSettingsSection",
                    "Hex.Settings.UI.BhHotkeysSection", "Hex.Settings.UI.BhSoundSection",
                    "Hex.Settings.UI.BhVideoSection", "Hex.Settings.UI.BhVisibilitySection"
                };
                foreach (var stn in sectionTypes)
                {
                    var sectionType = FindTypeAnywhere(stn, "Hex");
                    if (sectionType == null) continue;

                    var all = UnityEngine.Resources.FindObjectsOfTypeAll(sectionType);
                    int n = all != null ? all.Length : 0;
                    for (int i = 0; i < n; i++)
                    {
                        var inst = all[i];
                        if (inst == null) continue;
                        var dataObj = DataExtractor.ReadObj(sectionType, inst, "bric");
                        if (dataObj == null) continue;
                        string lang = DataExtractor.Read(dataType, dataObj, "language");
                        if (!string.IsNullOrEmpty(lang) && lang != "?" && lang != "null")
                        {
                            Plugin.Log.LogInfo($"[lang] GameSettings: {stn}[{i}].bric.language = '{lang}'");
                            return lang;
                        }
                    }
                }
                Plugin.Log.LogInfo("[lang] GameSettings: no section instance had a populated language yet");
            }
            catch (Exception ex) { Plugin.Log.LogInfo("[lang] GameSettings probe: " + ex.Message); }
            return null;
        }

        private static string TryUnityLocalization()
        {
            try
            {
                var settingsType = FindTypeAnywhere("UnityEngine.Localization.Settings.LocalizationSettings", "Localization");
                if (settingsType == null) { Plugin.Log.LogInfo("[lang] UnityLocalization: LocalizationSettings type not found"); return null; }

                var selectedLocaleProp = settingsType.GetProperty("SelectedLocale", DataExtractor.FR);
                if (selectedLocaleProp == null) { Plugin.Log.LogInfo("[lang] UnityLocalization: SelectedLocale property not found"); return null; }
                var locale = selectedLocaleProp.GetValue(null);
                if (locale == null) { Plugin.Log.LogInfo("[lang] UnityLocalization: SelectedLocale is null (not initialized yet?)"); return null; }

                var localeType = FindTypeAnywhere("UnityEngine.Localization.Locale", "Localization");
                var idProp = localeType?.GetProperty("Identifier", DataExtractor.FR);
                var id = idProp?.GetValue(locale);
                if (id == null) { Plugin.Log.LogInfo("[lang] UnityLocalization: Locale.Identifier not found/null"); return null; }

                var idType = FindTypeAnywhere("UnityEngine.Localization.LocaleIdentifier", "Localization");
                var codeProp = idType?.GetProperty("Code", DataExtractor.FR);
                string code = codeProp?.GetValue(id)?.ToString();
                Plugin.Log.LogInfo($"[lang] UnityLocalization SelectedLocale.Identifier.Code = '{code}'");
                return code;
            }
            catch (Exception ex) { Plugin.Log.LogInfo("[lang] UnityLocalization probe: " + ex.Message); return null; }
        }

        // Finds a type by exact full name across all loaded Il2Cpp assemblies.
        // "assemblyHint" is checked first (assembly name contains it) for speed;
        // if the game doesn't have an assembly matching the hint, it almost
        // certainly isn't using that system at all, so no further scan is done.
        private static Il2CppSystem.Type FindTypeAnywhere(string fullName, string assemblyHint)
        {
            try
            {
                foreach (var asm in Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an; try { an = asm.GetName().Name; } catch { continue; }
                    if (string.IsNullOrEmpty(an) || an.IndexOf(assemblyHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Il2CppReferenceArray<Il2CppSystem.Type> types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    if (types == null) continue;
                    foreach (var t in types)
                    {
                        string tn; try { tn = t?.FullName; } catch { continue; }
                        if (tn == fullName) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool ResolvesTo(string value, List<string> folders)
        {
            string f = ResolveFolder((value ?? "").Trim().ToLowerInvariant(), folders);
            // ResolveFolder always returns *something*; only treat as a real hit if it
            // matched the value rather than falling through to the english/first default.
            string want = (value ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(want)) return false;
            return f == want || f.StartsWith(want, StringComparison.Ordinal) || want.StartsWith(f, StringComparison.Ordinal)
                   || SystemLangToFolder(want) == f || AliasFolder(want) == f;
        }

        private static string AliasFolder(string want)
        {
            switch (want)
            {
                case "en": case "eng": return "english";
                case "ru": case "rus": return "russian";
                case "de": case "ger": case "deu": case "deutsch": return "german";
                case "fr": case "fra": return "french";
                case "es": case "spa": return "spanish";
                case "pl": case "pol": case "polski": return "polish";
                case "cs": case "cz": case "cze": return "czech";
                case "it": case "ita": return "italian";
                case "hu": case "hun": return "hungarian";
                case "tr": case "tur": return "turkish";
                case "uk": case "ukr": return "ukrainian";
                case "ja": case "jp": case "jpn": return "japanese";
                case "ko": case "kor": return "korean";
                case "zh": case "zhcn": case "zh-cn": return "zhcn";
                case "zhtw": case "zh-tw": return "zhtw";
                case "pt": case "ptbr": case "pt-br": return "brportugese";
                default: return want;
            }
        }

        private static string SystemLangToFolder(string sys)
        {
            switch ((sys ?? "").ToLowerInvariant())
            {
                case "english": return "english";
                case "german": return "german";
                case "french": return "french";
                case "spanish": return "spanish";
                case "russian": return "russian";
                case "polish": return "polish";
                case "czech": return "czech";
                case "italian": return "italian";
                case "hungarian": return "hungarian";
                case "turkish": return "turkish";
                case "ukrainian": return "ukrainian";
                case "japanese": return "japanese";
                case "korean": return "korean";
                case "chinesesimplified": case "chinese": return "zhcn";
                case "chinesetraditional": return "zhtw";
                case "portuguese": return "brportugese";
                default: return null;
            }
        }

        // The folder segment right after "Lang/" (e.g. "english"), lowercased.
        private static string FolderOf(string fullName)
        {
            var parts = fullName.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i].Equals("Lang", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1].ToLowerInvariant();
            return null;
        }

        // Resolve the configured value to one of the available folders.
        private static string ResolveFolder(string want, List<string> folders)
        {
            if (folders == null || folders.Count == 0) return "";

            // common codes / aliases -> folder name
            string mapped = AliasFolder(want);

            foreach (var f in folders) if (f == mapped) return f;
            foreach (var f in folders) if (f == want) return f;
            if (!string.IsNullOrEmpty(want))
                foreach (var f in folders)
                    if (f.StartsWith(want, StringComparison.Ordinal) || want.StartsWith(f, StringComparison.Ordinal)) return f;

            foreach (var f in folders) if (f == "english") return f; // sensible default
            return folders[0];
        }

        // Auto-detect and parse the loc format into 'map'.
        private static void ParseInto(string name, string text, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Olden Era format: arrays of records { "sid": "...", "text": "..." }.
            if (ParseSidText(text, map) > 0) return;

            string ext = "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0) ext = name.Substring(dot + 1).ToLowerInvariant();

            if (ext == "json" || text.TrimStart().StartsWith("{")) { if (ParseJsonish(text, map) > 0) return; }
            if (ext == "po") { if (ParsePo(text, map) > 0) return; }

            char[] delims = { ',', '\t', ';', '=' };
            char best = '\0'; int bestN = 0;
            foreach (var d in delims) { int n = CountDelimited(text, d); if (n > bestN) { bestN = n; best = d; } }
            if (bestN > 0) { ParseDelimited(text, best, map); return; }
            ParseJsonish(text, map);
        }

        private static readonly Regex RxRecord =
            new Regex("\"sid\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"\\s*,\\s*\"text\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                      RegexOptions.Compiled | RegexOptions.Singleline);

        // Pull every sid+text pair with a single regex. Must NOT split on '}',
        // because values contain placeholders like {0}/{1} which include braces.
        private static int ParseSidText(string text, Dictionary<string, string> map)
        {
            if (text.IndexOf("\"sid\"", StringComparison.Ordinal) < 0) return 0;
            int added = 0;
            foreach (Match m in RxRecord.Matches(text))
            {
                string k = Unescape(m.Groups[1].Value);
                string v = Unescape(m.Groups[2].Value);
                if (string.IsNullOrEmpty(k)) continue;
                map[k] = v; added++;
            }
            return added;
        }

        private static int ParseJsonish(string text, Dictionary<string, string> map)
        {
            int added = 0;
            // matches  "key" : "value"  (tolerant of escapes)
            var rx = new Regex("\"((?:\\\\.|[^\"\\\\])*)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            foreach (Match m in rx.Matches(text))
            {
                string k = Unescape(m.Groups[1].Value);
                string v = Unescape(m.Groups[2].Value);
                if (string.IsNullOrEmpty(k)) continue;
                map[k] = v; added++;
            }
            return added;
        }

        private static int CountDelimited(string text, char d)
        {
            int n = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int idx = line.IndexOf(d);
                if (idx > 0 && idx < line.Length - 1) n++;
            }
            return n;
        }

        private static void ParseDelimited(string text, char d, Dictionary<string, string> map)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int idx = line.IndexOf(d);
                if (idx <= 0) continue;
                string k = line.Substring(0, idx).Trim().Trim('"');
                string v = line.Substring(idx + 1).Trim().Trim('"');
                if (k.Length == 0) continue;
                map[k] = Unescape(v);
            }
        }

        private static int ParsePo(string text, Dictionary<string, string> map)
        {
            int added = 0; string id = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("msgid "))
                    id = Unquote(line.Substring(6));
                else if (line.StartsWith("msgstr ") && id != null)
                {
                    var v = Unquote(line.Substring(7));
                    if (id.Length > 0 && v.Length > 0) { map[id] = v; added++; }
                    id = null;
                }
            }
            return added;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            return Unescape(s);
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        // F11: write a discovery report (entry list, previews, parse counts, key tests).
        public static void Probe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# OldenPedia lang probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            // force a fresh load into a temp map for full preview, then ensure main map
            try { _loaded = false; _map.Clear(); Load(sb); }
            catch (Exception ex) { sb.AppendLine("Load EX: " + ex); }
            _loaded = true;

            sb.AppendLine();
            sb.AppendLine("--- key tests ---");
            string[] keys =
            {
                "skeleton", "skeleton_name", "skeleton_upg", "unit_skeleton", "unit_skeleton_name",
                "sunlight_cavalry", "sunlight_cavalry_name", "peasant", "peasant_name", "avatar_name",
                "dragon", "dragon_name", "phoenix", "phoenix_name"
            };
            foreach (var k in keys)
                sb.AppendLine($"   {k} -> {( _map.TryGetValue(k, out var v) ? "\"" + v + "\"" : "(miss)")}");

            // Reverse lookup: find which sid maps to a known English unit name,
            // so we learn the unit-name key convention.
            sb.AppendLine();
            sb.AppendLine("--- reverse lookup (value -> sid) ---");
            string[] targets = { "Skeleton", "Peasant", "Sunlight Cavalry", "Dragon", "Phoenix", "Griffin", "Vampire", "Angel" };
            foreach (var t in targets)
            {
                int hits = 0;
                foreach (var kv in _map)
                {
                    if (string.Equals(kv.Value, t, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"   \"{t}\" <- {kv.Key}");
                        if (++hits >= 4) break;
                    }
                }
                if (hits == 0) sb.AppendLine($"   \"{t}\" : no exact value match");
            }

            // sample some real keys to learn the format
            sb.AppendLine();
            sb.AppendLine("--- sample entries ---");
            int shown = 0;
            foreach (var kv in _map)
            {
                string val = kv.Value;
                if (val != null && val.Length > 60) val = val.Substring(0, 60) + "…";
                sb.AppendLine($"   {kv.Key} = {val}");
                if (++shown >= 40) break;
            }

            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "lang_probe.txt");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[lang] probe written to {path} ({_map.Count} entries)");
            }
            catch (Exception ex) { Plugin.Log.LogError("[lang] probe write: " + ex.Message); }
        }
    }
}
