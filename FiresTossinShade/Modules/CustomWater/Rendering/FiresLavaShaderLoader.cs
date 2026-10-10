using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Replaces vanilla Ashlands lava (materials bound to <c>Custom/FlowOpaque</c>) with our
    /// reconstructed <c>Custom/FiresFlowOpaque</c> shader, and points its crust/flow textures
    /// at the bundled <c>FireLavaCrust</c> / <c>FireLavaFlow</c>. Reuses the same
    /// <c>tossinshade</c> bundle that <see cref="FiresWaterShaderLoader"/> already mounts.
    ///
    /// The swap mutates the SHARED lava material's shader + its _MainTex/_FoamTex (caching the
    /// originals so <see cref="Restore"/> can revert). Mutating the shared material means every
    /// lava instance flips at once and new instances inherit the swap for free.
    /// </summary>
    public static class FiresLavaShaderLoader
    {
        // On by default — the point is to replace vanilla lava. Toggle live with `va_lava off`.
        public static bool Enabled = true;

        public const string ShaderName        = "Custom/FiresFlowOpaque";
        public const string VanillaLavaShader = "Custom/FlowOpaque";
        public const string CrustTexAsset     = "FireLavaCrust";
        public const string FlowTexAsset      = "FireLavaFlow";

        private static Shader s_shader;
        private static Texture s_crust, s_flow;
        private static bool s_attempted;

        private struct Orig { public Shader shader; public Texture main; public Texture foam; public Texture emisMap; public Color emisColor; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();

        private static readonly int Prop_MainTex   = Shader.PropertyToID("_MainTex");
        private static readonly int Prop_FoamTex   = Shader.PropertyToID("_FoamTex");
        private static readonly int Prop_EmisMap   = Shader.PropertyToID("_EmissionMap");
        private static readonly int Prop_EmisColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int Prop_FoamColor = Shader.PropertyToID("_FoamColor");
        private static readonly int Prop_FoamSpeed = Shader.PropertyToID("_FoamSpeed");
        private static readonly int Prop_Metallic  = Shader.PropertyToID("_Metallic");
        private static readonly int Prop_Gloss     = Shader.PropertyToID("_Glossiness");

        // Live tuning pushed from config (FiresAdminTerrain.ApplyFiresLavaConfig). Defaults preserve
        // the vanilla look (boost 1, glow from vanilla map). ApplyTuningToAll re-applies to all swapped.
        public static float TuneEmissionBoost   = 1f;
        public static bool  TuneGlowFromTexture = false;
        public static float TuneFlowSpeed       = 1f;
        public static Color TuneMoltenColor     = Color.white;
        public static float TuneMetallic        = 0f;
        public static float TuneSmoothness      = 0.2f;

        public static int SwappedCount => s_swapped.Count;

        /// <summary>Resolve our shader + the two textures from the shared bundle. Idempotent.</summary>
        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attempted) return null;
            s_attempted = true;

            try
            {
                s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                var bundle = FiresWaterShaderLoader.Bundle;
                if (bundle != null)
                {
                    s_crust = LoadTex(bundle, CrustTexAsset);
                    s_flow  = LoadTex(bundle, FlowTexAsset);
                }

                if (s_shader == null)
                    Debug.LogWarning($"[FiresLava] shader '{ShaderName}' not in bundle yet — lava swap inert.");
                else
                    FiresLogger.LogVerbose($"[FiresLava] loaded '{ShaderName}' (crust tex={(s_crust != null)}, flow tex={(s_flow != null)}).");
                return s_shader;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresLava] EnsureLoaded threw: {ex}");
                return null;
            }
        }

        private static Texture LoadTex(AssetBundle b, string name)
        {
            var t = b.LoadAsset<Texture2D>(name);
            if (t != null) return t;
            var sp = b.LoadAsset<Sprite>(name);
            if (sp != null && sp.texture != null) return sp.texture;
            foreach (var x in b.LoadAllAssets<Texture>())
                if (x != null && x.name == name) return x;
            return null;
        }

        /// <summary>Swap every currently-loaded material on the vanilla lava shader. Dedup'd + restorable.</summary>
        public static int ApplyToAllLava()
        {
            if (!Enabled) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;

            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || !ShaderNames.Is(mat.shader, VanillaLavaShader)) continue;
                if (TrySwap(mat, shader)) n++;
            }
            if (n > 0) FiresLogger.LogVerbose($"[FiresLava] swapped {n} lava material(s) to '{ShaderName}'.");
            return n;
        }

        private static bool TrySwap(Material mat, Shader shader)
        {
            if (s_swapped.ContainsKey(mat)) return false;
            try
            {
                var orig = new Orig
                {
                    shader    = mat.shader,
                    main      = mat.HasProperty(Prop_MainTex)   ? mat.GetTexture(Prop_MainTex)   : null,
                    foam      = mat.HasProperty(Prop_FoamTex)   ? mat.GetTexture(Prop_FoamTex)   : null,
                    emisMap   = mat.HasProperty(Prop_EmisMap)   ? mat.GetTexture(Prop_EmisMap)   : null,
                    emisColor = mat.HasProperty(Prop_EmisColor) ? mat.GetColor(Prop_EmisColor)   : Color.black
                };
                s_swapped[mat] = orig;
                mat.shader = shader;
                if (s_crust != null && mat.HasProperty(Prop_MainTex)) mat.SetTexture(Prop_MainTex, s_crust);
                if (s_flow  != null && mat.HasProperty(Prop_FoamTex)) mat.SetTexture(Prop_FoamTex, s_flow);
                ApplyTuning(mat, orig);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresLava] swap failed on '{mat.name}': {ex.Message}");
                s_swapped.Remove(mat);
                return false;
            }
        }

        /// <summary>Revert every swapped lava material to its original shader, textures + emission.</summary>
        public static int Restore()
        {
            int r = 0;
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                try
                {
                    mat.shader = kv.Value.shader;
                    if (mat.HasProperty(Prop_MainTex))   mat.SetTexture(Prop_MainTex, kv.Value.main);
                    if (mat.HasProperty(Prop_FoamTex))   mat.SetTexture(Prop_FoamTex, kv.Value.foam);
                    if (mat.HasProperty(Prop_EmisMap))   mat.SetTexture(Prop_EmisMap, kv.Value.emisMap);
                    if (mat.HasProperty(Prop_EmisColor)) mat.SetColor(Prop_EmisColor, kv.Value.emisColor);
                    r++;
                }
                catch { }
            }
            s_swapped.Clear();
            if (r > 0) Debug.Log($"[FiresLava] restored {r} lava material(s).");
            return r;
        }

        /// <summary>Re-push the current Tune* values onto every already-swapped material (config changed).</summary>
        public static void ApplyTuningToAll()
        {
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null || !ShaderNames.Is(mat.shader, ShaderName)) continue;
                ApplyTuning(mat, kv.Value);
            }
        }

        // Push the live tuning onto one swapped material. Emission is either the vanilla map scaled by
        // boost, or our FireLavaFlow texture (glow-from-our-texture) — both tinted by EmissionBoost.
        private static void ApplyTuning(Material mat, Orig orig)
        {
            if (mat.HasProperty(Prop_FoamSpeed)) mat.SetFloat(Prop_FoamSpeed, TuneFlowSpeed);
            if (mat.HasProperty(Prop_Metallic))  mat.SetFloat(Prop_Metallic, TuneMetallic);
            if (mat.HasProperty(Prop_Gloss))     mat.SetFloat(Prop_Gloss, TuneSmoothness);
            if (mat.HasProperty(Prop_FoamColor)) mat.SetColor(Prop_FoamColor, TuneMoltenColor);

            if (mat.HasProperty(Prop_EmisMap))
                mat.SetTexture(Prop_EmisMap, TuneGlowFromTexture && s_flow != null ? s_flow : orig.emisMap);
            if (mat.HasProperty(Prop_EmisColor))
            {
                Color baseC = TuneGlowFromTexture ? Color.white : orig.emisColor;
                mat.SetColor(Prop_EmisColor, baseC * TuneEmissionBoost);
            }
        }
    }

    /// <summary>
    /// Drives the lava swap: registers the <c>va_lava</c> command once the Terminal exists, then
    /// re-scans for lava materials on a cadence so lava that streams in later (entering Ashlands)
    /// is caught without a per-prefab hook. Fast cadence until the first swap lands, then a slow
    /// safety net — the shared material stays swapped, so re-scans are mostly no-ops after that.
    /// </summary>
    public class FiresLavaDriver : MonoBehaviour
    {
        private bool _cmdRegistered;
        private bool _found;
        private float _next;

        private void Update()
        {
            if (!_cmdRegistered)
            {
                try { LavaSwapCommand.Register(); _cmdRegistered = true; }
                catch { /* Terminal type not ready yet; retry next frame */ }
            }

            if (!FiresLavaShaderLoader.Enabled) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + (_found ? 60f : 5f);
            if (ZNetScene.instance == null) return;

            if (FiresLavaShaderLoader.ApplyToAllLava() > 0 || FiresLavaShaderLoader.SwappedCount > 0)
                _found = true;
        }
    }

    /// <summary><c>va_lava on|off|status</c> — toggle the lava swap live for A/B testing.</summary>
    public static class LavaSwapCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand(
                "va_lava",
                "Replace Ashlands lava with Custom/FiresFlowOpaque. Usage: va_lava on|off|status",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off")
                    {
                        FiresLavaShaderLoader.Enabled = false;
                        int r = FiresLavaShaderLoader.Restore();
                        args.Context.AddString($"[FiresLava] disabled; restored {r} material(s).");
                    }
                    else if (a == "status")
                    {
                        args.Context.AddString($"[FiresLava] enabled={FiresLavaShaderLoader.Enabled}, swapped={FiresLavaShaderLoader.SwappedCount}.");
                    }
                    else
                    {
                        FiresLavaShaderLoader.Enabled = true;
                        int n = FiresLavaShaderLoader.ApplyToAllLava();
                        args.Context.AddString($"[FiresLava] enabled; swapped {n} new material(s) (total {FiresLavaShaderLoader.SwappedCount}).");
                    }
                },
                isCheat: false);
            s_registered = true;
        }
    }
}
