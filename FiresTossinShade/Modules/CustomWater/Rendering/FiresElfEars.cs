using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Elf ears on the LOCAL player from the elf-ear pack (tossinshade bundle). Each set is a SINGLE
    /// ear mesh (split from the pack pair, pivoted at its inner base); we instance it TWICE — right
    /// (positive X scale) and left (negative X scale = mirror) — and parent each to the head attach
    /// (<c>VisEquipment.m_helmet</c>). So OffsetSide spreads the two ears, Scale sizes each, and the
    /// rotation sliders (read in the PLAYER frame, mirrored per side) angle each ear about its own base.
    /// Skin submeshes are tinted to the body skin tone; earring submeshes keep the pack material.
    /// </summary>
    public static class FiresElfEars
    {
        public static bool Enabled = false;
        public static string EarSet = "FiresEar_Pointed2";

        public static readonly string[] Sets =
        {
            "FiresEar_Pointed1", "FiresEar_Pointed2", "FiresEar_Pointed3",
            "FiresEar_Horned", "FiresEar_Blush", "FiresEar_Earringed",
        };

        // Placement: OffsetX = side (temple), OffsetY = up, OffsetZ = forward (metres). Dialled in-game.
        public static float OffsetX = 0.08f, OffsetY = 0.04f, OffsetZ = 0f;
        public static float ScaleX = 0.6f, ScaleY = 0.6f, ScaleZ = 0.6f;
        public static float Pitch = 0f, Yaw = 0f, Roll = 0f;
        public static Color EarColor = Color.white;

        private const string EarName = "FiresElfEarSide";

        private struct Rig { public GameObject[] ears; public Material[] mats; public Color skinTone; public LODGroup lod; public string set; public float baseScale; }
        private static readonly Dictionary<Transform, Rig> s_rigs = new Dictionary<Transform, Rig>();
        public static int Count => s_rigs.Count;

        private static string s_diag = "";
        private static void Diag(string m) { if (m != s_diag) { s_diag = m; Debug.Log("[FiresElfEars] " + m); } }

        public static void ApplyToLocalPlayer()
        {
            if (!Enabled) return;
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) { Diag("player not resolved yet"); return; }
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null || ve.m_helmet == null) { Diag("no helmet attach (VisEquipment.m_helmet null)"); return; }
            var helmet = ve.m_helmet;

            SweepStray(p, helmet);

            if (s_rigs.TryGetValue(helmet, out var rig) && rig.ears != null && rig.ears.Length == 2
                && rig.ears[0] != null && rig.ears[1] != null && rig.set == EarSet)
            {
                Tune(rig, 0); Tune(rig, 1);
                return;
            }

            DestroyAllRigs();
            CleanOrphans(helmet);

            var mesh = LoadEarMesh(EarSet, out var packMats);
            if (mesh == null)
            {
                var ear = System.Array.FindAll(FiresWaterShaderLoader.GetBundleAssetNames(),
                    n => n.IndexOf("firesear", StringComparison.OrdinalIgnoreCase) >= 0);
                Diag($"mesh load FAILED for '{EarSet}'. bundle ear assets ({ear.Length})");
                return;
            }

            // Read the body skin tone robustly: the shared material's _SkinColor is often 0 (Valheim sets the
            // real per-player colour via a MaterialPropertyBlock), so try the MPB too and keep whichever is
            // non-black. Fall back to a neutral skin tan so the ears are NEVER tinted to black.
            Color skinTone = new Color(0.80f, 0.62f, 0.52f, 1f);
            Material skinRef = null;
            if (ve.m_bodyModel != null)
            {
                if (ve.m_bodyModel.sharedMaterials != null && ve.m_bodyModel.sharedMaterials.Length > 0) skinRef = ve.m_bodyModel.sharedMaterials[0];
                if (skinRef != null && skinRef.HasProperty("_SkinColor")) { var c = skinRef.GetColor("_SkinColor"); if (c.maxColorComponent > 0.05f) skinTone = c; }
                try { var mpb = new MaterialPropertyBlock(); ve.m_bodyModel.GetPropertyBlock(mpb); var mc = mpb.GetColor("_SkinColor"); if (mc.maxColorComponent > 0.05f) skinTone = mc; } catch { }
            }
            skinTone.a = 1f;
            Debug.Log($"[FiresElfEars] skinTone={skinTone}");
            var skinMat = BuildSkinMaterial(skinRef);
            var mats = BuildMaterials(mesh, packMats, skinMat, skinTone);

            // Unity imports these FBXs tiny; normalize the single ear's longest dim to ~0.1m at Scale=1.
            float maxDim = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
            float baseScale = maxDim > 1e-6f ? 0.1f / maxDim : 1f;

            var r = new Rig { ears = new GameObject[2], mats = mats, skinTone = skinTone, lod = p.GetComponentInChildren<LODGroup>(), set = EarSet, baseScale = baseScale };
            r.ears[0] = CreateEar(helmet, mesh, mats);   // left  (mirrored by negative X scale in Tune)
            r.ears[1] = CreateEar(helmet, mesh, mats);   // right
            s_rigs[helmet] = r;
            Tune(r, 0); Tune(r, 1);
            Diag($"attached '{EarSet}' (L+R) baseScale={baseScale:F1} mats={mats.Length}");
        }

        private static GameObject CreateEar(Transform helmet, Mesh mesh, Material[] mats)
        {
            var go = new GameObject(EarName);
            go.transform.SetParent(helmet, false);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = mats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go;
        }

        private static Mesh LoadEarMesh(string set, out Material[] packMats)
        {
            packMats = null;
            var prefab = FiresWaterShaderLoader.LoadBundleModel(set);
            if (prefab == null) return FiresWaterShaderLoader.LoadBundleAsset<Mesh>(set);
            var mr = prefab.GetComponentInChildren<MeshRenderer>(true);
            if (mr != null) packMats = mr.sharedMaterials;
            var mf = prefab.GetComponentInChildren<MeshFilter>(true);
            return mf != null ? mf.sharedMesh : null;
        }

        private static Material[] BuildMaterials(Mesh mesh, Material[] packMats, Material skinMat, Color skinTone)
        {
            int sub = Mathf.Max(1, mesh.subMeshCount);
            var outMats = new Material[sub];
            for (int i = 0; i < sub; i++)
            {
                Material pm = (packMats != null && i < packMats.Length) ? packMats[i] : null;
                if (pm == null) { outMats[i] = skinMat; continue; }
                string pnm = pm.name.ToLowerInvariant();
                if (pnm.Contains("skin") || pnm.Contains("blush"))
                {
                    // Fresh Standard material with the pack skin texture loaded EXPLICITLY from the bundle
                    // (Unity's FBX import doesn't reliably link the embedded one). _Color stays white so the
                    // texture's own skin+pink-blush colour shows; EarColor tints it in Tune if the user wants.
                    Shader sh = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                    var c = new Material(sh) { name = "FiresEarSkin" };
                    // Only the "blush" materials have UVs that match the pack skin texture; the plain "Skin"
                    // ears shipped UNTEXTURED, so texturing them leaves stretched areas uncovered. Give plain
                    // skin a SOLID colour (from _Color in Tune) so it covers the whole ear at any scale.
                    if (pnm.Contains("blush"))
                    {
                        string texName = (pnm.Contains("blush 2") || pnm.Contains("blush2")) ? "Skin_and_Blush_2" : "Skin_and_Blush_1";
                        var tex = FiresWaterShaderLoader.LoadBundleAsset<Texture2D>(texName);
                        if (tex != null && c.HasProperty("_MainTex")) c.SetTexture("_MainTex", tex);
                    }
                    if (c.HasProperty("_Glossiness")) c.SetFloat("_Glossiness", 0.2f);
                    outMats[i] = c;   // _Color (skin tint) applied in Tune
                }
                else outMats[i] = pm;   // earring / metal: pack material as-is
            }
            return outMats;
        }

        private static Material BuildSkinMaterial(Material skinRef)
        {
            try
            {
                Shader sh = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Sprites/Default");
                var m = new Material(sh) { name = "FiresElfEar_skin" };
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.2f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0.8f, 0.6f, 0.5f, 1f));
                return m;
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresElfEars] skin material build failed: {ex.Message}"); return null; }
        }

        private static void Tune(Rig rig, int idx)
        {
            var go = rig.ears[idx]; if (go == null) return;
            var helmet = go.transform.parent; if (helmet == null) return;
            float side = idx == 1 ? 1f : -1f;   // 1 = right, -1 = left (negative X scale mirrors the ear)

            Vector3 ls = helmet.lossyScale;
            float sx = Mathf.Abs(ls.x) > 1e-4f ? ls.x : 1f;
            float sy = Mathf.Abs(ls.y) > 1e-4f ? ls.y : 1f;
            float sz = Mathf.Abs(ls.z) > 1e-4f ? ls.z : 1f;
            float b = rig.baseScale > 1e-6f ? rig.baseScale : 1f;

            go.transform.localPosition = new Vector3((OffsetX * side) / sx, OffsetY / sy, OffsetZ / sz);
            // HEAD-bone-local rotation so the ears are anchored to the head and turn WITH it (like a helmet).
            // Each ear now pivots at its own base, so this angles in place (no orbit); Yaw/Roll mirror per side.
            go.transform.localRotation = Quaternion.Euler(Pitch, Yaw * side, Roll * side);
            go.transform.localScale = new Vector3((ScaleX * side * b) / sx, (ScaleY * b) / sy, (ScaleZ * b) / sz);

            // re-tint skin materials live (EarColor changes)
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && rig.mats != null)
            {
                // Tint the ear skin to the BODY skin tone (EarColor, white by default, adjusts it further).
                Color tone = rig.skinTone * EarColor;
                for (int i = 0; i < rig.mats.Length; i++)
                {
                    var mm = rig.mats[i];
                    if (mm != null && mm.name.StartsWith("FiresEarSkin") && mm.HasProperty("_Color")) mm.SetColor("_Color", tone);
                }
            }
        }

        public static void SyncVisibility()
        {
            foreach (var kv in s_rigs)
            {
                var rig = kv.Value;
                if (rig.ears == null) continue;
                bool show = rig.lod == null || rig.lod.localReferencePoint.x < 99999f;
                for (int i = 0; i < rig.ears.Length; i++)
                {
                    if (rig.ears[i] == null) continue;
                    var mr = rig.ears[i].GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled != show) mr.enabled = show;
                }
            }
        }

        private static void SweepStray(Player p, Transform helmet)
        {
            GameObject k0 = null, k1 = null;
            if (s_rigs.TryGetValue(helmet, out var rig) && rig.ears != null && rig.ears.Length == 2) { k0 = rig.ears[0]; k1 = rig.ears[1]; }

            int killed = 0;
            var all = p.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var tr = all[i];
                if (tr == null || tr.name == null || !tr.name.StartsWith(EarName)) continue;
                if (tr.gameObject == k0 || tr.gameObject == k1) continue;
                UnityEngine.Object.Destroy(tr.gameObject);
                killed++;
            }
            if (s_rigs.Count > 0)
            {
                var stale = new List<Transform>();
                foreach (var kv in s_rigs) if (kv.Key != helmet) stale.Add(kv.Key);
                foreach (var h in stale) s_rigs.Remove(h);
            }
            if (killed > 0) Debug.Log($"[FiresElfEars] swept {killed} stray ear object(s).");
        }

        private static void DestroyAllRigs()
        {
            foreach (var kv in s_rigs)
                if (kv.Value.ears != null)
                    foreach (var go in kv.Value.ears) if (go != null) UnityEngine.Object.Destroy(go);
            s_rigs.Clear();
        }

        private static void CleanOrphans(Transform helmet)
        {
            if (helmet == null) return;
            var dead = new List<Transform>();
            for (int i = 0; i < helmet.childCount; i++)
            {
                var c = helmet.GetChild(i);
                if (c != null && c.name.StartsWith(EarName)) dead.Add(c);
            }
            foreach (var d in dead) UnityEngine.Object.Destroy(d.gameObject);
        }

        public static void Remove()
        {
            DestroyAllRigs();
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p != null) { var ve = p.GetComponent<VisEquipment>(); if (ve != null) CleanOrphans(ve.m_helmet); }
        }

        public static void ReTuneAll()
        {
            var dead = new List<Transform>();
            foreach (var kv in s_rigs)
            {
                var rig = kv.Value;
                if (kv.Key == null || rig.ears == null || rig.ears.Length != 2 || rig.ears[0] == null || rig.ears[1] == null) { dead.Add(kv.Key); continue; }
                Tune(rig, 0); Tune(rig, 1);
            }
            foreach (var d in dead) s_rigs.Remove(d);
        }
    }

    public class FiresElfEarsDriver : MonoBehaviour
    {
        private bool _cmd;
        private float _next;

        private void Update()
        {
            if (!_cmd) { try { ElfEarsCommand.Register(); _cmd = true; } catch { } }
            FiresHairBeardScale.Apply();
            if (!FiresElfEars.Enabled) return;
            FiresElfEars.SyncVisibility();
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            FiresElfEars.ApplyToLocalPlayer();
        }
    }

    public static class ElfEarsCommand
    {
        private static bool s_reg;
        public static void Register()
        {
            if (s_reg) return;
            new Terminal.ConsoleCommand("va_ears",
                "Elf ears on the local player. Usage: va_ears on|off|status | set <name> | next",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off") { FiresElfEars.Enabled = false; FiresElfEars.Remove(); args.Context.AddString("[FiresElfEars] off."); }
                    else if (a == "status") { args.Context.AddString($"[FiresElfEars] enabled={FiresElfEars.Enabled} set={FiresElfEars.EarSet} rigs={FiresElfEars.Count}."); }
                    else if (a == "set" && args.Length > 2)
                    {
                        FiresElfEars.EarSet = args[2];
                        FiresElfEars.Enabled = true; FiresElfEars.ApplyToLocalPlayer();
                        args.Context.AddString($"[FiresElfEars] set='{FiresElfEars.EarSet}'.");
                    }
                    else if (a == "next")
                    {
                        int idx = System.Array.IndexOf(FiresElfEars.Sets, FiresElfEars.EarSet);
                        FiresElfEars.EarSet = FiresElfEars.Sets[(idx + 1 + FiresElfEars.Sets.Length) % FiresElfEars.Sets.Length];
                        FiresElfEars.Enabled = true; FiresElfEars.ApplyToLocalPlayer();
                        args.Context.AddString($"[FiresElfEars] set='{FiresElfEars.EarSet}'.");
                    }
                    else { FiresElfEars.Enabled = true; FiresElfEars.ApplyToLocalPlayer(); args.Context.AddString($"[FiresElfEars] on set='{FiresElfEars.EarSet}' (rigs={FiresElfEars.Count})."); }
                }, isCheat: false);
            s_reg = true;
        }
    }
}
