using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Object = UnityEngine.Object;

namespace OldenPedia
{
    /// <summary>
    /// Installs a Pedia button at the right end of the HUD resource bar. The
    /// button is added to ResContainer but marked ignoreLayout and anchored just
    /// past the bar's right edge, so it does NOT participate in the bar's
    /// HorizontalLayoutGroup/ContentSizeFitter (which would otherwise resize and
    /// distort the bar). It shows a round combat-style bubble with a large '?'.
    /// </summary>
    public static class PediaButton
    {
        private const string ButtonName = "OldenPediaButton";
        private static RectTransform _rect;
        private static int _cooldown;
        private static Sprite _bubble;

        public static bool Alive => _rect != null;

        public static void EnsureInstalled()
        {
            if (_rect != null) return;
            if (_cooldown-- > 0) return;
            _cooldown = 60;
            try
            {
                var container = FindResContainer();
                if (container == null) return;
                var existing = container.Find(ButtonName);
                if (existing != null) { _rect = existing.TryCast<RectTransform>(); return; }
                Build(container);
                Plugin.Log.LogInfo("Pedia button installed on resource bar.");
            }
            catch (Exception ex) { Plugin.Log.LogError($"Pedia button install failed: {ex.Message}"); }
        }

        private static Transform FindResContainer()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || c.name != "CanvasResHUDPanel") continue;
                var t = FindDeep(c.transform, "ResContainer");
                if (t != null) return t;
            }
            return null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            int n = root.childCount;
            for (int i = 0; i < n; i++)
            {
                var ch = root.GetChild(i);
                if (ch.name == name) return ch;
                var d = FindDeep(ch, name);
                if (d != null) return d;
            }
            return null;
        }

        private static void Build(Transform container)
        {
            TMP_FontAsset font = null;
            var sample = FindDeep(container, "Amount");
            if (sample != null)
            {
                var stmp = sample.GetComponent<TextMeshProUGUI>();
                if (stmp != null) font = stmp.font;
            }

            // Height of a real resource slot, so we match the bar.
            float h = 46f;
            var slotT = FindDeep(container, "ContainerResGold");
            var slotRT = slotT != null ? slotT.TryCast<RectTransform>() : container.TryCast<RectTransform>();
            if (slotRT != null && slotRT.rect.height > 1f) h = slotRT.rect.height;

            var go = new GameObject(ButtonName);
            _rect = go.AddComponent<RectTransform>();
            go.transform.SetParent(container, false);

            // CRITICAL: keep out of the bar's layout so it can't distort it.
            var le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            _rect.anchorMin = new Vector2(1f, 0.5f);
            _rect.anchorMax = new Vector2(1f, 0.5f);
            _rect.pivot = new Vector2(0f, 0.5f);
            _rect.sizeDelta = new Vector2(h, h);
            _rect.anchoredPosition = new Vector2(8f, 0f);

            // Round bubble, a touch larger than the slot so the '?' reads big.
            float bub = h * 1.15f;
            var bubGo = new GameObject("Bubble");
            var brt = bubGo.AddComponent<RectTransform>();
            bubGo.transform.SetParent(go.transform, false);
            brt.anchorMin = new Vector2(0.5f, 0.5f); brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.sizeDelta = new Vector2(bub, bub);
            brt.anchoredPosition = new Vector2(0f, 0f);
            var bImg = bubGo.AddComponent<Image>();
            var bubbleSprite = Bubble();
            if (bubbleSprite != null) { bImg.sprite = bubbleSprite; bImg.preserveAspect = true; bImg.color = new Color(1f, 1f, 1f, 1f); }
            else bImg.color = new Color(0.12f, 0.09f, 0.05f, 0.92f);
            bImg.raycastTarget = true;

            var txtGo = new GameObject("Label");
            var trt = txtGo.AddComponent<RectTransform>();
            txtGo.transform.SetParent(bubGo.transform, false);
            trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(0f, -4f); trt.offsetMax = new Vector2(0f, 4f);
            var tmp = txtGo.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = "?";
            tmp.fontSize = h * 1.25f;        // ~2x the previous glyph
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.98f, 0.88f, 0.58f, 1f);
            tmp.raycastTarget = false;
        }

        private static Sprite Bubble()
        {
            if (_bubble != null) return _bubble;
            try
            {
                const int s = 96;
                float c = (s - 1) / 2f, rOut = c, rIn = c - 7f;
                var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
                var fill = new Color(0.12f, 0.09f, 0.05f, 0.94f);
                var ring = new Color(0.82f, 0.66f, 0.32f, 1f);
                var clear = new Color(0f, 0f, 0f, 0f);
                for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                    {
                        float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                        tex.SetPixel(x, y, d <= rIn ? fill : (d <= rOut ? ring : clear));
                    }
                tex.Apply();
                _bubble = Sprite.Create(tex, new Rect(0f, 0f, s, s), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception ex) { Plugin.Log.LogError($"bubble gen: {ex.Message}"); }
            return _bubble;
        }

        public static bool ContainsPoint(Vector2 screenPos)
        {
            if (_rect == null) return false;
            try
            {
                Camera cam = null;
                var canvas = _rect.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
                    if (root.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = root.worldCamera != null ? root.worldCamera : Camera.main;
                }
                return RectTransformUtility.RectangleContainsScreenPoint(_rect, screenPos, cam);
            }
            catch { return false; }
        }
    }
}
