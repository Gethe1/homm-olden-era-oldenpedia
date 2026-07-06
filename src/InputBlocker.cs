using System;
using HarmonyLib;
using UnityEngine;

namespace OldenPedia
{
    // The game reads gameplay/camera input through legacy UnityEngine.Input
    // (scroll wheel, mouse buttons, keys, axes). While the pedia is open we
    // Harmony-patch those legacy reads to return neutral, so the map underneath
    // gets no clicks/scroll/hotkeys. The pedia itself reads the NEW Input System
    // (Mouse/Keyboard.current), which these patches don't touch, and the cursor
    // overlay only reads Input.mousePosition, which we deliberately leave alone so
    // the visible cursor keeps tracking.
    public static class InputBlocker
    {
        // Set true only while the pedia is open (and blocking is enabled).
        public static bool Blocking;

        public static void Block()   { if (Plugin.BlockMapInput) Blocking = true; }
        public static void Unblock() { Blocking = false; }

        public static void ApplyPatches(Harmony h)
        {
            int n = 0;
            n += Patch(h, AccessTools.PropertyGetter(typeof(Input), nameof(Input.mouseScrollDelta)), nameof(ZeroVector2));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetMouseButton), new[] { typeof(int) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonDown), new[] { typeof(int) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonUp), new[] { typeof(int) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKey), new[] { typeof(string) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(string) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(string) }), nameof(FalseResult));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetAxis), new[] { typeof(string) }), nameof(ZeroFloat));
            n += Patch(h, AccessTools.Method(typeof(Input), nameof(Input.GetAxisRaw), new[] { typeof(string) }), nameof(ZeroFloat));
            Plugin.Log.LogInfo($"[block] Harmony-patched {n} legacy Input reads");
        }

        private static int Patch(Harmony h, System.Reflection.MethodInfo target, string prefixName)
        {
            if (target == null) return 0;
            try
            {
                h.Patch(target, prefix: new HarmonyMethod(typeof(InputBlocker), prefixName));
                return 1;
            }
            catch (Exception ex) { Plugin.Log.LogError("[block] patch " + target.Name + ": " + ex.Message); return 0; }
        }

        // Prefixes: when blocking, set a neutral result and skip the original.
        private static bool FalseResult(ref bool __result) { if (!Blocking) return true; __result = false; return false; }
        private static bool ZeroFloat(ref float __result) { if (!Blocking) return true; __result = 0f; return false; }
        private static bool ZeroVector2(ref Vector2 __result) { if (!Blocking) return true; __result = Vector2.zero; return false; }
    }
}
