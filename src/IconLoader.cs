using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace OldenPedia
{
    // Resolves a config "icon" string into a UnityEngine.Sprite using only
    // guaranteed-safe APIs (no Addressables interop dependency):
    //   1) Resources.Load<Sprite> with the key and a few common forms
    //   2) a one-time scan of already-loaded sprites, matched by name / leaf
    //   3) a bounded suffix match when there is exactly one clear hit
    // Whatever it finds is cached. If icons turn out to be Addressables-only and
    // aren't loaded yet, Get returns null and the logged key tells us what to add.
    public static class IconLoader
    {
        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Sprite> _byName;
        private static readonly List<string> _loadedNames = new List<string>();
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
            foreach (var candidate in CandidateForms(key))
            {
                Sprite s = Resources.Load<Sprite>(candidate);
                if (s != null) return s;
            }

            EnsureMap();
            foreach (var candidate in CandidateForms(key))
            {
                Sprite s;
                if (_byName.TryGetValue(candidate, out s) && s != null) return s;
            }

            foreach (var candidate in CandidateForms(key))
            {
                string suffixHit;
                Sprite s = TrySuffixMatch(candidate, out suffixHit);
                if (s != null) return s;
                if (!string.IsNullOrEmpty(suffixHit))
                    Plugin.Log.LogWarning($"[icon] key='{key}' suffix='{candidate}' ambiguous: {suffixHit}");
            }
            return null;
        }

        private static void EnsureMap()
        {
            if (_mapBuilt) return;
            _mapBuilt = true;
            _byName = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
            _loadedNames.Clear();
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
                    _loadedNames.Add(nm);
                    n++;
                }
                Plugin.Log.LogInfo($"[icon] sprite map: {n} loaded sprites, {_byName.Count} distinct names");
            }
            catch (Exception ex) { Plugin.Log.LogError("[icon] map build: " + ex.Message); }
        }

        private static IEnumerable<string> CandidateForms(string key)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var forms = new List<string>();

            void Add(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (seen.Add(s)) forms.Add(s);
            }

            Add(key);
            Add(Normalize(key));
            Add(Leaf(key));
            Add(Normalize(Leaf(key)));

            for (int i = 0; i < forms.Count; i++) yield return forms[i];
        }

        private static string Normalize(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string s = key.Trim().Replace('\\', '/');
            while (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
            while (s.EndsWith("/", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            int slash = s.LastIndexOf('/');
            int dot = s.LastIndexOf('.');
            if (dot > slash) s = s.Substring(0, dot);
            return s;
        }

        private static string Leaf(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int i = key.LastIndexOfAny(new[] { '/', '\\' });
            string s = i >= 0 ? key.Substring(i + 1) : key;
            return Normalize(s);
        }

        private static Sprite TrySuffixMatch(string candidate, out string matchedNames)
        {
            matchedNames = null;
            if (string.IsNullOrEmpty(candidate) || _loadedNames.Count == 0) return null;

            var hits = new List<string>();
            for (int i = 0; i < _loadedNames.Count; i++)
            {
                string name = _loadedNames[i];
                if (string.IsNullOrEmpty(name)) continue;
                string norm = Normalize(name);
                string leaf = Leaf(name);
                if (!name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase) &&
                    !norm.EndsWith(candidate, StringComparison.OrdinalIgnoreCase) &&
                    !leaf.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool seen = false;
                for (int j = 0; j < hits.Count; j++)
                    if (string.Equals(hits[j], name, StringComparison.OrdinalIgnoreCase)) { seen = true; break; }
                if (!seen) hits.Add(name);
                if (hits.Count > 8) break;
            }

            if (hits.Count == 1)
            {
                Sprite s;
                return _byName.TryGetValue(hits[0], out s) ? s : null;
            }

            if (hits.Count > 1)
            {
                matchedNames = string.Join(", ", hits.ToArray());
                return null;
            }

            return null;
        }
    }
}
