using System;
using UnityEngine;

namespace OldenPedia
{
    /// <summary>
    /// Step 1 of the town-style unit visual: confirm we can obtain a unit's 3D
    /// prefab outside battle. UnitViewConfig has `GameObject bxwr` and a getter
    /// `GameObject sew()`, plus a `String mesh` asset key. We log what we find so
    /// we know whether a render rig (instantiate prefab -> camera -> RenderTexture)
    /// is viable before building it. This is read-only and never throws into the UI.
    /// </summary>
    public static class UnitModel
    {
        private static readonly Il2CppSystem.Reflection.BindingFlags FR =
            (Il2CppSystem.Reflection.BindingFlags)60;

        private static string _last = "";

        public static void Probe(string ownId)
        {
            try
            {
                if (string.IsNullOrEmpty(ownId) || ownId == _last) return;
                _last = ownId;

                var vt = DataExtractor.ViewType;
                var vc = DataExtractor.GetViewConfig(ownId);
                if (vt == null || vc == null) { Plugin.Log.LogInfo($"[model] {ownId}: no view config"); return; }

                string mesh = SafeStr(() => { var f = vt.GetField("mesh", FR); return f != null ? f.GetValue(vc)?.ToString() : null; });
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
                var m = vt.GetMethod("sew", FR);
                if (m != null)
                {
                    var r = m.Invoke(vc, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(0));
                    if (r != null) prefab = r.TryCast<GameObject>();
                }
            }
            catch { }
            if (prefab == null)
            {
                try { var f = vt.GetField("bxwr", FR); var r = f != null ? f.GetValue(vc) : null; if (r != null) prefab = r.TryCast<GameObject>(); }
                catch { }
            }
            return prefab;
        }

        private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }
}
