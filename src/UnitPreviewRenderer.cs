using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace OldenPedia
{
    // EXPERIMENTAL, opt-in (Plugin.Show3DUnitPreview, default OFF).
    //
    // Renders a unit's own 3D prefab ONCE — after letting its idle animation
    // settle — captures that single frame into a frozen Texture2D, and
    // immediately tears the live instance down. In-memory cache ONLY (a prior
    // build persisted captures to disk; rolled back because a stale cached file
    // from before a rendering fix would silently keep showing the old, bad
    // result even after the fix shipped — every session now renders fresh).
    // Also background-prewarms the rest of the roster (one at a time, low
    // priority) so browsing feels instant instead of needing to reopen a unit.
    // A bounded LRU cache keeps memory in check even if every unit gets viewed.
    //
    // The remaining risk isn't the Unity render setup (Camera + RenderTexture +
    // ReadPixels is standard) — it's instantiating an arbitrary GAME prefab, which
    // may carry scripts assuming they're part of the live game world.
    //
    // Mitigation: instantiate under an INACTIVE holder first (Unity defers
    // Awake/OnEnable while inactive), strip every Behaviour-except-Animator/
    // Collider/Rigidbody/ParticleSystem/AudioSource with DestroyImmediate while
    // still inactive, put it on a private layer far from any gameplay position,
    // and only THEN activate it.
    public static class UnitPreviewRenderer
    {
        private const int Layer = 30; // arbitrary; not verified unused, hence opt-in
        private static readonly Vector3 Origin = new Vector3(300000f, 0f, 300000f);
        private const int SettleFrames = 5; // frames to let Animator reach its idle pose before capturing
        private const int Resolution = 768;
        private const int CacheCap = 48; // bounds in-memory textures

        private static GameObject _root, _holder, _current;
        private static Camera _cam;
        private static RenderTexture _rt;
        private static string _pendingId;
        private static bool _pendingIsPrewarm;
        private static bool _failed;
        private static int _pendingFrames;

        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();
        private static readonly List<string> _lru = new List<string>(); // front = least recently used
        private static readonly Queue<string> _prewarmQueue = new Queue<string>();
        private static readonly HashSet<string> _prewarmQueued = new HashSet<string>();
        private static readonly HashSet<string> _failedIds = new HashSet<string>();

        // Returns the frozen shot if already captured; otherwise kicks off a
        // capture (if not already in progress for this id) and returns null until
        // it's ready (poll again — see PediaWindow's per-frame re-check). In-memory
        // only (no disk persistence) — a stale cached file from before a rendering
        // fix would otherwise mask that the fix ever happened.
        public static Texture GetPreview(string ownId)
        {
            if (_failed || string.IsNullOrEmpty(ownId) || _failedIds.Contains(ownId)) return null;
            if (_cache.TryGetValue(ownId, out var cached)) { Touch(ownId); return cached; }

            try
            {
                EnsureSetup();
                if (_root == null) return null;
                if (ownId == _pendingId) { _pendingIsPrewarm = false; return null; } // already in flight; promote to player-priority

                StartCapture(ownId, false);
                return null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[preview] GetPreview " + ownId + ": " + ex.Message);
                _failed = true; // one failure disables the feature for the session, not the game
                return null;
            }
        }

        // Queue every unit id for background capture at idle time, so browsing
        // doesn't need to wait once the player has looked at a few. Safe to call
        // repeatedly; already-cached/queued ids are skipped.
        public static void EnqueuePrewarm(IEnumerable<string> ownIds)
        {
            if (ownIds == null) return;
            foreach (var id in ownIds)
            {
                if (string.IsNullOrEmpty(id) || _cache.ContainsKey(id) || _prewarmQueued.Contains(id) || _failedIds.Contains(id)) continue;
                _prewarmQueue.Enqueue(id);
                _prewarmQueued.Add(id);
            }
        }

        // Call once per frame (cheap no-op when nothing pending/queued). Runs the
        // settle countdown for whatever's in flight (player-requested or prewarm),
        // captures when ready, then either serves the next player request or pulls
        // the next id from the prewarm queue.
        public static void Tick()
        {
            if (_pendingFrames > 0 && _current != null)
            {
                _pendingFrames--;
                if (_pendingFrames == 0) FinishCapture();
                return;
            }
            if (_pendingId == null && _prewarmQueue.Count > 0)
            {
                string next = _prewarmQueue.Dequeue();
                if (!_cache.ContainsKey(next) && !_failedIds.Contains(next))
                {
                    try { StartCapture(next, true); }
                    catch (Exception ex) { Plugin.Log.LogError("[preview] prewarm start " + next + ": " + ex.Message); }
                }
            }
        }

        private static void StartCapture(string ownId, bool isPrewarm)
        {
            var prefab = UnitModel.GetPrefab(ownId);
            if (prefab == null) { _failedIds.Add(ownId); return; }

            ClearCurrent();
            _current = BuildSafeInstance(prefab);
            if (_current == null) { _failedIds.Add(ownId); return; }

            FrameCamera(_current, ownId); // rough frame now; refined again right before capture
            _pendingId = ownId;
            _pendingIsPrewarm = isPrewarm;
            _pendingFrames = SettleFrames;
        }

        private static void FinishCapture()
        {
            string id = _pendingId;
            try
            {
                FrameCamera(_current, id);
                var tex = CaptureFrame();
                if (tex != null && !string.IsNullOrEmpty(id))
                {
                    _cache[id] = tex;
                    Touch(id);
                    EvictIfNeeded();
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[preview] capture " + id + ": " + ex.Message); }
            finally
            {
                ClearCurrent(); // frozen texture is cached; the live instance is no longer needed
            }
        }

        private static void Touch(string id)
        {
            _lru.Remove(id);
            _lru.Add(id);
        }

        private static void EvictIfNeeded()
        {
            while (_cache.Count > CacheCap && _lru.Count > 0)
            {
                string oldest = _lru[0];
                _lru.RemoveAt(0);
                if (_cache.TryGetValue(oldest, out var tex))
                {
                    _cache.Remove(oldest);
                    try { UnityEngine.Object.DestroyImmediate(tex); } catch { }
                }
            }
        }

        private static Texture2D CaptureFrame()
        {
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                RenderTexture.active = _rt;
                tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, _rt.width, _rt.height), 0, 0);
                tex.Apply();
            }
            finally { RenderTexture.active = prevActive; }
            return tex;
        }

        private static void EnsureSetup()
        {
            if (_root != null) return;
            try
            {
                _rt = new RenderTexture(Resolution, Resolution, 16, RenderTextureFormat.ARGB32) { name = "OldenPedia_UnitPreviewRT" };
                int msaa = Mathf.Max(1, QualitySettings.antiAliasing);
                _rt.antiAliasing = msaa;

                _root = new GameObject("OldenPedia_UnitPreviewRoot");
                _root.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _root.transform.position = Origin;

                var camGo = new GameObject("PreviewCamera");
                camGo.transform.SetParent(_root.transform, false);
                camGo.layer = Layer;
                _cam = camGo.AddComponent<Camera>();
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _cam.cullingMask = 1 << Layer;
                _cam.targetTexture = _rt;
                _cam.fieldOfView = 30f;
                _cam.nearClipPlane = 0.05f;
                _cam.farClipPlane = 1000f;
                _cam.allowMSAA = QualitySettings.antiAliasing > 0;
                _cam.allowHDR = true; // matches typical modern-pipeline defaults; safe either way for a solid-color-background capture

                var lightGo = new GameObject("PreviewLight");
                lightGo.transform.SetParent(_root.transform, false);
                lightGo.layer = Layer;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = 1 << Layer;
                light.intensity = 1.1f;
                lightGo.transform.eulerAngles = new Vector3(40f, -30f, 0f);

                _holder = new GameObject("PreviewHolder");
                _holder.transform.SetParent(_root.transform, false);
                _holder.SetActive(false); // Awake/OnEnable of children deferred while this is inactive

                string qName = "?";
                try { int lvl = QualitySettings.GetQualityLevel(); var names = QualitySettings.names; qName = (names != null && lvl >= 0 && lvl < names.Length) ? names[lvl] : lvl.ToString(); } catch { }
                Plugin.Log.LogInfo($"[preview] setup ok (layer {Layer}, res {Resolution}, quality='{qName}', msaa={msaa}, textureLimit={QualitySettings.masterTextureLimit}, aniso={QualitySettings.anisotropicFiltering})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[preview] setup failed: " + ex.Message);
                _failed = true; _root = null;
            }
        }

        // Instantiate the prefab under the still-inactive holder, strip anything
        // that isn't pure render data, THEN activate.
        private static GameObject BuildSafeInstance(GameObject prefab)
        {
            GameObject inst = null;
            try
            {
                inst = UnityEngine.Object.Instantiate(prefab, _holder.transform);
                inst.transform.localPosition = new Vector3(0f, 0f, 0f);
                inst.transform.localRotation = new Quaternion(0f, 0f, 0f, 1f); // identity

                StripBehavioursExceptAnimator(inst); // keep Animator so it plays its default/idle pose instead of the bind (T) pose
                StripAll<Collider>(inst);
                StripAll<Rigidbody>(inst);
                StripAll<ParticleSystem>(inst);
                StripAll<AudioSource>(inst);

                SetLayerRecursive(inst.transform, Layer);
                ForceHighestLod(inst);

                inst.SetActive(true);
                _holder.SetActive(true);
                return inst;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[preview] instance build failed: " + ex.Message);
                try { if (inst != null) UnityEngine.Object.DestroyImmediate(inst); } catch { }
                return null;
            }
        }

        // If the model uses an LODGroup, Unity picks the mesh/material detail level
        // based on how much of the CAMERA'S view it fills — at our framing distance
        // that can land on a lower LOD than the game's own close-up shows, which
        // would explain "resolution doesn't change quality" (the RENDER texture
        // was fine; the SOURCE mesh/material was already low-detail). Forcing LOD 0
        // always uses the best available mesh/textures regardless of our distance.
        private static void ForceHighestLod(GameObject inst)
        {
            try
            {
                var arr = inst.GetComponentsInChildren(Il2CppType.Of<LODGroup>(), true);
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var lg = arr[i].TryCast<LODGroup>();
                    if (lg == null) continue;
                    lg.ForceLOD(0);
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[preview] ForceHighestLod: " + ex.Message); }
        }

        // Same as StripAll<Behaviour> but keeps Animator, so the model settles into
        // its default/idle animation state instead of freezing in the T-pose.
        // Slight added risk versus stripping everything: if the animator controller
        // uses StateMachineBehaviours (custom per-state script logic), those DO run.
        // Animation Events on missing targets are safe no-ops (Unity SendMessage),
        // so a stripped-away script being referenced by name won't throw.
        private static void StripBehavioursExceptAnimator(GameObject root)
        {
            try
            {
                var arr = root.GetComponentsInChildren(Il2CppType.Of<Behaviour>(), true);
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i];
                    if (c == null) continue;
                    if (c.TryCast<Animator>() != null) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch { }
                }
            }
            catch { }
        }

        private static void StripAll<T>(GameObject root) where T : Component
        {
            try
            {
                var arr = root.GetComponentsInChildren(Il2CppType.Of<T>(), true);
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var c = arr[i];
                    if (c == null) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); } catch { }
                }
            }
            catch { }
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            try
            {
                t.gameObject.layer = layer;
                for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
            }
            catch { }
        }

        private static int _boundsLog = 0;

        private static void FrameCamera(GameObject inst, string ownId)
        {
            try
            {
                var renderers = inst.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                bool has = false;
                Bounds b = new Bounds(inst.transform.position, new Vector3(1f, 1f, 1f));
                int rendererCount = 0;
                for (int i = 0; i < (renderers != null ? renderers.Length : 0); i++)
                {
                    var r = renderers[i].TryCast<Renderer>();
                    if (r == null) continue;
                    rendererCount++;
                    if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
                }
                if (!has) b = new Bounds(inst.transform.position, new Vector3(2f, 2f, 2f));

                // Proper trig-based fit instead of a diagonal-magnitude guess: sizes
                // to the larger of width/height so nothing gets clipped regardless
                // of body shape (tall skeleton vs. wide dragon), with a fixed margin.
                float halfSize = Mathf.Max(Mathf.Max(b.extents.x, b.extents.y), 0.3f);
                float fovRad = _cam.fieldOfView * Mathf.Deg2Rad * 0.5f;
                float dist = halfSize / Mathf.Tan(fovRad) * 1.35f;

                Vector3 dir = new Vector3(0f, 0.2f, 1f).normalized;
                _cam.transform.position = b.center - dir * dist;
                _cam.transform.LookAt(b.center);

                // Diagnostic for chasing the remaining off-center/miscentered units:
                // logs each unit's computed bounds once, so a reported bad unit's
                // numbers can be checked for an outlier (e.g. a stray VFX renderer
                // pulling the box off the body).
                if (_boundsLog < 60)
                {
                    _boundsLog++;
                    Plugin.Log.LogInfo($"[preview] {ownId} bounds center={b.center - inst.transform.position} size={b.size} renderers={rendererCount}");
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[preview] frame camera " + ownId + ": " + ex.Message); }
        }

        private static void ClearCurrent()
        {
            try { if (_current != null) UnityEngine.Object.DestroyImmediate(_current); } catch { }
            _current = null; _pendingId = null; _pendingFrames = 0; _pendingIsPrewarm = false;
        }
    }
}
