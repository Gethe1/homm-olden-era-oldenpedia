using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace OldenPedia
{
    // Resolves a config "icon" string into a UnityEngine.Sprite using only
    // guaranteed-safe APIs (no Addressables interop dependency):
    //   1) Resources.Load<Sprite> with the key and a few common subpaths
    //   2) a one-time scan of already-loaded sprites, matched by name / leaf
    // Whatever it finds is cached. If icons turn out to be Addressables-only and
    // aren't loaded yet, Get returns null and the logged key tells us what to add.
    public static class IconLoader
    {
        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Sprite> _byName;
        private static bool _mapBuilt;
        private static int _loggedCount;

        public static Sprite Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            Sprite s = null;
            try { s = Resolve(key); }
            catch (Exception ex) { Plugin.Log.LogError("[icon] resolve '" + key + "': " + ex.Message); }

            if (_loggedCount < 20)
            {
                _loggedCount++;
                Plugin.Log.LogInfo($"[icon] lookup key='{key}' -> {(s != null ? "FOUND" : "not found")}; loadedSprites={(_byName != null ? _byName.Count : 0)}");
            }
            _cache[key] = s;
            return s;
        }

        private static Sprite Resolve(string key)
        {
            // 1) Resources folder (try key as-is and a couple of common roots)
            Sprite s = Resources.Load<Sprite>(key);
            if (s != null) return s;
            string leaf = Leaf(key);
            if (!string.Equals(leaf, key, StringComparison.Ordinal))
            {
                s = Resources.Load<Sprite>(leaf);
                if (s != null) return s;
            }

            // 2) already-loaded sprites, by name
            EnsureMap();
            if (_byName.TryGetValue(key, out s) && s != null) return s;
            if (_byName.TryGetValue(leaf, out s) && s != null) return s;
            return null;
        }

        private static void EnsureMap()
        {
            if (_mapBuilt) return;
            _mapBuilt = true;
            _byName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
                int n = 0;
                foreach (var o in all)
                {
                    if (o == null) continue;
                    var sp = o.TryCast<Sprite>();
                    if (sp == null) continue;
                    string nm = sp.name;
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (!_byName.ContainsKey(nm)) _byName[nm] = sp;
                    n++;
                }
                Plugin.Log.LogInfo($"[icon] sprite map: {n} loaded sprites, {_byName.Count} distinct names");
            }
            catch (Exception ex) { Plugin.Log.LogError("[icon] map build: " + ex.Message); }
        }

        private static string Leaf(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int i = key.LastIndexOfAny(new[] { '/', '\\' });
            string s = i >= 0 ? key.Substring(i + 1) : key;
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s;
        }
    }
}
