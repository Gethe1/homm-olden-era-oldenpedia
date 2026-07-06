using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using Object = UnityEngine.Object;

namespace OldenPedia
{
    /// <summary>
    /// Encyclopedia window. Procedurally drawn square panel. Top-left dropdown
    /// switches category (Units / Heroes / Artifacts). Master–detail body: left
    /// index (faction accordion for grouped categories, flat list otherwise),
    /// right detail with a tab per upgrade variant. Input polled by PediaBehaviour.
    /// </summary>
    public static class PediaWindow
    {
        private struct Entry { public bool Header; public int G; public int Fam; }

        private static GameObject _root, _dropList;
        private static RectTransform _leftViewport, _leftContent, _closeRect, _tabBar, _dropBtn, _rightViewport, _rightContent;
        private static RectTransform _portrait;
        private static Image _portraitImg;
        private static RectTransform _unitPreview;
        private static RawImage _unitPreviewImg;
        private static float _detailW = 320f;
        private static TextMeshProUGUI _detail, _dropLabel, _searchLabel;
        private static RectTransform _searchBox;
        private static string _searchText = "";
        private static bool _searchFocused;
        private static bool _searchInputSubscribed;
        private static TMP_FontAsset _font;

        private static readonly List<bool> _expanded = new List<bool>();
        private static readonly List<Entry> _rowEntries = new List<Entry>();
        private static readonly List<RectTransform> _rowRects = new List<RectTransform>();
        private static readonly List<Image> _rowBgs = new List<Image>();
        private static readonly List<RectTransform> _tabRects = new List<RectTransform>();
        private static readonly List<Image> _tabBgs = new List<Image>();
        private static readonly List<RectTransform> _dropRects = new List<RectTransform>();

        private static int _cat = 0, _selG = -1, _selFam = -1, _variant = 0;
        private static float _scrollY, _rightScrollY, _leftContentH;
        private static bool _open, _dropOpen;

        private const float RowH = 30f;
        // #D9C08E confirmed via the Y-probe (tavern screen's own accent/label color).
        private static readonly Color Gold = new Color(0.851f, 0.753f, 0.557f, 1f);
        private static readonly Color HeaderBg = new Color(0.55f, 0.43f, 0.16f, 0.40f);
        private static readonly Color RowNormal = new Color(0f, 0f, 0f, 0f);
        private static readonly Color RowSelected = new Color(0.55f, 0.43f, 0.16f, 0.6f);
        private static readonly Color TabNormal = new Color(0.15f, 0.13f, 0.09f, 0.9f);
        private static readonly Color TabSelected = new Color(0.55f, 0.43f, 0.16f, 0.9f);
        private static readonly Color DropBg = new Color(0.12f, 0.10f, 0.07f, 0.98f);

        public static bool IsOpen => _open;
        public static void Toggle() => SetOpen(!_open);

        public static void SetOpen(bool open)
        {
            try
            {
                if (open && _root == null) Build();
                _open = open;
                if (_root != null) _root.SetActive(open);
                if (open) { Populate(); InputBlocker.Block(); }
                else InputBlocker.Unblock();
            }
            catch (Exception ex) { Plugin.Log.LogError($"PediaWindow.SetOpen: {ex}"); }
        }

        public static void Tick(Vector2 pointer, float notches)
        {
            if (!_open || notches == 0f) return;
            const float StepPerNotch = RowH * 2.5f; // ~2.5 rows per notch
            float delta = notches * StepPerNotch;
            try
            {
                if (_leftViewport != null && _leftContent != null &&
                    RectTransformUtility.RectangleContainsScreenPoint(_leftViewport, pointer, null))
                {
                    float max = Mathf.Max(0f, _leftContentH - _leftViewport.rect.height);
                    _scrollY = Mathf.Clamp(_scrollY - delta, 0f, max);
                    _leftContent.anchoredPosition = new Vector2(0f, _scrollY);
                }
                else if (_rightViewport != null && _rightContent != null &&
                         RectTransformUtility.RectangleContainsScreenPoint(_rightViewport, pointer, null))
                {
                    float max = Mathf.Max(0f, _rightContent.rect.height - _rightViewport.rect.height);
                    _rightScrollY = Mathf.Clamp(_rightScrollY - delta, 0f, max);
                    _rightContent.anchoredPosition = new Vector2(0f, _rightScrollY);
                }
            }
            catch { }
        }

        public static bool CloseContains(Vector2 p)
        {
            if (_closeRect == null) return false;
            try { return RectTransformUtility.RectangleContainsScreenPoint(_closeRect, p, null); }
            catch { return false; }
        }

        public static void HandlePointerDown(Vector2 p)
        {
            try
            {
                // Search box focus.
                if (_searchBox != null && RectTransformUtility.RectangleContainsScreenPoint(_searchBox, p, null))
                { _searchFocused = true; return; }
                _searchFocused = false; // clicking anywhere else drops focus

                // Dropdown button.
                if (_dropBtn != null && RectTransformUtility.RectangleContainsScreenPoint(_dropBtn, p, null))
                { ToggleDropdown(); return; }

                // Open dropdown items.
                if (_dropOpen)
                {
                    for (int i = 0; i < _dropRects.Count; i++)
                        if (_dropRects[i] != null && RectTransformUtility.RectangleContainsScreenPoint(_dropRects[i], p, null))
                        { SwitchCategory(i); return; }
                    ToggleDropdown(); // click elsewhere closes it
                }

                // Variant tabs.
                for (int i = 0; i < _tabRects.Count; i++)
                    if (_tabRects[i] != null && RectTransformUtility.RectangleContainsScreenPoint(_tabRects[i], p, null))
                    { SetVariant(i); return; }

                // Index rows.
                for (int i = 0; i < _rowRects.Count; i++)
                {
                    if (_rowRects[i] == null) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(_rowRects[i], p, null)) continue;
                    var e = _rowEntries[i];
                    if (e.Header) { _expanded[e.G] = !_expanded[e.G]; RebuildIndex(); }
                    else SelectFamily(e.G, e.Fam);
                    return;
                }
            }
            catch (Exception ex) { Plugin.Log.LogError($"PediaWindow.HandlePointerDown: {ex}"); }
        }

        // ---- category / dropdown ----

        private static List<DataExtractor.Category> Cats => DataExtractor.Categories;
        private static DataExtractor.Category Cat =>
            (_cat >= 0 && _cat < Cats.Count) ? Cats[_cat] : null;

        private static void Populate()
        {
            DataExtractor.EnsureLoaded();
            if (Cats.Count == 0 || _leftContent == null) return;
            if (_cat < 0 || _cat >= Cats.Count) _cat = 0;
            SwitchCategory(_cat);
        }

        private static void ToggleDropdown()
        {
            _dropOpen = !_dropOpen;
            if (_dropList != null) _dropList.SetActive(_dropOpen);
        }

        private static void SwitchCategory(int c)
        {
            if (c < 0 || c >= Cats.Count) return;
            _cat = c;
            _dropOpen = false;
            if (_dropList != null) _dropList.SetActive(false);
            var cat = Cat;
            if (_dropLabel != null) _dropLabel.text = $"{cat.Name}  \u25BE";

            _expanded.Clear();
            for (int i = 0; i < cat.Groups.Count; i++) _expanded.Add(!cat.Grouped); // flat = always open
            if (cat.Grouped && cat.Groups.Count > 0) _expanded[0] = true;

            _selG = -1; _selFam = -1; _variant = 0; _scrollY = 0f;
            RebuildIndex();
            SelectFirst();
        }

        private static void SelectFirst()
        {
            var cat = Cat; if (cat == null) return;
            for (int g = 0; g < cat.Groups.Count; g++)
                if (_expanded[g] && cat.Groups[g].Families.Count > 0) { SelectFamily(g, 0); return; }
            RefreshDetail();
        }

        // ---- index ----

        // ---- search ----

        // onTextInput isn't exposed in this game's Keyboard interop binding (IL2CPP
        // interop only generates members the game itself references). Falls back
        // to direct per-key polling instead — the same proven-safe pattern already
        // used for Backspace/Escape elsewhere in this file, just extended to
        // letters/digits/space/a few punctuation marks. Search is case-insensitive,
        // so shift is only used to decide "is this a real keypress", not casing.
        private static readonly Key[] SearchLetterKeys =
        {
            Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H, Key.I, Key.J, Key.K, Key.L, Key.M,
            Key.N, Key.O, Key.P, Key.Q, Key.R, Key.S, Key.T, Key.U, Key.V, Key.W, Key.X, Key.Y, Key.Z
        };
        private static readonly Key[] SearchDigitKeys =
        {
            Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4,
            Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9
        };

        private static void EnsureSearchInputSubscribed() { /* no-op: search text now comes from per-frame key polling */ }

        // Called every frame while open (see PediaBehaviour).
        public static void PollSearchInput()
        {
            if (!_open || !_searchFocused) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            bool changed = false;

            for (int i = 0; i < SearchLetterKeys.Length; i++)
                if (kb[SearchLetterKeys[i]].wasPressedThisFrame) { AppendSearchChar((char)('a' + i)); changed = true; }
            for (int i = 0; i < SearchDigitKeys.Length; i++)
                if (kb[SearchDigitKeys[i]].wasPressedThisFrame) { AppendSearchChar((char)('0' + i)); changed = true; }
            if (kb[Key.Space].wasPressedThisFrame) { AppendSearchChar(' '); changed = true; }
            if (kb[Key.Minus].wasPressedThisFrame) { AppendSearchChar('-'); changed = true; }
            if (kb[Key.Quote].wasPressedThisFrame) { AppendSearchChar('\''); changed = true; }
            if (kb[Key.Period].wasPressedThisFrame) { AppendSearchChar('.'); changed = true; }

            if (kb[Key.Backspace].wasPressedThisFrame && _searchText.Length > 0)
            {
                _searchText = _searchText.Substring(0, _searchText.Length - 1);
                changed = true;
            }
            if (kb[Key.Escape].wasPressedThisFrame && _searchText.Length > 0)
            {
                _searchText = "";
                changed = true;
            }

            if (changed) { UpdateSearchLabel(); RebuildIndex(); }
        }

        private static void AppendSearchChar(char c)
        {
            if (_searchText.Length > 60) return; // sane cap
            _searchText += c;
        }

        private static void UpdateSearchLabel()
        {
            if (_searchLabel == null) return;
            if (string.IsNullOrEmpty(_searchText))
            {
                _searchLabel.text = "Search...";
                _searchLabel.color = new Color(0.6f, 0.58f, 0.52f, 1f);
            }
            else
            {
                _searchLabel.text = _searchText;
                _searchLabel.color = new Color(0.937f, 0.937f, 0.937f, 1f);
            }
        }

        private static bool MatchesSearch(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void RebuildIndex()
        {
            try
            {
                var cat = Cat; if (cat == null) return;
                for (int i = _leftContent.childCount - 1; i >= 0; i--)
                    Object.Destroy(_leftContent.GetChild(i).gameObject);
                _rowRects.Clear(); _rowBgs.Clear(); _rowEntries.Clear();

                _scratch.Clear();
                bool searching = !string.IsNullOrEmpty(_searchText);
                for (int g = 0; g < cat.Groups.Count; g++)
                {
                    var grp = cat.Groups[g];
                    if (!searching)
                    {
                        if (cat.Grouped) _scratch.Add(new Entry { Header = true, G = g, Fam = -1 });
                        if (_expanded[g])
                            for (int fam = 0; fam < grp.Families.Count; fam++)
                                _scratch.Add(new Entry { Header = false, G = g, Fam = fam });
                        continue;
                    }

                    // Searching: skip groups with no match at all; auto-expand any
                    // group that does match (ignoring manual expand/collapse state)
                    // so results are visible without an extra click.
                    bool groupNameMatches = MatchesSearch(grp.Name);
                    var matchingFams = new List<int>();
                    for (int fam = 0; fam < grp.Families.Count; fam++)
                    {
                        string disp = grp.Families[fam].Variants.Count > 0 ? grp.Families[fam].Variants[0].Display : grp.Families[fam].Key;
                        if (groupNameMatches || MatchesSearch(disp)) matchingFams.Add(fam);
                    }
                    if (matchingFams.Count == 0) continue;

                    if (cat.Grouped) _scratch.Add(new Entry { Header = true, G = g, Fam = -1 });
                    foreach (var fam in matchingFams) _scratch.Add(new Entry { Header = false, G = g, Fam = fam });
                }

                for (int i = 0; i < _scratch.Count; i++)
                {
                    var e = _scratch[i];
                    var row = NewUI("Row", _leftContent);
                    row.anchorMin = new Vector2(0f, 1f); row.anchorMax = new Vector2(1f, 1f); row.pivot = new Vector2(0.5f, 1f);
                    row.sizeDelta = new Vector2(0f, RowH);
                    row.anchoredPosition = new Vector2(0f, -i * RowH);
                    var bg = row.gameObject.AddComponent<Image>();
                    bg.raycastTarget = true;

                    var lab = NewUI("T", row);
                    lab.anchorMin = new Vector2(0f, 0f); lab.anchorMax = new Vector2(1f, 1f);
                    var t = lab.gameObject.AddComponent<TextMeshProUGUI>();
                    if (_font != null) t.font = _font;
                    t.raycastTarget = false; t.alignment = TextAlignmentOptions.Left;

                    if (e.Header)
                    {
                        var grp = cat.Groups[e.G];
                        bg.color = HeaderBg;
                        lab.offsetMin = new Vector2(10f, 0f); lab.offsetMax = new Vector2(-6f, 0f);
                        t.text = $"{(_expanded[e.G] ? "[-]" : "[+]")} {Cap(grp.Name)}  ({grp.Count})";
                        t.fontSize = 18f; t.color = new Color(0.97f, 0.88f, 0.6f, 1f);
                    }
                    else
                    {
                        var fam = cat.Groups[e.G].Families[e.Fam];
                        bool sel = (e.G == _selG && e.Fam == _selFam);
                        bg.color = sel ? RowSelected : RowNormal;
                        lab.offsetMin = new Vector2(cat.Grouped ? 30f : 12f, 0f); lab.offsetMax = new Vector2(-6f, 0f);
                        string disp = fam.Variants.Count > 0 ? fam.Variants[0].Display : fam.Key;
                        string tag = fam.Variants.Count > 1 ? $"  (+{fam.Variants.Count - 1})" : "";
                        t.text = disp + tag;
                        t.fontSize = 17f; t.color = new Color(0.88f, 0.85f, 0.78f, 1f);
                    }

                    _rowRects.Add(row); _rowBgs.Add(bg); _rowEntries.Add(e);
                }

                _leftContent.sizeDelta = new Vector2(0f, _scratch.Count * RowH);
                _leftContentH = _scratch.Count * RowH;
                _scrollY = Mathf.Clamp(_scrollY, 0f, Mathf.Max(0f, _leftContentH - _leftViewport.rect.height));
                _leftContent.anchoredPosition = new Vector2(0f, _scrollY);
            }
            catch (Exception ex) { Plugin.Log.LogError($"PediaWindow.RebuildIndex: {ex}"); }
        }
        private static readonly List<Entry> _scratch = new List<Entry>();

        private static void SelectFamily(int g, int fam)
        {
            var cat = Cat; if (cat == null) return;
            if (g < 0 || g >= cat.Groups.Count) return;
            if (fam < 0 || fam >= cat.Groups[g].Families.Count) return;
            _selG = g; _selFam = fam; _variant = 0;

            for (int i = 0; i < _rowEntries.Count; i++)
            {
                var e = _rowEntries[i];
                if (e.Header || _rowBgs[i] == null) continue;
                _rowBgs[i].color = (e.G == g && e.Fam == fam) ? RowSelected : RowNormal;
            }
            RebuildTabs();
            RefreshDetail();
        }

        private static void SetVariant(int v)
        {
            var fam = CurrentFamily();
            if (fam == null || v < 0 || v >= fam.Variants.Count) return;
            _variant = v;
            for (int i = 0; i < _tabBgs.Count; i++)
                if (_tabBgs[i] != null) _tabBgs[i].color = (i == v) ? TabSelected : TabNormal;
            RefreshDetail();
        }

        private static DataExtractor.ItemFamily CurrentFamily()
        {
            var cat = Cat; if (cat == null) return null;
            if (_selG < 0 || _selG >= cat.Groups.Count) return null;
            if (_selFam < 0 || _selFam >= cat.Groups[_selG].Families.Count) return null;
            return cat.Groups[_selG].Families[_selFam];
        }

        private static void RebuildTabs()
        {
            if (_tabBar == null) return;
            for (int i = _tabBar.childCount - 1; i >= 0; i--)
                Object.Destroy(_tabBar.GetChild(i).gameObject);
            _tabRects.Clear(); _tabBgs.Clear();

            var fam = CurrentFamily();
            if (fam == null || fam.Variants.Count <= 1) return; // tabs only when there are variants

            const float tw = 168f, th = 34f, gap = 8f;
            for (int i = 0; i < fam.Variants.Count; i++)
            {
                var tab = NewUI("Tab", _tabBar);
                tab.anchorMin = new Vector2(0f, 1f); tab.anchorMax = new Vector2(0f, 1f); tab.pivot = new Vector2(0f, 1f);
                tab.sizeDelta = new Vector2(tw, th);
                tab.anchoredPosition = new Vector2(i * (tw + gap), 0f);
                var bg = tab.gameObject.AddComponent<Image>();
                var tabSprite = IconLoader.Get("TextField_Big");
                if (tabSprite != null) { bg.sprite = tabSprite; bg.type = Image.Type.Sliced; }
                bg.color = (i == _variant) ? TabSelected : TabNormal; bg.raycastTarget = true;

                var lab = NewUI("T", tab); StretchFull(lab);
                lab.offsetMin = new Vector2(18f, 0f); lab.offsetMax = new Vector2(-18f, 0f);
                var t = lab.gameObject.AddComponent<TextMeshProUGUI>();
                if (_font != null) t.font = _font;
                t.text = VariantLabel(fam.Variants[i]);
                t.fontSize = 16f; t.alignment = TextAlignmentOptions.Center;
                t.color = new Color(0.95f, 0.92f, 0.85f, 1f); t.raycastTarget = false;

                _tabRects.Add(tab); _tabBgs.Add(bg);
            }
        }

        private static string VariantLabel(DataExtractor.PediaItem it)
        {
            if (!string.IsNullOrEmpty(it.TabLabel)) return it.TabLabel;
            string id = it.Id ?? "";
            if (id.EndsWith("_upg_alt", StringComparison.Ordinal)) return "Upgrade II";
            if (id.EndsWith("_upg", StringComparison.Ordinal)) return "Upgrade I";
            return "Base";
        }

        private static void RefreshDetail()
        {
            var fam = CurrentFamily();
            if (_detail == null) { return; }
            if (_portrait != null) _portrait.gameObject.SetActive(false);
            if (_unitPreview != null) _unitPreview.gameObject.SetActive(false);
            if (fam == null || fam.Variants.Count == 0) { _detail.text = ""; _detail.margin = Vector4.zero; return; }
            if (_variant < 0 || _variant >= fam.Variants.Count) _variant = 0;
            var it = fam.Variants[_variant];

            // Reserve a right margin equal to whatever portrait/preview will show,
            // so text (title/stats especially) never runs under it; the description
            // ("legend") still flows in that same margined column, which avoids any
            // overlap at the cost of not being truly full-width once a portrait ends.
            float reservedRight = 0f;
            bool willShowSpritePortrait = _portraitImg != null && !string.IsNullOrEmpty(it.IconKey) && IconLoader.Get(it.IconKey) != null;
            bool willShowUnitPreview = Cat != null && Cat.Name == "Units" && Plugin.Show3DUnitPreview;
            if (willShowSpritePortrait || willShowUnitPreview)
                reservedRight = Mathf.Max(96f, _detailW * 0.40f) + 20f;
            _detail.margin = new Vector4(0f, 0f, reservedRight, 0f);

            _detail.text = Detail(it);
            if (_portraitImg != null && !string.IsNullOrEmpty(it.IconKey))
            {
                var sp = IconLoader.Get(it.IconKey);
                if (sp != null)
                {
                    _portraitImg.sprite = sp;
                    float w = Mathf.Max(96f, _detailW * 0.40f);
                    float ar = 1f;
                    var r = sp.rect;
                    if (r.width > 0f && r.height > 0f) ar = r.height / r.width;
                    _portrait.sizeDelta = new Vector2(w, w * ar);
                    _portrait.gameObject.SetActive(true);
                }
            }
            _rightScrollY = 0f;
            if (_rightContent != null) _rightContent.anchoredPosition = new Vector2(0f, 0f);
            if (Cat != null && Cat.Name == "Units")
            {
                UnitModel.Probe(it.Id);
                _currentPreviewUnitId = it.Id;
                if (Plugin.Show3DUnitPreview) TryShowUnitPreview(it.Id);
            }
            else _currentPreviewUnitId = null;
        }

        private static string _currentPreviewUnitId;

        private static void TryShowUnitPreview(string ownId)
        {
            if (_unitPreviewImg == null || string.IsNullOrEmpty(ownId)) return;
            var tex = UnitPreviewRenderer.GetPreview(ownId);
            if (tex == null) return;
            _unitPreviewImg.texture = tex;
            float w = Mathf.Max(96f, _detailW * 0.40f);
            _unitPreview.sizeDelta = new Vector2(w, w); // render texture is square
            _unitPreview.gameObject.SetActive(true);
        }

        // Called every frame while open (see PediaBehaviour). A preview that's
        // still capturing when RefreshDetail ran will finish a few frames later —
        // this polls so it appears without needing to reopen the unit.
        public static void PollUnitPreview()
        {
            if (!_open || !Plugin.Show3DUnitPreview || string.IsNullOrEmpty(_currentPreviewUnitId)) return;
            if (_unitPreview != null && _unitPreview.gameObject.activeSelf) return; // already showing
            TryShowUnitPreview(_currentPreviewUnitId);
        }

        private static string Detail(DataExtractor.PediaItem it)
        {
            var sb = new StringBuilder();
            string title = string.IsNullOrEmpty(it.Display) ? it.Id : it.Display;
            sb.AppendLine($"<size=40><b>{title}</b></size>");
            if (Cat == null || Cat.Name != "Heroes")
                sb.AppendLine($"<size=14><color=#6f6f6f>{it.Id}</color></size>");
            if (!string.IsNullOrEmpty(it.Subtitle))
                sb.AppendLine($"<size=22><color=#c9b079>{it.Subtitle}</color></size>");
            sb.AppendLine();
            if (it.StatLabels.Count > 0)
            {
                sb.AppendLine("<size=24><mspace=0.62em>");
                int w = 0; foreach (var l in it.StatLabels) if (l.Length > w) w = l.Length;
                for (int i = 0; i < it.StatLabels.Count; i++)
                    sb.AppendLine($"<color=#c9b079>{it.StatLabels[i].PadRight(w + 1)}</color> {it.StatValues[i]}");
                sb.AppendLine("</mspace></size>");
            }
            if (!string.IsNullOrEmpty(it.Description))
            {
                sb.AppendLine();
                sb.AppendLine($"<size=18><color=#cfcabf>{it.Description}</color></size>");
            }
            if (it.StatLabels.Count == 0 && string.IsNullOrEmpty(it.Description))
                sb.AppendLine("<size=18><color=#7c7c7c>(no further data yet)</color></size>");
            return sb.ToString();
        }

        private static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        // ---- construction ----

        private static void Build()
        {
            Plugin.Log.LogInfo("PediaWindow: building");
            float sw = Screen.width, sh = Screen.height;
            float side = Mathf.Min(sw, sh) * 0.9f;
            const float pad = 18f, titleH = 48f, tabH = 40f;
            float leftW = side * 0.30f;
            _detailW = side - leftW - pad * 3f;

            _font = FindFont();

            _root = new GameObject("OldenPediaCanvas");
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            _root.AddComponent<GraphicRaycaster>();
            Object.DontDestroyOnLoad(_root);

            var bd = NewUI("Backdrop", _root.transform);
            StretchFull(bd);
            var bdImg = bd.gameObject.AddComponent<Image>();
            bdImg.color = new Color(0f, 0f, 0f, 0.6f); bdImg.raycastTarget = true;

            var panel = NewUI("Panel", _root.transform);
            panel.anchorMin = new Vector2(0.5f, 0.5f); panel.anchorMax = new Vector2(0.5f, 0.5f); panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(side, side); panel.anchoredPosition = new Vector2(0f, 0f);
            var pImg = panel.gameObject.AddComponent<Image>();
            // "universal_back" confirmed via the Y-probe as the game's own generic
            // window frame (used across many of its screens at different sizes).
            // The sprite itself has visible internal padding baked into its texture
            // (a shadow/bevel margin), so its OPAQUE area sits inside the rect we'd
            // naturally size it to — drawn as a separate, larger layer BEHIND the
            // panel (oversized by a fixed margin) so its visible frame actually
            // reaches out to meet the gold-bordered elements sitting on top of it,
            // instead of looking inset from them. Falls back to the plain drawn
            // panel if the sprite can't be found this session.
            var panelSprite = IconLoader.Get("universal_back");
            if (panelSprite != null)
            {
                const float overscan = 46f;
                var panelBg = NewUI("PanelBg", _root.transform);
                panelBg.SetSiblingIndex(panel.GetSiblingIndex()); // insert just before panel -> renders behind it
                panelBg.anchorMin = new Vector2(0.5f, 0.5f); panelBg.anchorMax = new Vector2(0.5f, 0.5f); panelBg.pivot = new Vector2(0.5f, 0.5f);
                panelBg.sizeDelta = new Vector2(side + overscan * 2f, side + overscan * 2f);
                panelBg.anchoredPosition = Vector2.zero;
                var bgImg = panelBg.gameObject.AddComponent<Image>();
                bgImg.sprite = panelSprite; bgImg.type = Image.Type.Sliced;
                bgImg.color = Color.white; bgImg.raycastTarget = true;

                pImg.color = new Color(0f, 0f, 0f, 0f); pImg.raycastTarget = false; // panel itself is now just a layout container
            }
            else
            {
                pImg.color = new Color(0.07f, 0.06f, 0.05f, 0.98f); pImg.raycastTarget = true;
                AddBorder(panel, Gold, 3f);
            }

            // Top-left category dropdown button.
            _dropBtn = NewUI("DropBtn", panel);
            _dropBtn.anchorMin = new Vector2(0f, 1f); _dropBtn.anchorMax = new Vector2(0f, 1f); _dropBtn.pivot = new Vector2(0f, 1f);
            _dropBtn.sizeDelta = new Vector2(240f, titleH - 6f);
            _dropBtn.anchoredPosition = new Vector2(pad, -pad * 0.5f);
            var dbImg = _dropBtn.gameObject.AddComponent<Image>();
            ApplyFieldStyle(_dropBtn, dbImg);
            var dlab = NewUI("T", _dropBtn); StretchFull(dlab);
            dlab.offsetMin = new Vector2(28f, 0f); dlab.offsetMax = new Vector2(-26f, 0f);
            _dropLabel = dlab.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) _dropLabel.font = _font;
            _dropLabel.text = "Units  \u25BE"; _dropLabel.fontSize = 24f; _dropLabel.alignment = TextAlignmentOptions.Left;
            _dropLabel.color = new Color(0.97f, 0.88f, 0.6f, 1f); _dropLabel.raycastTarget = false;

            // Search bar — same size as the dropdown, placed immediately to its right.
            _searchBox = NewUI("SearchBox", panel);
            _searchBox.anchorMin = new Vector2(0f, 1f); _searchBox.anchorMax = new Vector2(0f, 1f); _searchBox.pivot = new Vector2(0f, 1f);
            _searchBox.sizeDelta = new Vector2(240f, titleH - 6f);
            _searchBox.anchoredPosition = new Vector2(pad + 240f + 12f, -pad * 0.5f);
            var sbImg = _searchBox.gameObject.AddComponent<Image>();
            ApplyFieldStyle(_searchBox, sbImg);
            var slab = NewUI("T", _searchBox); StretchFull(slab);
            slab.offsetMin = new Vector2(28f, 0f); slab.offsetMax = new Vector2(-26f, 0f);
            _searchLabel = slab.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) _searchLabel.font = _font;
            _searchLabel.text = "Search..."; _searchLabel.fontSize = 20f; _searchLabel.alignment = TextAlignmentOptions.Left;
            _searchLabel.color = new Color(0.6f, 0.58f, 0.52f, 1f); _searchLabel.raycastTarget = false;
            EnsureSearchInputSubscribed();

            // Centered title header.
            var title = NewUI("Title", panel);
            title.anchorMin = new Vector2(0f, 1f); title.anchorMax = new Vector2(1f, 1f); title.pivot = new Vector2(0.5f, 1f);
            title.offsetMin = new Vector2(0f, -titleH); title.offsetMax = new Vector2(0f, -pad * 0.5f);
            var tlab = title.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) tlab.font = _font;
            tlab.text = "OldenPedia " + Plugin.Version;
            tlab.fontSize = 26f; tlab.alignment = TextAlignmentOptions.Center;
            tlab.color = new Color(0.97f, 0.90f, 0.66f, 1f); tlab.raycastTarget = false;

            // Close.
            var close = NewUI("Close", panel);
            close.anchorMin = new Vector2(1f, 1f); close.anchorMax = new Vector2(1f, 1f); close.pivot = new Vector2(1f, 1f);
            close.sizeDelta = new Vector2(titleH, titleH); close.anchoredPosition = new Vector2(-pad * 0.5f, -pad * 0.5f);
            _closeRect = close;
            var cImg = close.gameObject.AddComponent<Image>();
            // "Button_EskBig" confirmed via the Y-probe as the game's own close-button glyph.
            var closeSprite = IconLoader.Get("Button_EskBig");
            if (closeSprite != null)
            {
                cImg.sprite = closeSprite; cImg.type = Image.Type.Simple;
                cImg.color = Color.white; cImg.raycastTarget = true; cImg.preserveAspect = true;
            }
            else
            {
                cImg.color = new Color(0.5f, 0.12f, 0.10f, 0.95f); cImg.raycastTarget = true;
                var clab = NewUI("X", close); StretchFull(clab);
                var cTmp = clab.gameObject.AddComponent<TextMeshProUGUI>();
                if (_font != null) cTmp.font = _font;
                cTmp.text = "X"; cTmp.fontSize = 26f; cTmp.alignment = TextAlignmentOptions.Center;
                cTmp.color = new Color(1f, 1f, 1f, 1f); cTmp.raycastTarget = false;
            }

            float bodyTop = -(titleH + pad);

            // Left accordion.
            var leftPanel = NewUI("LeftPanel", panel);
            leftPanel.anchorMin = new Vector2(0f, 0f); leftPanel.anchorMax = new Vector2(0f, 1f); leftPanel.pivot = new Vector2(0f, 1f);
            leftPanel.offsetMin = new Vector2(pad, pad); leftPanel.offsetMax = new Vector2(pad + leftW, bodyTop);
            var lpImg = leftPanel.gameObject.AddComponent<Image>();
            lpImg.color = new Color(0f, 0f, 0f, 0.32f); lpImg.raycastTarget = true;
            AddBorder(leftPanel, Gold, 2f);

            _leftViewport = NewUI("LeftViewport", leftPanel);
            StretchFull(_leftViewport);
            _leftViewport.offsetMin = new Vector2(4f, 4f); _leftViewport.offsetMax = new Vector2(-4f, -4f);
            _leftViewport.gameObject.AddComponent<RectMask2D>();

            _leftContent = NewUI("LeftContent", _leftViewport);
            _leftContent.anchorMin = new Vector2(0f, 1f); _leftContent.anchorMax = new Vector2(1f, 1f); _leftContent.pivot = new Vector2(0.5f, 1f);
            _leftContent.anchoredPosition = new Vector2(0f, 0f); _leftContent.sizeDelta = new Vector2(0f, 0f);

            // Right detail.
            var rightPanel = NewUI("RightPanel", panel);
            rightPanel.anchorMin = new Vector2(0f, 0f); rightPanel.anchorMax = new Vector2(1f, 1f); rightPanel.pivot = new Vector2(0f, 1f);
            rightPanel.offsetMin = new Vector2(pad + leftW + pad, pad); rightPanel.offsetMax = new Vector2(-pad, bodyTop);
            var rpImg = rightPanel.gameObject.AddComponent<Image>();
            rpImg.color = new Color(0f, 0f, 0f, 0.18f); rpImg.raycastTarget = true;
            AddBorder(rightPanel, Gold, 2f);

            _tabBar = NewUI("TabBar", rightPanel);
            _tabBar.anchorMin = new Vector2(0f, 1f); _tabBar.anchorMax = new Vector2(1f, 1f); _tabBar.pivot = new Vector2(0f, 1f);
            _tabBar.offsetMin = new Vector2(20f, -tabH - 14f); _tabBar.offsetMax = new Vector2(-20f, -14f);

            _rightViewport = NewUI("RightViewport", rightPanel);
            _rightViewport.anchorMin = new Vector2(0f, 0f); _rightViewport.anchorMax = new Vector2(1f, 1f); _rightViewport.pivot = new Vector2(0.5f, 0.5f);
            _rightViewport.offsetMin = new Vector2(20f, 16f); _rightViewport.offsetMax = new Vector2(-20f, -(tabH + 28f));
            _rightViewport.gameObject.AddComponent<RectMask2D>();

            _rightContent = NewUI("RightContent", _rightViewport);
            _rightContent.anchorMin = new Vector2(0f, 1f); _rightContent.anchorMax = new Vector2(1f, 1f); _rightContent.pivot = new Vector2(0.5f, 1f);
            _rightContent.anchoredPosition = new Vector2(0f, 0f); _rightContent.sizeDelta = new Vector2(0f, 0f);
            var rcsf = _rightContent.gameObject.AddComponent<ContentSizeFitter>();
            rcsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rcsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            _detail = _rightContent.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) _detail.font = _font;
            _detail.fontSize = 22f; _detail.alignment = TextAlignmentOptions.TopLeft;
            _detail.color = new Color(0.937f, 0.937f, 0.937f, 1f); _detail.richText = true; _detail.raycastTarget = false;

            _portrait = NewUI("Portrait", rightPanel);
            _portrait.anchorMin = new Vector2(1f, 1f); _portrait.anchorMax = new Vector2(1f, 1f); _portrait.pivot = new Vector2(1f, 1f);
            _portrait.sizeDelta = new Vector2(128f, 128f);
            _portrait.anchoredPosition = new Vector2(-22f, -(tabH + 20f));
            _portraitImg = _portrait.gameObject.AddComponent<Image>();
            _portraitImg.preserveAspect = true; _portraitImg.raycastTarget = false;
            AddBorder(_portrait, Gold, 1.5f);
            _portrait.gameObject.SetActive(false);

            // Unit 3D preview (experimental, opt-in) — same slot as the portrait,
            // only one of the two is ever shown depending on category.
            _unitPreview = NewUI("UnitPreview", rightPanel);
            _unitPreview.anchorMin = new Vector2(1f, 1f); _unitPreview.anchorMax = new Vector2(1f, 1f); _unitPreview.pivot = new Vector2(1f, 1f);
            _unitPreview.sizeDelta = new Vector2(128f, 128f);
            _unitPreview.anchoredPosition = new Vector2(-22f, -(tabH + 20f));
            _unitPreviewImg = _unitPreview.gameObject.AddComponent<RawImage>();
            AddBorder(_unitPreview, Gold, 1.5f);
            _unitPreview.gameObject.SetActive(false);

            // Dropdown list (created last so it renders above the panels).
            _dropList = new GameObject("DropList").AddComponent<RectTransform>().gameObject;
            var dlrt = _dropList.GetComponent<RectTransform>();
            dlrt.SetParent(panel, false);
            dlrt.anchorMin = new Vector2(0f, 1f); dlrt.anchorMax = new Vector2(0f, 1f); dlrt.pivot = new Vector2(0f, 1f);
            dlrt.sizeDelta = new Vector2(240f, RowH * 4f + 8f);
            dlrt.anchoredPosition = new Vector2(pad, -pad * 0.5f - (titleH - 6f));
            var dlbg = _dropList.AddComponent<Image>(); dlbg.color = DropBg; dlbg.raycastTarget = true;
            AddBorder(dlrt, Gold, 2f);
            _dropRects.Clear();
            string[] names = { "Units", "Heroes", "Artifacts", "Skills" };
            for (int i = 0; i < names.Length; i++)
            {
                var item = NewUI("DItem", dlrt);
                item.anchorMin = new Vector2(0f, 1f); item.anchorMax = new Vector2(1f, 1f); item.pivot = new Vector2(0.5f, 1f);
                item.sizeDelta = new Vector2(0f, RowH);
                item.anchoredPosition = new Vector2(0f, -4f - i * RowH);
                var ibg = item.gameObject.AddComponent<Image>(); ibg.color = RowNormal; ibg.raycastTarget = true;
                var il = NewUI("T", item); StretchFull(il); il.offsetMin = new Vector2(14f, 0f);
                var it = il.gameObject.AddComponent<TextMeshProUGUI>();
                if (_font != null) it.font = _font;
                it.text = names[i]; it.fontSize = 20f; it.alignment = TextAlignmentOptions.Left;
                it.color = new Color(0.92f, 0.88f, 0.78f, 1f); it.raycastTarget = false;
                _dropRects.Add(item);
            }
            _dropList.SetActive(false);

            Plugin.Log.LogInfo($"PediaWindow: built (font={_font != null}, side={side})");
        }

        // "TextField_Big" confirmed via the Y-probe as the game's own input/dropdown
        // field background. Falls back to the plain drawn box if not found.
        private static void ApplyFieldStyle(RectTransform rect, Image img)
        {
            var sprite = IconLoader.Get("TextField_Big");
            if (sprite != null)
            {
                img.sprite = sprite; img.type = Image.Type.Sliced;
                img.color = Color.white; img.raycastTarget = true;
            }
            else
            {
                img.color = DropBg; img.raycastTarget = true;
                AddBorder(rect, Gold, 2f);
            }
        }

        private static void AddBorder(RectTransform parent, Color col, float t)
        {
            Edge(parent, col, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, t));
            Edge(parent, col, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, t));
            Edge(parent, col, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(t, 0f));
            Edge(parent, col, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(t, 0f));
        }

        private static void Edge(RectTransform parent, Color col, Vector2 aMin, Vector2 aMax, Vector2 size)
        {
            var e = NewUI("Edge", parent);
            e.anchorMin = aMin; e.anchorMax = aMax; e.pivot = new Vector2(0.5f, 0.5f);
            e.sizeDelta = size; e.anchoredPosition = new Vector2(0f, 0f);
            var img = e.gameObject.AddComponent<Image>();
            img.color = col; img.raycastTarget = false;
        }

        private static RectTransform NewUI(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
        }

        private static TMP_FontAsset FindFont()
        {
            // Confirmed via the Y-probe (tavern screen): "LT-Regular" is the
            // game's own body-text font. Fall back to whatever's loaded if not found.
            var f = FontLoader.Get("LT-Regular");
            if (f != null) return f;
            try
            {
                foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
                    if (t != null && t.font != null) return t.font;
            }
            catch { }
            return null;
        }
    }
}
