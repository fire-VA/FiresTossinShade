using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Swaps the LOCAL player's visual for a custom model rigged to the EXACT Valheim player skeleton.
    /// The custom model (from the embedded `firesplayermodels` bundle) carries bones named identically to
    /// the live skeleton; each of its SkinnedMeshRenderers is REBOUND onto the live player bones by name,
    /// so the player's own animator deforms the custom mesh directly — no retarget, no Humanoid avatar,
    /// no per-frame pose copy. The vanilla body + gear renderers are hidden while swapped.
    /// </summary>
    public static class FiresPlayerModel
    {
        public static bool Enabled = false;
        public static bool ShowInFirstPerson = false;  // showing her in FP puts her body AT the camera (blocks it); cull by default

        private const string BundleResourceName = "firesplayermodels";
        private static AssetBundle s_bundle;
        private static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();  // every model in the bundle, by name
        public static string ActiveModelName = null;   // which model to swap in; null/blank = first available
        private static Texture2D s_texture;
        private static bool s_triedLoad;

        private static GameObject s_instance;
        private static Transform s_playerRoot;
        private static Animator s_anim;
        private static LODGroup s_lod;
        private static float s_nextLog;
        private static readonly HashSet<Renderer> s_hidden = new HashSet<Renderer>();   // set, not list — the game re-enables renderers each frame; dedup stops unbounded growth

        private static void EnsureBundle()
        {
            if (s_triedLoad) return;
            s_triedLoad = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith(BundleResourceName, System.StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
                if (resName == null) { Debug.LogWarning("[FiresPlayerModel] embedded bundle not found: " + BundleResourceName); return; }
                using (var st = asm.GetManifestResourceStream(resName))
                {
                    if (st == null) { Debug.LogWarning("[FiresPlayerModel] resource stream null: " + resName); return; }
                    s_bundle = AssetBundle.LoadFromStream(st);
                }
                if (s_bundle == null) { Debug.LogWarning("[FiresPlayerModel] LoadFromStream returned null"); return; }
                foreach (var a in s_bundle.LoadAllAssets())
                {
                    if (a is GameObject go) { if (!s_prefabs.ContainsKey(go.name)) s_prefabs[go.name] = go; }   // every model prefab, by name
                    else if (s_texture == null && a is Texture2D tx) s_texture = tx;
                }
                Debug.Log($"[FiresPlayerModel] bundle models=[{string.Join(",", s_prefabs.Keys)}] active={(string.IsNullOrEmpty(ActiveModelName) ? "(first)" : ActiveModelName)} tex={(s_texture != null)}");
            }
            catch (System.Exception e) { Debug.LogWarning("[FiresPlayerModel] bundle load failed: " + e.Message); }
        }

        // The active model prefab from the bundle (by ActiveModelName, else the first one loaded).
        private static GameObject ActivePrefab()
        {
            EnsureBundle();
            if (s_prefabs.Count == 0) return null;
            if (!string.IsNullOrEmpty(ActiveModelName) && s_prefabs.TryGetValue(ActiveModelName, out var go)) return go;
            foreach (var kv in s_prefabs) return kv.Value;   // first available
            return null;
        }

        // Switch the active model. If already swapped in, hot-swap to the new one immediately (no rebuild).
        public static void SetModel(string name)
        {
            ActiveModelName = string.IsNullOrEmpty(name) ? null : name;
            if (Enabled && s_instance != null) { Restore(); ApplyToLocalPlayer(); }
        }

        public static List<string> AvailableModels() { EnsureBundle(); var l = new List<string>(s_prefabs.Keys); l.Sort(); return l; }   // sorted = stable order for the config index slider

        // Called each Update; sets the swap up once (guarded), no-op when already swapped or disabled.
        public static void ApplyToLocalPlayer()
        {
            if (!Enabled || s_instance != null) return;
            var prefab = ActivePrefab();
            if (prefab == null) return;

            if (Player.m_localPlayer == null) return;   // in-game only — the menu preview grabbed duplicate bones + hid the whole menu scene
            var p = Player.m_localPlayer.gameObject;     // scope to the PLAYER, not the scene (the old resolver was hiding the whole streaming world)

            // Authoritative skeleton = the vanilla body's OWN bones (the SMR with the most bones), not a
            // hierarchy walk — a walk can grab duplicate-named transforms (ragdoll/attach) and fling verts.
            SkinnedMeshRenderer bodySMR = null;
            foreach (var smr in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.bones != null && (bodySMR == null || smr.bones.Length > bodySMR.bones.Length)) bodySMR = smr;
            if (bodySMR == null || bodySMR.bones.Length < 20) { Debug.LogWarning("[FiresPlayerModel] vanilla body skeleton not ready yet"); return; }
            var live = new Dictionary<string, Transform>();
            foreach (var b in bodySMR.bones) if (b != null && !live.ContainsKey(b.name)) live[b.name] = b;
            if (bodySMR.rootBone != null && !live.ContainsKey(bodySMR.rootBone.name)) live[bodySMR.rootBone.name] = bodySMR.rootBone;
            Debug.Log($"[FiresPlayerModel] live skeleton '{bodySMR.name}' bones={bodySMR.bones.Length} root={(bodySMR.rootBone != null ? bodySMR.rootBone.name : "?")} rootScale={(bodySMR.rootBone != null ? bodySMR.rootBone.lossyScale.ToString("F3") : "?")}");

            // Hiding the vanilla body makes Unity's Animator cull itself (no visible renderer it owns) and FREEZE
            // the bone transforms — so the rebound mesh wouldn't animate. Force it to always update the skeleton.
            s_anim = p.GetComponentInChildren<Animator>();
            if (s_anim != null) s_anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            s_playerRoot = p.transform;
            s_lod = p.GetComponentInChildren<LODGroup>();
            s_instance = Object.Instantiate(prefab, p.transform);
            s_instance.name = "FiresPlayerModel_Custom";
            s_instance.transform.localPosition = Vector3.zero;
            s_instance.transform.localRotation = Quaternion.identity;
            var ia = s_instance.GetComponentInChildren<Animator>();   // its own animator would fight the live skeleton
            if (ia != null) Object.Destroy(ia);
            // Safety net: the bundle strips cameras/lights at build time, but re-strip at runtime so a stale or
            // mis-built bundle can NEVER hijack the view. Cheap runtime op; the user is fine with these.
            foreach (var c in s_instance.GetComponentsInChildren<Camera>(true)) Object.Destroy(c);
            foreach (var lt in s_instance.GetComponentsInChildren<Light>(true)) Object.Destroy(lt);

            // Auto-scale her to the live body's height — custom meshes import a touch short, so match the player
            // exactly (done BEFORE the rebind so the recomputed bindposes pick up the scaled transform).
            var herSMR0 = s_instance.GetComponentInChildren<SkinnedMeshRenderer>();
            float liveH = bodySMR.bounds.size.y, herH = herSMR0 != null ? herSMR0.bounds.size.y : 0f;
            if (herH > 0.1f && liveH > 0.1f)
            {
                float sc = liveH / herH;
                s_instance.transform.localScale = new Vector3(sc, sc, sc);
                Debug.Log($"[FiresPlayerModel] auto-scale to live body: {herH:F2} -> {liveH:F2} (x{sc:F3})");
            }

            // Each live bone's BIND (rest) world-to-local, derived from the vanilla body's bindposes — so we bind
            // the model to the player's REST pose, NOT whatever idle frame the skeleton is caught in at swap.
            // (Binding to the idle frame is what made A-pose models read "arms pinned back".) The bone's rest world
            // = bodyLocalToWorld * bindpose⁻¹, so its rest worldToLocal = bindpose * bodyLocalToWorld⁻¹.
            var liveBindW2L = new Dictionary<string, Matrix4x4>();
            var bodyMesh = bodySMR.sharedMesh;
            if (bodyMesh != null && bodyMesh.bindposes != null && bodyMesh.bindposes.Length >= bodySMR.bones.Length)
            {
                var bps = bodyMesh.bindposes; var bL2Winv = bodySMR.localToWorldMatrix.inverse;
                for (int j = 0; j < bodySMR.bones.Length; j++)
                    if (bodySMR.bones[j] != null && !liveBindW2L.ContainsKey(bodySMR.bones[j].name))
                        liveBindW2L[bodySMR.bones[j].name] = bps[j] * bL2Winv;
            }

            // REBIND each custom SMR onto the live bones by name → the player's animator drives the custom mesh, and
            // armor/held items (which rivet to these same vanilla bones) ride along correctly.
            int matched = 0, total = 0;
            var unmatched = new List<string>();
            foreach (var msmr in s_instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = msmr.bones;
                var dst = new Transform[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    Transform t = null;
                    if (src[i] != null)
                    {
                        var bn = src[i].name;
                        // Mixamo-rigged models prefix "mixamorig:"; Valheim's bones are the same names without it.
                        if (!live.TryGetValue(bn, out t) && bn.StartsWith("mixamorig:")) live.TryGetValue(bn.Substring(10), out t);
                        // Leaf bones the live skeleton lacks (finger tips, IK helpers) → nearest matched ancestor.
                        var anc = src[i].parent;
                        while (t == null && anc != null)
                        {
                            var an = anc.name;
                            if (!live.TryGetValue(an, out t) && an.StartsWith("mixamorig:")) live.TryGetValue(an.Substring(10), out t);
                            anc = anc.parent;
                        }
                    }
                    if (t != null) { dst[i] = t; matched++; }
                    else { dst[i] = src[i]; if (src[i] != null && unmatched.Count < 12) unmatched.Add(src[i].name); }
                    total++;
                }
                msmr.bones = dst;
                Transform rb = null;
                if (msmr.rootBone != null) live.TryGetValue(msmr.rootBone.name, out rb);
                if (rb == null) live.TryGetValue("Hips", out rb);
                if (rb != null) msmr.rootBone = rb;
                msmr.updateWhenOffscreen = true;

                // Bindposes from the live REST pose (above) so the model's A-pose lines up to vanilla's neutral;
                // fall back to the bone's current matrix for any bone the body's bindposes don't cover (finger tips).
                var bp = new Matrix4x4[dst.Length];
                var l2w = msmr.localToWorldMatrix;
                for (int i = 0; i < dst.Length; i++)
                {
                    if (dst[i] != null && liveBindW2L.TryGetValue(dst[i].name, out var rw2l)) bp[i] = rw2l * l2w;
                    else bp[i] = (dst[i] != null ? dst[i].worldToLocalMatrix : Matrix4x4.identity) * l2w;
                }
                var meshCopy = Object.Instantiate(msmr.sharedMesh);
                meshCopy.bindposes = bp;
                msmr.sharedMesh = meshCopy;
            }
            Debug.Log($"[FiresPlayerModel] rebind(bind-pose) matched {matched}/{total} bones, restBones={liveBindW2L.Count}" + (unmatched.Count > 0 ? " UNMATCHED: " + string.Join(",", unmatched) : ""));

            // DIAGNOSTIC: compare live skeleton vs Minri's bind bones (position + scale) for a few key bones —
            // a big position/scale gap = the bindpose-vs-live mismatch (Balrond's "visual scale" gotcha) that explodes the mesh.
            foreach (var bn in new[] { "Hips", "Spine", "LeftUpLeg", "LeftArm" })
            {
                live.TryGetValue(bn, out var lt);
                var mt = FindDeep(s_instance.transform, bn) ?? FindDeep(s_instance.transform, "mixamorig:" + bn);
                Debug.Log($"[FiresPlayerModel]   {bn}: live=" + (lt != null ? lt.position.ToString("F2") + " s" + lt.lossyScale.ToString("F3") : "MISSING")
                                              + " | model=" + (mt != null ? mt.position.ToString("F2") + " s" + mt.lossyScale.ToString("F3") : "MISSING"));
            }
            foreach (var sm in s_instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Debug.Log($"[FiresPlayerModel]   SMR={sm.name} boundsSize={sm.bounds.size.ToString("F2")} smrScale={sm.transform.lossyScale.ToString("F3")}");

            // The bundle already baked the material (a clone of the real player body material + her texture). Just
            // re-point its shader at the LIVE shader so we don't render with the stripped-variant copy the bundle ships.
            foreach (var r in s_instance.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null) { var liveShader = Shader.Find(m.shader.name); if (liveShader != null) m.shader = liveShader; }

            // SNAPSHOT + hide the CURRENT vanilla renderers (the body + hair). Armor and held items equipped LATER
            // are NEW renderers not in this snapshot, so HideVanilla leaves them visible — and since they rivet to
            // the same live bones the custom mesh is bound to, they render correctly on her.
            s_hidden.Clear();
            foreach (var r in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.enabled && !r.transform.IsChildOf(s_instance.transform)) { r.enabled = false; s_hidden.Add(r); }
            foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true))
                if (r.enabled && !r.transform.IsChildOf(s_instance.transform)) { r.enabled = false; s_hidden.Add(r); }
            Debug.Log($"[FiresPlayerModel] swapped in custom model (rebind); hid {s_hidden.Count} vanilla renderers (gear added later stays visible).");
        }

        // Called each LateUpdate — the custom mesh deforms automatically off the live bones, so this only
        // re-hides re-equipped vanilla renderers and culls the model in first-person.
        public static void SyncPose()
        {
            if (s_instance == null || s_playerRoot == null) return;
            if (s_anim != null && s_anim.cullingMode != AnimatorCullingMode.AlwaysAnimate) s_anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            HideVanilla();

            bool fp = !ShowInFirstPerson && s_lod != null && s_lod.localReferencePoint.x > 99999f;
            foreach (var r in s_instance.GetComponentsInChildren<Renderer>(true))
                if (r.enabled == fp) r.enabled = !fp;

            if (Time.time >= s_nextLog)
            {
                Debug.Log($"[FiresPlayerModel] active hidden={s_hidden.Count} fpCull={fp}");
                s_nextLog = Time.time + 5f;
            }
        }

        // Re-hide only the SNAPSHOT taken at swap (the vanilla body + hair). The game re-enables the body each
        // frame, so we keep disabling it — but we no longer touch armor/held items, which were added after the
        // snapshot and must stay visible.
        private static void HideVanilla()
        {
            foreach (var r in s_hidden) if (r != null && r.enabled) r.enabled = false;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { var r = FindDeep(root.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        // Swaps every renderer's material to the named shader (+ re-applies the texture). Falls back through
        // Custom/Creature → Standard. Exposed via `va_model shader <name>` for live material iteration.
        public static void ApplyShader(string name)
        {
            if (s_instance == null) return;
            var sh = Shader.Find(name) ?? Shader.Find("Custom/Creature") ?? Shader.Find("Standard");
            foreach (var r in s_instance.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.materials)
                {
                    if (m == null) continue;
                    if (sh != null) m.shader = sh;
                    if (s_texture != null) { m.mainTexture = s_texture; if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", s_texture); }
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.1f);
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                }
            Debug.Log($"[FiresPlayerModel] shader -> {(sh != null ? sh.name : "none")}");
        }

        public static void Restore()
        {
            foreach (var r in s_hidden) if (r != null) r.enabled = true;
            s_hidden.Clear();
            if (s_instance != null) Object.Destroy(s_instance);
            s_instance = null; s_playerRoot = null; s_anim = null;
        }
    }

    public static class FiresPlayerModelCommand
    {
        private static bool s_reg;
        public static void Register()
        {
            if (s_reg) return;
            new Terminal.ConsoleCommand("va_model",
                "Swap your character for a custom model. Usage: va_model on|off|status|list|use <name>|shader <name>",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off") { FiresPlayerModel.Enabled = false; FiresPlayerModel.Restore(); args.Context.AddString("[FiresPlayerModel] off (restored)."); }
                    else if (a == "status") { args.Context.AddString($"[FiresPlayerModel] enabled={FiresPlayerModel.Enabled} model={(string.IsNullOrEmpty(FiresPlayerModel.ActiveModelName) ? "(first)" : FiresPlayerModel.ActiveModelName)}."); }
                    else if (a == "list") { args.Context.AddString("[FiresPlayerModel] models: " + string.Join(", ", FiresPlayerModel.AvailableModels())); }
                    else if (a == "use" || a == "model") { string n = args.Length > 2 ? args[2] : ""; FiresPlayerModel.SetModel(n); FiresPlayerModel.Enabled = true; FiresPlayerModel.ApplyToLocalPlayer(); args.Context.AddString("[FiresPlayerModel] model -> " + (string.IsNullOrEmpty(n) ? "(first)" : n)); }
                    else if (a == "shader") { string sh = args.Length > 2 ? args[2] : "Custom/Player"; FiresPlayerModel.ApplyShader(sh); args.Context.AddString("[FiresPlayerModel] shader -> " + sh); }
                    else { FiresPlayerModel.Enabled = true; FiresPlayerModel.ApplyToLocalPlayer(); args.Context.AddString("[FiresPlayerModel] on (custom model)."); }
                }, isCheat: false, optionsFetcher: () => FiresPlayerModel.AvailableModels());
            s_reg = true;
        }
    }
}
