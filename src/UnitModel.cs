using System;
using System.Collections.Generic;
using UnityEngine;

namespace OldenPedia
{
    /// <summary>
    /// Step 1 of the town-style unit visual: confirm we can obtain a unit's 3D
    /// prefab outside battle. Golden Era interop exposes the prefab as
    /// `field_Private_GameObject_0`. Older `seo()`, `sew()`, and `bxwr`
    /// names remain compatibility candidates only; this is the only brittle
    /// obfuscated-name surface used by the 3D preview. If those fail, we log
    /// discovered GameObject members as diagnostics instead of selecting one silently.
    /// This is read-only and never throws into the UI.
    /// </summary>
    public static class UnitModel
    {
        private static readonly Il2CppSystem.Reflection.BindingFlags FR =
            (Il2CppSystem.Reflection.BindingFlags)60;

        private static string _last = "";
        private static readonly HashSet<string> _candidateLogs = new HashSet<string>();

        public static void Probe(string ownId)
        {
            try
            {
                if (string.IsNullOrEmpty(ownId) || ownId == _last) return;
                _last = ownId;

                var vt = DataExtractor.ViewType;
                var vc = DataExtractor.GetViewConfig(ownId);
                if (vt == null || vc == null) { Plugin.Log.LogInfo($"[model] {ownId}: no view config"); return; }

                string mesh = DataExtractor.Read(vt, vc, "mesh");
                var prefab = GetPrefabInternal(vt, vc);

                Plugin.Log.LogInfo($"[model] {ownId}: mesh='{mesh}', prefab={(prefab != null ? prefab.name : "null")}");
            }
            catch (Exception ex) { Plugin.Log.LogError($"[model] probe {ownId}: {ex.Message}"); }
        }

        // Returns the unit's 3D prefab (or null), for the preview renderer.
        public static GameObject GetPrefab(string ownId)
        {
            try
            {
                var vt = DataExtractor.ViewType;
                var vc = DataExtractor.GetViewConfig(ownId);
                if (vt == null || vc == null) return null;
                return GetPrefabInternal(vt, vc);
            }
            catch (Exception ex) { Plugin.Log.LogError($"[model] GetPrefab {ownId}: {ex.Message}"); return null; }
        }

        private static GameObject GetPrefabInternal(Il2CppSystem.Type vt, Il2CppSystem.Object vc)
        {
            GameObject prefab = null;
            try
            {
                prefab = TryPrefabProperty(vt, vc, "field_Private_GameObject_0");
                if (prefab == null) prefab = TryPrefabGetter(vt, vc, "seo");
                if (prefab == null) prefab = TryPrefabGetter(vt, vc, "sew");
                if (prefab == null) prefab = TryPrefabField(vt, vc, "bxwr");
                if (prefab == null) LogPrefabCandidates(vt);
            }
            catch { }
            return prefab;
        }

        private static GameObject TryPrefabProperty(Il2CppSystem.Type vt, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var p = vt.GetProperty(name, FR);
                if (p == null) return null;
                if (p.PropertyType == null || p.PropertyType.Name != "GameObject") return null;
                return TryReadPrefabProperty(p, vc, name);
            }
            catch { return null; }
        }

        private static GameObject TryPrefabGetter(Il2CppSystem.Type vt, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var m = vt.GetMethod(name, FR);
                if (m == null) return null;
                if (m.GetParameters().Length != 0) return null;
                if (m.ReturnType == null || m.ReturnType.Name != "GameObject") return null;
                return TryInvokePrefabGetter(m, vc, name);
            }
            catch { return null; }
        }

        private static GameObject TryInvokePrefabGetter(Il2CppSystem.Reflection.MethodInfo m, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var r = m.Invoke(vc, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(0));
                var prefab = r != null ? r.TryCast<GameObject>() : null;
                if (prefab != null)
                    Plugin.Log.LogInfo($"[model] prefab getter '{name}' resolved -> '{prefab.name}'");
                return prefab;
            }
            catch { return null; }
        }


        private static GameObject TryPrefabField(Il2CppSystem.Type vt, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var f = vt.GetField(name, FR);
                if (f == null) return null;
                if (f.FieldType == null || f.FieldType.Name != "GameObject") return null;
                return TryReadPrefabField(f, vc, name);
            }
            catch { return null; }
        }

        private static void LogPrefabCandidates(Il2CppSystem.Type vt)
        {
            try
            {
                string owner = SafeStr(() => vt.FullName);
                if (!_candidateLogs.Add(owner)) return;
                var names = new List<string>();
                foreach (var p in vt.GetProperties(FR))
                {
                    if (p == null || p.DeclaringType != vt) continue;
                    if (p.PropertyType != null && p.PropertyType.Name == "GameObject") names.Add("property " + p.Name);
                }
                foreach (var m in vt.GetMethods(FR))
                {
                    if (m == null || m.DeclaringType != vt) continue;
                    if (m.GetParameters().Length == 0 && m.ReturnType != null && m.ReturnType.Name == "GameObject") names.Add("getter " + m.Name);
                }
                foreach (var f in vt.GetFields(FR))
                {
                    if (f == null || f.DeclaringType != vt) continue;
                    if (f.FieldType != null && f.FieldType.Name == "GameObject") names.Add("field " + f.Name);
                }
                if (names.Count == 0)
                    Plugin.Log.LogWarning("[model] no validated prefab member found, and no GameObject members were discoverable on " + owner);
                else
                    Plugin.Log.LogWarning("[model] no validated prefab member found on " + owner + "; GameObject candidates for future mapping: " + string.Join(", ", names.ToArray()));
            }
            catch { }
        }

        private static GameObject TryReadPrefabField(Il2CppSystem.Reflection.FieldInfo f, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var r = f.GetValue(vc);
                var prefab = r != null ? r.TryCast<GameObject>() : null;
                if (prefab != null)
                    Plugin.Log.LogInfo($"[model] prefab field '{name}' resolved -> '{prefab.name}'");
                return prefab;
            }
            catch { return null; }
        }

        private static GameObject TryReadPrefabProperty(Il2CppSystem.Reflection.PropertyInfo p, Il2CppSystem.Object vc, string name)
        {
            try
            {
                var r = p.GetValue(vc);
                var prefab = r != null ? r.TryCast<GameObject>() : null;
                if (prefab != null)
                    Plugin.Log.LogInfo($"[model] prefab property '{name}' resolved -> '{prefab.name}'");
                return prefab;
            }
            catch { return null; }
        }

        private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }
}
