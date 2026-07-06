using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace OldenPedia
{
    // Resolves a font name (from the Y-probe's style dump, e.g. "LT-Regular") to
    // an already-loaded TMP_FontAsset, the same way IconLoader resolves icon keys
    // to Sprites — a one-time scan of loaded assets, matched by name or name
    // prefix (the game's own font instances often carry a "(Clone)" suffix).
    public static class FontLoader
    {
        private static readonly Dictionary<string, TMP_FontAsset> _cache =
            new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, TMP_FontAsset> _byName;
        private static bool _mapBuilt;

        public static TMP_FontAsset Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache.TryGetValue(name, out var cached)) return cached;

            TMP_FontAsset f = null;
            try { f = Resolve(name); }
            catch (Exception ex) { Plugin.Log.LogError("[font] resolve '" + name + "': " + ex.Message); }

            Plugin.Log.LogInfo($"[font] lookup '{name}' -> {(f != null ? "FOUND ('" + f.name + "')" : "not found")}");
            _cache[name] = f;
            return f;
        }

        private static TMP_FontAsset Resolve(string name)
        {
            EnsureMap();
            if (_byName.TryGetValue(name, out var exact)) return exact;
            // The game's own instances are often "<name>(Clone)" — match by prefix.
            foreach (var kv in _byName)
                if (kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private static void EnsureMap()
        {
            if (_mapBuilt) return;
            _mapBuilt = true;
            _byName = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>());
                int n = 0;
                foreach (var o in all)
                {
                    if (o == null) continue;
                    var fa = o.TryCast<TMP_FontAsset>();
                    if (fa == null) continue;
                    string nm = fa.name;
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (!_byName.ContainsKey(nm)) _byName[nm] = fa;
                    n++;
                }
                Plugin.Log.LogInfo($"[font] map: {n} loaded font assets, {_byName.Count} distinct names");
            }
            catch (Exception ex) { Plugin.Log.LogError("[font] map build: " + ex.Message); }
        }
    }
}
