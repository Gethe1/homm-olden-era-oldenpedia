using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OldenPedia
{
    /// <summary>
    /// Dumps the live UI hierarchy of every Canvas to a text file, including the
    /// visible text on each node (read reflectively, so it needs no TMP/UGUI
    /// reference). Run this on the main adventure screen: the node whose text is
    /// your gold/wood/etc. numbers is the resource bar, and its parent container
    /// is where the Pedia button gets appended.
    /// </summary>
    public static class UiProbe
    {
        public static void DumpMainScreenUi()
        {
            try
            {
                var sb = new StringBuilder();
                var seenRoots = new HashSet<int>();

                var canvases = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());
                sb.AppendLine($"# OldenPedia UI dump — {canvases.Length} canvases");
                sb.AppendLine("# Find the node whose text shows resource numbers (gold/wood/ore…).");
                sb.AppendLine("# Note its parent container + whether it has a *LayoutGroup component,");
                sb.AppendLine("# and pick a sibling element we can clone for the Pedia button.");
                sb.AppendLine();

                foreach (var obj in canvases)
                {
                    if (obj == null) continue;
                    // FindObjectsOfTypeAll returns UnityEngine.Object[]; cast to
                    // the real type before touching Component members.
                    var canvas = obj.TryCast<Canvas>();
                    if (canvas == null) continue;

                    var root = canvas.transform.root;
                    if (!seenRoots.Add(root.GetInstanceID())) continue;

                    sb.AppendLine($"=== ROOT: {root.gameObject.name} ===");
                    Walk(root, 0, sb);
                    sb.AppendLine();
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "ui_hierarchy.txt");
                File.WriteAllText(path, sb.ToString());

                Plugin.Log.LogInfo($"UI hierarchy ({seenRoots.Count} roots) written to {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"UI dump failed: {ex}");
            }
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            var go = t.gameObject;
            string indent = new string(' ', depth * 2);

            var comps = go.GetComponents(Il2CppType.Of<Component>());
            var names = new List<string>();
            string text = null;

            foreach (var comp in comps)
            {
                if (comp == null) continue;
                names.Add(Il2CppUtil.RuntimeTypeName(comp));
                if (text == null)
                {
                    var maybe = TryGetText(comp);
                    if (!string.IsNullOrEmpty(maybe)) text = maybe;
                }
            }

            string state = go.activeSelf ? "on" : "off";
            string line = $"{indent}{go.name} [{state}] sib={t.GetSiblingIndex()} <{string.Join(", ", names)}>";
            if (text != null)
            {
                text = text.Replace("\n", " ").Trim();
                if (text.Length > 40) text = text.Substring(0, 40) + "…";
                line += $"  text=\"{text}\"";
            }
            sb.AppendLine(line);

            int n = t.childCount;
            for (int i = 0; i < n; i++)
                Walk(t.GetChild(i), depth + 1, sb);
        }

        // Reads visible text by casting to the real text components. Reflection
        // via GetType() is unreliable on this interop build (returns the wrapper
        // type), so we TryCast to TMP_Text / UnityEngine.UI.Text directly.
        private static string TryGetText(Component comp)
        {
            try
            {
                var tmp = comp.TryCast<TMPro.TMP_Text>();
                if (tmp != null) return tmp.text;

                var uiText = comp.TryCast<UnityEngine.UI.Text>();
                if (uiText != null) return uiText.text;
            }
            catch { }
            return null;
        }
    }
}
