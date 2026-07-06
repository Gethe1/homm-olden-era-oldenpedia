using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace OldenPedia
{
    // IL2CPP plugins derive from BasePlugin (NOT BaseUnityPlugin, which is Mono).
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "oldenpedia.civilopedia";
        public const string Name = "OldenPedia";
        public const string Version = "0.75.0";

        internal static ManualLogSource Log;

        // The key that opens/closes OldenPedia. Default '.' (Period), rebindable
        // in BepInEx/config/oldenpedia.civilopedia.cfg.
        internal static Key ToggleKey = Key.Period;
        internal static string LangCode = "en";
        internal static bool BlockMapInput = true;
        internal static bool Show3DUnitPreview = false;

        private static ConfigEntry<string> _toggleKeyName;

        // Comma-separated type names the F4 detail probe inspects. Editable in
        // the cfg so we can re-aim it at new obfuscated types without rebuilding.
        internal static string[] InspectTypes = { "cjw", "cnn", "cjv" };
        private static ConfigEntry<string> _inspectTypes;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo($"{Name} {Version} loading…");

            try
            {
                BepInEx.Logging.Logger.Listeners.Add(new FileLogListener());
                Log.LogInfo("File log started → BepInEx/OldenPedia/console.txt");
            }
            catch (Exception ex) { Log.LogWarning($"file log listener failed: {ex.Message}"); }

            _toggleKeyName = Config.Bind(
                "General",
                "ToggleKey",
                "Period",
                "Key that opens/closes OldenPedia. Use a UnityEngine.InputSystem " +
                "Key name (e.g. Period, Comma, Backquote, F7) or the literal " +
                "symbol (e.g. \".\"). Default is '.' (Period).");

            ToggleKey = ResolveKey(_toggleKeyName.Value);
            Log.LogInfo($"Toggle key = {ToggleKey} (from config \"{_toggleKeyName.Value}\").");

            LangCode = Config.Bind(
                "General", "Language", "auto",
                "Language for names/descriptions, read from Core.zip/Lang. " +
                "'auto' detects the game's language automatically. " +
                "Or set a folder name: english, russian, german, french, spanish, polish, czech, " +
                "italian, hungarian, turkish, ukrainian, japanese, korean, zhcn, zhtw, brportugese " +
                "(codes en/de/fr/pl/... also work).").Value;

            BlockMapInput = Config.Bind(
                "General", "BlockMapInput", true,
                "While the pedia is open, stop the mouse wheel from zooming the map underneath. " +
                "Set false if it causes any input issues.").Value;

            Show3DUnitPreview = Config.Bind(
                "General", "Show3DUnitPreview", false,
                "EXPERIMENTAL. Renders the unit's actual 3D model in the detail panel " +
                "instead of no image. Off by default: it works by instantiating the game's " +
                "own unit prefab in isolation, which is generally safe but unverified across " +
                "all units. Try it; if a unit's page looks wrong or causes trouble, turn this " +
                "back off.").Value;

            _inspectTypes = Config.Bind(
                "Probe",
                "InspectTypes",
                "cjw,cnn,cjv",
                "Comma-separated type names for the F4 detail probe to dump fully.");
            InspectTypes = ParseList(_inspectTypes.Value);

            // IL2CPP can't see our managed MonoBehaviour until we register it.
            ClassInjector.RegisterTypeInIl2Cpp<PediaBehaviour>();

            // A hidden, persistent host object to run Update()/OnGUI().
            var go = new GameObject("OldenPedia");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            go.AddComponent<PediaBehaviour>();

            // Harmony-patch the game's legacy Input reads so the map ignores
            // clicks/scroll/hotkeys while the pedia is open.
            try
            {
                var harmony = new HarmonyLib.Harmony(Guid);
                InputBlocker.ApplyPatches(harmony);
            }
            catch (Exception ex) { Log.LogError("[block] Harmony init: " + ex.Message); }

            Log.LogInfo($"OldenPedia attached. '{_toggleKeyName.Value}' = open. " +
                        "F7-F10 = data/loc probes.");
        }

        // Maps a config string to an InputSystem Key without fragile IL2CPP enum
        // parsing. Falls back to Period (the requested default) if unrecognised.
        private static Key ResolveKey(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && KeyMap.TryGetValue(name.Trim(), out var k))
                return k;
            Log?.LogWarning($"Unrecognised ToggleKey \"{name}\"; falling back to Period.");
            return Key.Period;
        }

        private static string[] ParseList(string csv)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(csv))
                foreach (var s in csv.Split(','))
                {
                    var x = s.Trim();
                    if (x.Length > 0) list.Add(x);
                }
            return list.ToArray();
        }

        private static readonly Dictionary<string, Key> KeyMap =
            new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
            {
                { "Period", Key.Period }, { ".", Key.Period },
                { "Comma", Key.Comma }, { ",", Key.Comma },
                { "Backquote", Key.Backquote }, { "`", Key.Backquote },
                { "Backslash", Key.Backslash }, { "\\", Key.Backslash },
                { "Slash", Key.Slash }, { "/", Key.Slash },
                { "Semicolon", Key.Semicolon }, { ";", Key.Semicolon },
                { "Quote", Key.Quote }, { "'", Key.Quote },
                { "LeftBracket", Key.LeftBracket }, { "[", Key.LeftBracket },
                { "RightBracket", Key.RightBracket }, { "]", Key.RightBracket },
                { "Minus", Key.Minus }, { "-", Key.Minus },
                { "Equals", Key.Equals }, { "=", Key.Equals },
                { "Insert", Key.Insert }, { "Home", Key.Home }, { "End", Key.End },
                { "PageUp", Key.PageUp }, { "PageDown", Key.PageDown },
                { "P", Key.P }, { "O", Key.O }, { "J", Key.J }, { "K", Key.K },
                { "F1", Key.F1 }, { "F2", Key.F2 }, { "F3", Key.F3 },
                { "F4", Key.F4 }, { "F5", Key.F5 }, { "F6", Key.F6 },
                { "F7", Key.F7 }, { "F8", Key.F8 }, { "F9", Key.F9 },
                { "F10", Key.F10 }, { "F11", Key.F11 }, { "F12", Key.F12 },
            };
    }
}
