using System;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OldenPedia
{
    /// <summary>
    /// Token-based localizer. The game resolves loc keys through an obfuscated
    /// manager (currently type "cnn", base "cnl") that exposes a method shaped
    /// like  bool tat(string, string, out string)  — the lookup behind the
    /// runtime "[LocKit]" logs. We discover that type + method by SIGNATURE
    /// (not by its obfuscated name, so it survives patches), locate a live
    /// instance, and call it. The second string arg's meaning (language /
    /// category / fallback) is unknown, so we auto-detect the working call shape
    /// on first use and cache it. If discovery fails or a key is missing, we
    /// fall back to a humanized id. All reflection is read-only / defensive.
    /// </summary>
    public static class Localizer
    {
        // 60 = Instance|Static|Public|NonPublic (no DeclaredOnly -> includes inherited)
        private static readonly Il2CppSystem.Reflection.BindingFlags FR =
            (Il2CppSystem.Reflection.BindingFlags)60;

        private static bool _searched;
        private static Il2CppSystem.Object _loc;                       // resolver instance (null if static)
        private static Il2CppSystem.Reflection.MethodInfo _tat;        // bool (string,string,string&)
        private static bool _isStatic;
        private static int _shape = -1;                                // cached working call shape

        // Direct game loc table (e.g. cnk.bypx) — a static Dictionary<string,string>.
        private static Il2CppSystem.Object _dict;
        private static Il2CppSystem.Reflection.MethodInfo _dictContains, _dictGet;

        internal sealed class ResolverCand
        {
            public Il2CppSystem.Type Type;
            public Il2CppSystem.Reflection.MethodInfo Method;
            public bool IsStatic;
        }

        public static string Name(string nameKey, string id) => NameWith(nameKey, id, "");

        // Resolve a name by trying the config key, then each pattern ("{0}"=id),
        // then a humanized id. Patterns let categories use prefixes or infixes,
        // e.g. "skill_{0}_name" or "{0}_artifact_name".
        public static string NameByPatterns(string nameKey, string id, string[] patterns)
        {
            string r = Resolve(nameKey);
            if (!string.IsNullOrEmpty(r)) return r;
            if (patterns != null)
                foreach (var p in patterns)
                {
                    r = Resolve(string.Format(p, id));
                    if (!string.IsNullOrEmpty(r)) return r;
                }
            return Humanize(id);
        }

        public static string DescByPatterns(string descKey, string id, string[] patterns)
        {
            string r = Resolve(descKey);
            if (!string.IsNullOrEmpty(r)) return r;
            if (patterns != null)
                foreach (var p in patterns)
                {
                    r = Resolve(string.Format(p, id));
                    if (!string.IsNullOrEmpty(r)) return r;
                }
            return null;
        }

        // Resolve a display name, trying an entity-specific infix first
        // (e.g. "_artifact" -> "<id>_artifact_name") then the plain "<id>_name".
        public static string NameWith(string nameKey, string id, string infix)
        {
            string r = Resolve(nameKey);
            if (!string.IsNullOrEmpty(r)) return r;
            if (!string.IsNullOrEmpty(infix))
            {
                r = Resolve(id + infix + "_name");
                if (!string.IsNullOrEmpty(r)) return r;
            }
            r = Resolve(id + "_name");
            if (!string.IsNullOrEmpty(r)) return r;
            r = Resolve(id);
            if (!string.IsNullOrEmpty(r)) return r;
            return Humanize(id);
        }

        // Resolve a description, trying the config-provided key, then infix
        // variants, then plain id-based suffixes. Returns null if none hit.
        public static string DescWith(string descKey, string id, string infix)
        {
            string r = Resolve(descKey);
            if (!string.IsNullOrEmpty(r)) return r;
            if (!string.IsNullOrEmpty(infix))
            {
                r = Resolve(id + infix + "_narrativeDescription"); if (!string.IsNullOrEmpty(r)) return r;
                r = Resolve(id + infix + "_description"); if (!string.IsNullOrEmpty(r)) return r;
            }
            r = Resolve(id + "_narrativeDescription"); if (!string.IsNullOrEmpty(r)) return r;
            r = Resolve(id + "_description"); if (!string.IsNullOrEmpty(r)) return r;
            r = Resolve(id + "_desc"); if (!string.IsNullOrEmpty(r)) return r;
            return null;
        }

        public static string Resolve(string key)
        {
            if (string.IsNullOrEmpty(key) || key == "?" || key == "null") return null;

            // Preferred: the game's own translations read from Core.zip/Lang.
            if (LangLoader.TryGet(key, out var disk) && !string.IsNullOrEmpty(disk)) return disk;

            EnsureResolver();

            // direct static loc dictionary (the game's own table)
            if (_dict != null && _dictContains != null && _dictGet != null)
            {
                try
                {
                    var arg = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                    arg[0] = (Il2CppSystem.String)key;
                    var has = _dictContains.Invoke(_dict, arg);
                    if (has != null && has.Unbox<bool>())
                    {
                        var v = _dictGet.Invoke(_dict, arg);
                        var s = v != null ? v.ToString() : null;
                        if (!string.IsNullOrEmpty(s) && s != key) return s;
                    }
                }
                catch { }
            }

            if (_loc == null && _tat == null) return null;
            if (_tat == null) return null;

            // shape encodes (keyArgIndex, otherArgValue). Once one works, reuse it.
            if (_shape >= 0) return Call(key, _shape);
            for (int s = 0; s < Shapes; s++)
            {
                var v = Call(key, s);
                if (v != null) { _shape = s; return v; }
            }
            return null;
        }

        // Shapes: keyArg in {0,1} x otherArg in {"", "en", key}
        private const int Shapes = 6;
        private static string Call(string key, int shape)
        {
            try
            {
                int keyArg = shape % 2;             // 0 or 1
                int otherSel = shape / 2;           // 0,1,2
                string other = otherSel == 0 ? "" : otherSel == 1 ? "en" : key;

                var args = new Il2CppReferenceArray<Il2CppSystem.Object>(3);
                args[keyArg] = (Il2CppSystem.String)key;
                args[1 - keyArg] = (Il2CppSystem.String)other;
                args[2] = null; // out

                var ret = _tat.Invoke(_isStatic ? null : _loc, args);
                bool ok = ret != null && ret.Unbox<bool>();
                var outv = args[2];
                string s = outv != null ? outv.ToString() : null;
                if (ok && !string.IsNullOrEmpty(s) && s != key && s != "0") return s;
                return null;
            }
            catch { return null; }
        }

        public static string Humanize(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            var sb = new StringBuilder();
            foreach (var part in id.Split('_'))
            {
                if (part.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpper(part[0]));
                if (part.Length > 1) sb.Append(part.Substring(1));
            }
            return sb.ToString();
        }

        private static void EnsureResolver()
        {
            if (_searched) return;
            _searched = true;
            // If the on-disk Lang table loaded, we don't need the reflection
            // resolver at all — skip it (cleaner logs, no game calls).
            LangLoader.EnsureLoaded();
            if (LangLoader.Count > 0) return;
            try
            {
                var cands = FindAllResolverMethods();
                if (cands.Count == 0)
                {
                    Plugin.Log.LogInfo("[loc] no resolver method (bool (String,String,String&)) found; humanized ids.");
                    return;
                }

                // Best path: a static string->string dictionary on a candidate type
                // (e.g. cnk.bypx) — the game's own table, readable with no instance.
                if (TrySetupDict(cands))
                {
                    Plugin.Log.LogInfo($"[loc] test demon='{Resolve("demon")}' skeleton_name='{Resolve("skeleton_name")}' avatar_name='{Resolve("avatar_name")}'");
                }

                // Prefer a static resolver (no instance needed).
                foreach (var c in cands)
                {
                    if (!c.IsStatic) continue;
                    _tat = c.Method; _loc = null; _isStatic = true;
                    Plugin.Log.LogInfo($"[loc] static resolver {Safe(() => c.Type.FullName)}.{Safe(() => c.Method.Name)}");
                    Probe();
                    if (_shape >= 0) return;
                }

                // Otherwise an instance method whose instance is reachable via a
                // plain static field (safe — no arbitrary getter invocation here;
                // the F10 probe explores accessors and reports them).
                foreach (var c in cands)
                {
                    if (c.IsStatic) continue;
                    var inst = FindInstanceOfType(c.Type);
                    if (inst == null) continue;
                    _tat = c.Method; _loc = inst; _isStatic = false;
                    Plugin.Log.LogInfo($"[loc] instance resolver {Safe(() => c.Type.FullName)}.{Safe(() => c.Method.Name)}");
                    Probe();
                    if (_shape >= 0) return;
                }

                _tat = null; _loc = null;
                Plugin.Log.LogInfo($"[loc] {cands.Count} resolver candidate(s) but none produced text; humanized ids.");
            }
            catch (Exception ex) { Plugin.Log.LogError($"[loc] EnsureResolver: {ex.Message}"); }
        }

        private static void Probe()
        {
            string a = Resolve("demon"), b = Resolve("undead_city"), c = Resolve("skeleton");
            Plugin.Log.LogInfo($"[loc] test demon='{a}' undead_city='{b}' skeleton='{c}' (shape={_shape})");
        }

        // All methods (any Hex type) shaped bool (String, String, String&).
        internal static System.Collections.Generic.List<ResolverCand> FindAllResolverMethods()
        {
            var outp = new System.Collections.Generic.List<ResolverCand>();
            var DECL = (Il2CppSystem.Reflection.BindingFlags)62; // DeclaredOnly: faster, no inherited dupes
            foreach (var t in HexTypes())
            {
                Il2CppSystem.Reflection.MethodInfo[] methods;
                try { methods = t.GetMethods(DECL); } catch { continue; }
                if (methods == null) continue;
                foreach (var m in methods)
                {
                    if (m == null) continue;
                    try
                    {
                        if (m.ReturnType == null || m.ReturnType.Name != "Boolean") continue;
                        var ps = m.GetParameters();
                        if (ps == null || ps.Length != 3) continue;
                        var p0 = ps[0].ParameterType; var p1 = ps[1].ParameterType; var p2 = ps[2].ParameterType;
                        if (p0 == null || p1 == null || p2 == null) continue;
                        if (!p0.Name.StartsWith("String") || !p1.Name.StartsWith("String")) continue;
                        if (!p2.IsByRef || !p2.Name.StartsWith("String")) continue;
                        outp.Add(new ResolverCand { Type = t, Method = m, IsStatic = m.IsStatic });
                    }
                    catch { }
                }
            }
            return outp;
        }

        internal static Il2CppSystem.Reflection.MethodInfo FindResolverMethod(out Il2CppSystem.Type owner)
        {
            owner = null;
            var all = FindAllResolverMethods();
            if (all.Count == 0) return null;
            owner = all[0].Type;
            return all[0].Method;
        }

        // Reach an instance of 'type': static fields anywhere, then static no-arg
        // accessors (methods/properties) on the type or its base (singleton getters).
        internal static Il2CppSystem.Object TryGetInstance(Il2CppSystem.Type type)
        {
            var byField = FindInstanceOfType(type);
            if (byField != null) return byField;

            for (Il2CppSystem.Type t = type; t != null; t = SafeBase(t))
            {
                // static properties with a getter returning an assignable type
                Il2CppSystem.Reflection.PropertyInfo[] props;
                try { props = t.GetProperties((Il2CppSystem.Reflection.BindingFlags)62); } catch { props = null; }
                if (props != null)
                    foreach (var p in props)
                    {
                        try
                        {
                            if (p == null) continue;
                            var g = p.GetGetMethod();
                            if (g == null || !g.IsStatic) continue;
                            var pr = g.GetParameters();
                            if (pr != null && pr.Length != 0) continue;
                            if (!Assignable(type, p.PropertyType)) continue;
                            var v = g.Invoke(null, new Il2CppReferenceArray<Il2CppSystem.Object>(0));
                            if (v != null) return v;
                        }
                        catch { }
                    }

                // static no-arg methods returning an assignable type
                Il2CppSystem.Reflection.MethodInfo[] ms;
                try { ms = t.GetMethods((Il2CppSystem.Reflection.BindingFlags)62); } catch { ms = null; }
                if (ms != null)
                    foreach (var m in ms)
                    {
                        try
                        {
                            if (m == null || !m.IsStatic) continue;
                            var pr = m.GetParameters();
                            if (pr == null || pr.Length != 0) continue;
                            if (!Assignable(type, m.ReturnType)) continue;
                            var v = m.Invoke(null, new Il2CppReferenceArray<Il2CppSystem.Object>(0));
                            if (v != null) return v;
                        }
                        catch { }
                    }
            }
            return null;
        }

        // Look for a static Dictionary<string,string> on any candidate type and wire it.
        private static bool TrySetupDict(System.Collections.Generic.List<ResolverCand> cands)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var c in cands)
            {
                string tn; try { tn = c.Type.FullName; } catch { continue; }
                if (tn == null || !seen.Add(tn)) continue;

                Il2CppSystem.Reflection.FieldInfo[] fields;
                try { fields = c.Type.GetFields(FR); } catch { continue; }
                if (fields == null) continue;
                foreach (var f in fields)
                {
                    if (f == null || !f.IsStatic) continue;
                    string fn; try { fn = f.FieldType.FullName; } catch { continue; }
                    if (fn == null || fn.IndexOf("Dictionary`2", StringComparison.Ordinal) < 0) continue;
                    if (CountStr(fn, "System.String") < 2) continue; // need string keys AND string values

                    Il2CppSystem.Object dict;
                    try { dict = f.GetValue(null); } catch { continue; }
                    if (dict == null) continue;
                    int count = DictCount(f.FieldType, dict);
                    if (count < 200) continue; // tiny static dicts are caches, not the loc table

                    _dict = dict;
                    _dictContains = f.FieldType.GetMethod("ContainsKey", FR);
                    _dictGet = f.FieldType.GetMethod("get_Item", FR);
                    if (_dictContains == null || _dictGet == null) { _dict = null; continue; }
                    Plugin.Log.LogInfo($"[loc] static string table {tn}.{Safe(() => f.Name)} ({count} entries)");
                    return true;
                }
            }
            return false;
        }

        private static int DictCount(Il2CppSystem.Type dictType, Il2CppSystem.Object dict)
        {
            try
            {
                var getCount = dictType.GetMethod("get_Count", FR);
                if (getCount == null) return 0;
                var r = getCount.Invoke(dict, new Il2CppReferenceArray<Il2CppSystem.Object>(0));
                return r != null ? r.Unbox<int>() : 0;
            }
            catch { return 0; }
        }

        private static int CountStr(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        private static bool Assignable(Il2CppSystem.Type want, Il2CppSystem.Type got)
        {
            if (want == null || got == null) return false;
            try
            {
                if (got.FullName == want.FullName) return true;
                if (want.IsAssignableFrom(got)) return true;
                if (got.IsAssignableFrom(want)) return true;
            }
            catch { }
            return false;
        }

        private static Il2CppSystem.Type SafeBase(Il2CppSystem.Type t)
        {
            try { return t.BaseType; } catch { return null; }
        }

        // Scan Hex assemblies for a non-null static field assignable to 'wanted'.
        internal static Il2CppSystem.Object FindInstanceOfType(Il2CppSystem.Type wanted)
        {
            string wantedName = Safe(() => wanted.FullName);
            foreach (var t in HexTypes())
            {
                Il2CppSystem.Reflection.FieldInfo[] fields;
                try { fields = t.GetFields(FR); } catch { continue; }
                if (fields == null) continue;
                foreach (var f in fields)
                {
                    if (f == null || !f.IsStatic) continue;
                    try
                    {
                        var ft = f.FieldType;
                        if (ft == null) continue;
                        // same type or subclass
                        bool match = ft.FullName == wantedName || (wanted.IsAssignableFrom(ft));
                        if (!match) continue;
                        var v = f.GetValue(null);
                        if (v != null) return v;
                    }
                    catch { }
                }
            }
            return null;
        }

        internal static System.Collections.Generic.IEnumerable<Il2CppSystem.Type> HexTypes()
        {
            var list = new System.Collections.Generic.List<Il2CppSystem.Type>();
            try
            {
                foreach (var asm in Il2CppSystem.AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an; try { an = asm.GetName().Name; } catch { continue; }
                    if (string.IsNullOrEmpty(an) || !an.StartsWith("Hex")) continue;
                    Il2CppReferenceArray<Il2CppSystem.Type> types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    if (types == null) continue;
                    foreach (var t in types) if (t != null) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }
}
