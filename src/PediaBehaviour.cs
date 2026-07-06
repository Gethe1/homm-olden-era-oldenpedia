using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OldenPedia
{
    // The IL2CPP constructor pattern (IntPtr ctor) is REQUIRED for injected
    // MonoBehaviours. Il2CppInterop wires up Update automatically.
    public class PediaBehaviour : MonoBehaviour
    {
        public PediaBehaviour(IntPtr ptr) : base(ptr) { }

        private static bool _wheelLogged;
        private static bool _prewarmQueued;

        private static void EnsureUnitPreviewsQueued()
        {
            if (_prewarmQueued) return;
            if (DataExtractor.UnitRows == null || DataExtractor.UnitRows.Count == 0) return; // not loaded yet
            _prewarmQueued = true;
            var ids = new System.Collections.Generic.List<string>();
            foreach (var r in DataExtractor.UnitRows)
                if (!string.IsNullOrEmpty(r.OwnId)) ids.Add(r.OwnId);
            UnitPreviewRenderer.EnqueuePrewarm(ids);
            Plugin.Log.LogInfo($"[preview] queued {ids.Count} units for background prewarm");
        }

        private void Update()
        {
            try
            {
                // Keep the resource-bar Pedia button installed (handles HUD reloads).
                PediaButton.EnsureInstalled();
                PediaWindow.PollSearchInput();
                if (Plugin.Show3DUnitPreview)
                {
                    UnitPreviewRenderer.Tick();
                    PediaWindow.PollUnitPreview();
                    EnsureUnitPreviewsQueued();
                }

                var kb = Keyboard.current;
                var mouse = Mouse.current;
                bool wasOpen = PediaWindow.IsOpen;
                Vector2 mpos = mouse != null ? mouse.position.ReadValue() : new Vector2(0f, 0f);

                // Toggle open — '.' (rebindable) or the resource-bar button.
                if (!wasOpen)
                {
                    if (kb != null && kb[Plugin.ToggleKey].wasPressedThisFrame) PediaWindow.Toggle();
                    if (mouse != null && mouse.leftButton.wasPressedThisFrame && PediaButton.ContainsPoint(mpos))
                        PediaWindow.Toggle();
                }

                // While open: ESC / toggle closes; close button, row selection, wheel scrolling.
                // The pedia reads the NEW input system; the game's legacy reads are Harmony-blocked.
                if (wasOpen)
                {
                    bool esc = kb != null && kb[Key.Escape].wasPressedThisFrame;
                    bool tog = kb != null && kb[Plugin.ToggleKey].wasPressedThisFrame;
                    if (esc || tog) { PediaWindow.SetOpen(false); return; }

                    if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                    {
                        if (PediaWindow.CloseContains(mpos)) PediaWindow.SetOpen(false);
                        else PediaWindow.HandlePointerDown(mpos);
                    }

                    float raw = 0f;
                    if (mouse != null) { try { raw = mouse.scroll.ReadValue().y; } catch { } }
                    if (raw != 0f && !_wheelLogged) { Plugin.Log.LogInfo($"[scroll] raw wheel delta = {raw}"); _wheelLogged = true; }

                    float notches = 0f;
                    if (raw > 0.01f) notches += 1f;
                    else if (raw < -0.01f) notches -= 1f;
                    if (kb != null)
                    {
                        if (kb[Key.UpArrow].wasPressedThisFrame) notches += 1f;
                        if (kb[Key.DownArrow].wasPressedThisFrame) notches -= 1f;
                        if (kb[Key.PageUp].wasPressedThisFrame) notches += 4f;
                        if (kb[Key.PageDown].wasPressedThisFrame) notches -= 4f;
                    }
                    if (notches != 0f) PediaWindow.Tick(mpos, notches);
                }



                // Dev recon probes.
                if (kb != null)
                {
                    if (kb[Key.L].wasPressedThisFrame) LangSettingProbe.Dump();
                    if (kb[Key.Y].wasPressedThisFrame) StyleProbe.Dump();
                    if (kb[Key.F2].wasPressedThisFrame) AbilityProbe.Dump();
                    if (kb[Key.F3].wasPressedThisFrame) DataExtractor.DumpUnits();
                    if (kb[Key.F4].wasPressedThisFrame) TypeDetailProbe.DumpTypeDetails();
                    if (kb[Key.F5].wasPressedThisFrame) ContainerProbe.DumpConfigContainers();
                    if (kb[Key.F6].wasPressedThisFrame) TypeProbe.DumpContentTypes();
                    if (kb[Key.F7].wasPressedThisFrame) DataProbe.DumpMessagePackSchema();
                    if (kb[Key.F8].wasPressedThisFrame) GameDataProbe.DumpScriptableObjectCensus();
                    if (kb[Key.F9].wasPressedThisFrame) UiProbe.DumpMainScreenUi();
                    if (kb[Key.F10].wasPressedThisFrame) LocProbe.Dump();
                    if (kb[Key.F11].wasPressedThisFrame) LangLoader.Probe();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Input read failed: {ex.Message}");
            }
        }
    }
}
