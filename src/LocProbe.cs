using System;
using System.IO;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OldenPedia
{
    /// <summary>
    /// F10: authoritative localization recon. Writes BepInEx/OldenPedia/loc_probe.txt
    /// with the resolver method signature, the instance source, the result of
    /// calling it with several keys across candidate argument shapes, and the
    /// DataToken layout (so the token table can be read directly if needed).
    /// Read-only; everything wrapped defensively.
    /// </summary>
    public static class LocProbe
    {
        private static readonly Il2CppSystem.Reflection.BindingFlags FR =
            (Il2CppSystem.Reflection.BindingFlags)60;

        private static readonly string[] TestKeys =
        {
            "demon", "undead", "demon_city", "undead_city",
            "avatar_name", "peasant_name", "sunlight_cavalry", "skeleton"
        };

        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# OldenPedia loc probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            try { Run(sb); }
            catch (Exception ex) { sb.AppendLine("FATAL: " + ex); }

            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "loc_probe.txt");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[locprobe] written to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError("[locprobe] write: " + ex.Message); }
        }

        private static void Run(StringBuilder sb)
        {
            // ---- 1) every method shaped bool (String,String,String&) ----
            var cands = Localizer.FindAllResolverMethods();
            sb.AppendLine($"resolver candidates: {cands.Count}");
            sb.AppendLine();

            foreach (var c in cands)
            {
                string tn = SafeName(c.Type);
                sb.AppendLine($"=== {tn} . {Safe(() => c.Method.Name)}  static={c.IsStatic}  locFields=[{LocFieldsOf(c.Type)}] ===");

                Il2CppSystem.Object target = null;
                if (c.IsStatic) sb.AppendLine("  (static — no instance needed)");
                else
                {
                    target = Localizer.TryGetInstance(c.Type);
                    sb.AppendLine("  instance: " + (target != null ? "FOUND" : "NOT FOUND"));
                    if (target == null) { sb.AppendLine(); continue; }
                }

                bool anyHit = false;
                foreach (var key in TestKeys)
                    for (int shape = 0; shape < 6; shape++)
                    {
                        string res = TryCall(c.Method, target, key, shape, out bool ok);
                        if (ok && res != null && res != key)
                        {
                            int keyArg = shape % 2, sel = shape / 2;
                            string other = sel == 0 ? "\"\"" : sel == 1 ? "en" : "<key>";
                            sb.AppendLine($"  HIT {key} | k@{keyArg},o={other} | \"{res}\"");
                            anyHit = true;
                        }
                    }
                if (!anyHit) sb.AppendLine("  (no key produced text across shapes)");

                // if this is the real resolver, try to read its token table from instance fields
                if (target != null) DumpInstanceLoc(sb, c.Type, target);
                sb.AppendLine();
            }

            // ---- candidate static dictionaries (the game's loc table) ----
            sb.AppendLine("--- candidate static dictionaries ---");
            DumpCandidateDicts(sb, cands);
            sb.AppendLine();

            // ---- live instances via Unity object registry ----
            sb.AppendLine("--- instance search (Resources.FindObjectsOfTypeAll) ---");
            SearchInstances(sb, cands);
            sb.AppendLine();

            // ---- on-disk localization files ----
            sb.AppendLine("--- streamingAssets loc-file scan ---");
            ScanLocFiles(sb);
            sb.AppendLine();

            // ---- LocData via static fields (fallback search) ----
            sb.AppendLine("--- LocData / DataToken (static-field search) ---");
            DumpLocData(sb);
        }

        private static void SearchInstances(StringBuilder sb, System.Collections.Generic.List<Localizer.ResolverCand> cands)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var c in cands)
            {
                if (c.IsStatic) continue;
                string tn = SafeName(c.Type);
                if (!seen.Add(tn)) continue;
                try
                {
                    var arr = UnityEngine.Resources.FindObjectsOfTypeAll(c.Type);
                    int n = arr != null ? arr.Length : 0;
                    sb.AppendLine($"{tn}: found {n}");
                    if (n <= 0) continue;
                    Il2CppSystem.Object inst = arr[0];
                    var f = c.Type.GetField("bypy", FR);
                    if (f == null) { sb.AppendLine("   (no bypy field)"); continue; }
                    Il2CppSystem.Object d; try { d = f.GetValue(inst); } catch { continue; }
                    int cnt = d != null ? DictCount(f.FieldType, d) : -1;
                    sb.AppendLine($"   bypy count={cnt}");
                    if (d != null && cnt > 0) TestDict(sb, f.FieldType, d);
                }
                catch (Exception ex) { sb.AppendLine($"{tn}: EX {ex.Message}"); }
            }
        }

        private static void TestDict(StringBuilder sb, Il2CppSystem.Type dt, Il2CppSystem.Object dict)
        {
            var contains = dt.GetMethod("ContainsKey", FR);
            var getItem = dt.GetMethod("get_Item", FR);
            if (contains == null || getItem == null) return;
            foreach (var k in DictTestKeys)
            {
                try
                {
                    var arg = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                    arg[0] = (Il2CppSystem.String)k;
                    var has = contains.Invoke(dict, arg);
                    if (has == null || !has.Unbox<bool>()) continue;
                    var v = getItem.Invoke(dict, arg);
                    string val = v != null ? v.ToString() : "null";
                    if (val != null && val.Length > 80) val = val.Substring(0, 80) + "…";
                    sb.AppendLine($"   HIT {k} = \"{val}\"");
                }
                catch { }
            }
        }

        private static void ScanLocFiles(StringBuilder sb)
        {
            try
            {
                string sa = UnityEngine.Application.streamingAssetsPath;
                sb.AppendLine("streamingAssets = " + sa);
                if (!Directory.Exists(sa)) { sb.AppendLine("  (dir not found)"); return; }
                var files = Directory.GetFiles(sa, "*", SearchOption.AllDirectories);
                sb.AppendLine($"  {files.Length} files total");
                int shown = 0;
                foreach (var p in files)
                {
                    string low = p.ToLowerInvariant();
                    if (low.Contains("loc") || low.Contains("translat") || low.Contains("lang") ||
                        low.EndsWith(".csv") || low.EndsWith(".po") || low.EndsWith(".json") ||
                        low.EndsWith(".txt") || low.EndsWith(".pak") || low.EndsWith(".bytes"))
                    {
                        long len = 0; try { len = new FileInfo(p).Length; } catch { }
                        sb.AppendLine($"   {p} ({len} bytes)");
                        if (++shown >= 80) { sb.AppendLine("   …(truncated)"); break; }
                    }
                }
                if (shown == 0) sb.AppendLine("   (no obvious loc files by name/extension)");
            }
            catch (Exception ex) { sb.AppendLine("ScanLocFiles: " + ex.Message); }
        }

        private static readonly string[] DictTestKeys =
        {
            "skeleton", "skeleton_name", "skeleton_upg", "peasant", "peasant_name",
            "avatar", "avatar_name", "sunlight_cavalry", "sunlight_cavalry_name",
            "demon", "demon_city", "undead", "undead_city",
            "unit_skeleton", "unit_skeleton_name", "name_skeleton", "loc_skeleton"
        };

        private static void DumpCandidateDicts(StringBuilder sb, System.Collections.Generic.List<Localizer.ResolverCand> cands)
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var c in cands)
            {
                string tn = SafeName(c.Type);
                if (!seen.Add(tn)) continue;

                Il2CppSystem.Reflection.FieldInfo[] fields;
                try { fields = c.Type.GetFields(FR); } catch { continue; }
                if (fields == null) continue;
                foreach (var f in fields)
                {
                    string fn; try { fn = f.FieldType.FullName; } catch { continue; }
                    if (fn == null || fn.IndexOf("Dictionary`2", StringComparison.Ordinal) < 0) continue;

                    if (!f.IsStatic)
                    {
                        sb.AppendLine($"{tn}.{Safe(() => f.Name)} (instance) : {ShortName(fn)}  [needs instance]");
                        continue;
                    }

                    Il2CppSystem.Object dict; try { dict = f.GetValue(null); } catch { continue; }
                    int count = dict != null ? DictCount(f.FieldType, dict) : -1;
                    sb.AppendLine($"{tn}.{Safe(() => f.Name)} static : {fn}  count={count}");
                    if (dict == null || count <= 0) continue;

                    var contains = f.FieldType.GetMethod("ContainsKey", FR);
                    var getItem = f.FieldType.GetMethod("get_Item", FR);
                    if (contains == null || getItem == null) { sb.AppendLine("   (no ContainsKey/get_Item)"); continue; }

                    foreach (var k in DictTestKeys)
                    {
                        try
                        {
                            var arg = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
                            arg[0] = (Il2CppSystem.String)k;
                            var has = contains.Invoke(dict, arg);
                            if (has == null || !has.Unbox<bool>()) continue;
                            var v = getItem.Invoke(dict, arg);
                            string val = v != null ? v.ToString() : "null";
                            if (val != null && val.Length > 80) val = val.Substring(0, 80) + "…";
                            sb.AppendLine($"   HIT {k} = \"{val}\"");
                        }
                        catch { }
                    }
                }
            }
        }

        private static int DictCount(Il2CppSystem.Type dt, Il2CppSystem.Object dict)
        {
            try
            {
                var gc = dt.GetMethod("get_Count", FR);
                if (gc == null) return -1;
                var r = gc.Invoke(dict, new Il2CppReferenceArray<Il2CppSystem.Object>(0));
                return r != null ? r.Unbox<int>() : -1;
            }
            catch { return -1; }
        }

        // names of fields on a type whose type references loc/token tables
        private static string LocFieldsOf(Il2CppSystem.Type t)
        {
            var sb = new StringBuilder();
            try
            {
                var fs = t.GetFields(FR);
                if (fs != null)
                    foreach (var f in fs)
                    {
                        string ftn; try { ftn = f.FieldType.FullName; } catch { continue; }
                        if (ftn == null) continue;
                        if (ftn.IndexOf("LocData", StringComparison.Ordinal) >= 0 ||
                            ftn.IndexOf("DataToken", StringComparison.Ordinal) >= 0 ||
                            ftn.IndexOf("Dictionary", StringComparison.Ordinal) >= 0)
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append($"{Safe(() => f.Name)}:{ShortName(ftn)}");
                        }
                    }
            }
            catch { }
            return sb.ToString();
        }

        // read LocData/tokens reachable from a live instance's fields
        private static void DumpInstanceLoc(StringBuilder sb, Il2CppSystem.Type t, Il2CppSystem.Object inst)
        {
            try
            {
                var fs = t.GetFields(FR);
                if (fs == null) return;
                foreach (var f in fs)
                {
                    string ftn; try { ftn = f.FieldType.FullName; } catch { continue; }
                    if (ftn == null || ftn.IndexOf("LocData", StringComparison.Ordinal) < 0) continue;
                    Il2CppSystem.Object ld; try { ld = f.GetValue(inst); } catch { continue; }
                    if (ld == null) continue;
                    sb.AppendLine($"  -> LocData field {Safe(() => f.Name)}; dumping tokens:");
                    DumpTokens(sb, f.FieldType, ld, "    ");
                    return;
                }
            }
            catch { }
        }

        private static string TryCall(Il2CppSystem.Reflection.MethodInfo m, Il2CppSystem.Object inst,
                                      string key, int shape, out bool ok)
        {
            ok = false;
            try
            {
                int keyArg = shape % 2, sel = shape / 2;
                string other = sel == 0 ? "" : sel == 1 ? "en" : key;
                var args = new Il2CppReferenceArray<Il2CppSystem.Object>(3);
                args[keyArg] = (Il2CppSystem.String)key;
                args[1 - keyArg] = (Il2CppSystem.String)other;
                args[2] = null;
                var ret = m.Invoke(inst, args);
                ok = ret != null && ret.Unbox<bool>();
                var outv = args[2];
                return outv != null ? outv.ToString() : null;
            }
            catch (Exception ex) { return "EX:" + ex.Message; }
        }

        private static void DumpLocData(StringBuilder sb)
        {
            try
            {
                Il2CppSystem.Object locData = null;
                Il2CppSystem.Type ldType = null;
                foreach (var t in Localizer.HexTypes())
                {
                    Il2CppSystem.Reflection.FieldInfo[] fields;
                    try { fields = t.GetFields(FR); } catch { continue; }
                    if (fields == null) continue;
                    foreach (var f in fields)
                    {
                        if (f == null || !f.IsStatic) continue;
                        string ftn; try { ftn = f.FieldType.FullName; } catch { continue; }
                        if (ftn == null || ftn.IndexOf("LocData", StringComparison.Ordinal) < 0) continue;
                        Il2CppSystem.Object v; try { v = f.GetValue(null); } catch { continue; }
                        if (v != null) { locData = v; ldType = f.FieldType; sb.AppendLine($"LocData via {SafeName(t)}.{Safe(() => f.Name)} = {ftn}"); break; }
                    }
                    if (locData != null) break;
                }
                if (locData == null) { sb.AppendLine("no live LocData static field found"); return; }
                DumpTokens(sb, ldType, locData, "");
            }
            catch (Exception ex) { sb.AppendLine("DumpLocData: " + ex); }
        }

        // Given a LocData instance, dump its DataToken[] layout + first entries.
        private static void DumpTokens(StringBuilder sb, Il2CppSystem.Type ldType, Il2CppSystem.Object locData, string ind)
        {
            try
            {
                var tokensF = ldType.GetField("tokens", FR);
                if (tokensF == null) { sb.AppendLine(ind + "LocData.tokens field not found"); return; }
                var arrObj = tokensF.GetValue(locData);
                var arr = arrObj != null ? arrObj.TryCast<Il2CppSystem.Array>() : null;
                if (arr == null) { sb.AppendLine(ind + "tokens not an array / null"); return; }
                int n = arr.Length;
                sb.AppendLine($"{ind}tokens length = {n}");

                Il2CppSystem.Type dtType = FindTokenType();
                if (dtType == null) { sb.AppendLine(ind + "DataToken type not resolved"); return; }
                var dfields = dtType.GetFields(FR);
                sb.AppendLine(ind + "DataToken fields:");
                if (dfields != null) foreach (var f in dfields) sb.AppendLine($"{ind}  {Safe(() => f.FieldType.Name)} {Safe(() => f.Name)}");

                int show = n < 10 ? n : 10;
                sb.AppendLine($"{ind}first {show} tokens:");
                for (int i = 0; i < show; i++)
                {
                    var tok = arr.GetValue(i);
                    if (tok == null) { sb.AppendLine($"{ind}  [{i}] null"); continue; }
                    var parts = new StringBuilder();
                    if (dfields != null)
                        foreach (var f in dfields)
                        {
                            string val;
                            try { var fv = f.GetValue(tok); val = fv != null ? fv.ToString() : "null"; }
                            catch { val = "<err>"; }
                            if (val != null && val.Length > 60) val = val.Substring(0, 60) + "…";
                            parts.Append($"{Safe(() => f.Name)}={val}  ");
                        }
                    sb.AppendLine($"{ind}  [{i}] {parts}");
                }
            }
            catch (Exception ex) { sb.AppendLine(ind + "DumpTokens: " + ex.Message); }
        }

        private static string ShortName(string full)
        {
            if (string.IsNullOrEmpty(full)) return full;
            int lt = full.IndexOf('[');
            string head = lt >= 0 ? full.Substring(0, lt) : full;
            int dot = head.LastIndexOf('.');
            return dot >= 0 ? head.Substring(dot + 1) : head;
        }

        private static Il2CppSystem.Type FindTokenType()
        {
            foreach (var t in Localizer.HexTypes())
            {
                string fn; try { fn = t.FullName; } catch { continue; }
                if (fn == "Hex.Loc.DataToken" || (fn != null && fn.EndsWith(".DataToken", StringComparison.Ordinal)))
                    return t;
            }
            return null;
        }

        private static string SafeName(Il2CppSystem.Type t) { try { return t.FullName; } catch { return "?"; } }
        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }
}
