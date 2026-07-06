using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace OldenPedia
{
    // Y: dumps the VISUAL STYLE (not just structure) of whatever game UI is
    // currently open — colors, sprite names, borders, fonts — so the pedia's own
    // look can be matched to a specific screen (open that screen first, then
    // press F1). Complements UiProbe (F9), which dumps structure/text but not
    // style. Read-only: Image/TMP_Text members we already use directly elsewhere
    // in this mod, so this only touches confirmed-safe, already-bound APIs.
    public static class StyleProbe
    {
        public static void Dump()
        {
            var sb = new StringBuilder();
            try
            {
                var canvases = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());
                var seenRoots = new HashSet<int>();
                sb.AppendLine($"# OldenPedia style probe — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("# Open the screen whose look you want copied, THEN press Y.");
                sb.AppendLine("# [BG?] = large sliced/tiled image, likely a panel background/frame.");
                sb.AppendLine();

                foreach (var obj in canvases)
                {
                    if (obj == null) continue;
                    var canvas = obj.TryCast<Canvas>();
                    if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;

                    var root = canvas.transform.root;
                    if (!seenRoots.Add(root.GetInstanceID())) continue;

                    sb.AppendLine($"=== ROOT: {root.gameObject.name} ===");
                    Walk(root, 0, sb);
                    sb.AppendLine();
                }

                var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "OldenPedia");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "style_probe.txt");
                File.WriteAllText(path, sb.ToString());
                Plugin.Log.LogInfo($"[style] written to {path}");
            }
            catch (Exception ex) { Plugin.Log.LogError($"[style] dump failed: {ex}"); }
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            try
            {
                var go = t.gameObject;
                if (!go.activeInHierarchy) return; // only what's actually visible right now
                string indent = new string(' ', depth * 2);
                string line = indent + go.name;

                var img = go.GetComponent<Image>();
                if (img != null && img.sprite != null)
                {
                    string spriteName = SafeName(img.sprite);
                    string col = Hex(img.color);
                    var rt = go.GetComponent<RectTransform>();
                    Vector2 size = rt != null ? rt.rect.size : Vector2.zero;
                    bool bigEnough = size.x > 80f || size.y > 80f;
                    bool sliced = img.type == Image.Type.Sliced || img.type == Image.Type.Tiled;
                    string tag = (bigEnough && sliced) ? " [BG?]" : "";
                    line += $"  <Image sprite='{spriteName}' type={img.type} color=#{col} size={size.x:0}x{size.y:0}{tag}>";
                }

                var tmp = go.GetComponent<TMPro.TMP_Text>();
                if (tmp != null && !string.IsNullOrEmpty(tmp.text))
                {
                    string fontName = tmp.font != null ? SafeName(tmp.font) : "?";
                    string col = Hex(tmp.color);
                    string preview = tmp.text.Replace("\n", " ").Trim();
                    if (preview.Length > 30) preview = preview.Substring(0, 30) + "...";
                    line += $"  <TMP font='{fontName}' size={tmp.fontSize:0.#} color=#{col} style={tmp.fontStyle} text=\"{preview}\">";
                }

                sb.AppendLine(line);

                int n = t.childCount;
                for (int i = 0; i < n; i++) Walk(t.GetChild(i), depth + 1, sb);
            }
            catch (Exception ex) { sb.AppendLine(new string(' ', depth * 2) + "(err: " + ex.Message + ")"); }
        }

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.name : "null"; } catch { return "?"; }
        }

        private static string Hex(Color c)
        {
            try
            {
                int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
                int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
                int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
                int a = Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
                return $"{r:X2}{g:X2}{b:X2}{a:X2}";
            }
            catch { return "??????"; }
        }
    }
}
