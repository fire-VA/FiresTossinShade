using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Replaces vanilla <c>Custom/LitParticles</c> (the Mistlands "heavymist" mist) with our verbatim
    /// reconstruction <c>Custom/FiresLitParticles</c>. VALUE-PRESERVING: the mist material keeps all its
    /// own data (_MainTex soft_cloud_round, _NormalTex soft_cloud_normal, _Color, the fade distances,
    /// _AlphaChannel, etc.) - we only change the shader, and FiresLitParticles declares the same property
    /// names so everything carries over.
    ///
    /// Scoped to materials whose name contains "mist" (heavymist / heavymist_mistlands_small_lux), NOT
    /// every Custom/LitParticles material (ground_cloud, dust, wildfire) - keeps the blast radius on the
    /// mist while the reconstruction is verified. The mist material is a shared asset loaded when the
    /// Mistlands/Ashlands stream in, so the driver re-scans every few seconds to catch it.
    /// </summary>
    public static class FiresLitParticlesShaderLoader
    {
        // Off by default - opt in (mirrors the heightmap swap).
        public static bool Enabled = false;

        public const string ShaderName            = "Custom/FiresLitParticles";
        public const string VanillaParticleShader = "Custom/LitParticles";

        private static Shader s_shader;
        private static bool s_attempted;

        private struct Orig { public Shader shader; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();
        private static readonly HashSet<int> s_notMist = new HashSet<int>();

        // Live tuning pushed from config (FiresAdminTerrain). Defaults are NO-OPs => vanilla mist.
        // ApplyTuningToAll re-applies to every swapped material on each config change / poll tick.
        public static Color TuneTint       = Color.white;
        public static float TuneBrightness = 1f;   // lit *= this
        public static float TuneDensity    = 1f;   // final alpha *= this
        public static float TuneFadeScale  = 1f;   // camera-fade reach scale
        public static float TuneEdgePower  = 1f;   // alpha edge softness/sharpness
        public static float TuneLightScale = 1f;   // light-response scale
        public static float TuneAltitude   = 30f;  // mist floor altitude
        public static float TunePatchiness = 0.55f; // patchy-density amount (fog-ify default: coverage modulates the mist)
        public static float TunePatchScale = 0.05f; // patch noise frequency
        // Tier-2 fog-ify defaults — ACTIVE so `va_mist on` shows the fog-ified mist. Set these to no-op
        // (WindDrift 0, PushStrength 0, Coverage 0, TwoTone 0, Patchiness 0) via config for pure vanilla-exact.
        public static float TuneWindDrift    = 0.02f;  // coverage drifts along _GlobalWind1
        public static float TunePushStrength = 0.6f;   // mist parts/flows around the player
        public static float TunePushRadius   = 18f;
        public static float TuneCoverage     = 1f;      // 0 = simple patch noise, 1 = richer banks+wisps coverage
        public static float TuneCoverScale   = 0.015f;
        public static Color TuneDenseTint    = new Color(0.82f, 0.88f, 1f, 1f); // cooler dense cores (two-tone)
        public static float TuneTwoTone      = 0.45f;

        // Live WEATHER modulation, driven by FiresFogEnvReactive each EnvMan.SetEnv (no-op defaults => identical to the
        // static-tuned mist). Tune* are the config BASE; these blend env weather ON TOP, exactly like the volumetric fog.
        public static float WeatherDensityMul = 1f;          // multiplies base density (thicken in fog/rain/storms)
        public static Color WeatherEnvFog     = Color.white; // env's blended fog hue
        public static float WeatherColorBlend = 0f;          // 0..1 how far the tint shifts toward WeatherEnvFog
        private static readonly int Prop_Tint    = Shader.PropertyToID("_MistTint");
        private static readonly int Prop_Bright  = Shader.PropertyToID("_MistBrightness");
        private static readonly int Prop_Density = Shader.PropertyToID("_MistDensity");
        private static readonly int Prop_Fade    = Shader.PropertyToID("_MistFadeScale");
        private static readonly int Prop_Edge    = Shader.PropertyToID("_MistEdgePower");
        private static readonly int Prop_Light   = Shader.PropertyToID("_MistLightScale");
        private static readonly int Prop_Alt     = Shader.PropertyToID("_MistAltitude");
        private static readonly int Prop_Patch   = Shader.PropertyToID("_MistPatchiness");
        private static readonly int Prop_PatchSc = Shader.PropertyToID("_MistPatchScale");
        private static readonly int Prop_Wind    = Shader.PropertyToID("_MistWindDrift");
        private static readonly int Prop_PushStr  = Shader.PropertyToID("_MistPushStrength");
        private static readonly int Prop_PushRad  = Shader.PropertyToID("_MistPushRadius");
        private static readonly int Prop_Cover    = Shader.PropertyToID("_MistCoverage");
        private static readonly int Prop_CoverSc  = Shader.PropertyToID("_MistCoverScale");
        private static readonly int Prop_Dense    = Shader.PropertyToID("_MistDenseTint");
        private static readonly int Prop_TwoTone  = Shader.PropertyToID("_MistTwoTone");

        // Config is the single source of truth, polled by the driver every ~1s (same as the heightmap swap).
        public static System.Action ConfigPoll;
        public static System.Action<bool> SetEnabledConfig;
        public static System.Action ResetConfig;

        public static int SwappedCount => s_swapped.Count;

        /// <summary>Resolve our shader from the shared tossinshade bundle. Idempotent.</summary>
        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attempted) return null;
            s_attempted = true;
            try
            {
                s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                if (s_shader == null)
                    Debug.LogWarning($"[FiresLitParticles] shader '{ShaderName}' not in bundle yet - mist swap inert.");
                else
                    Debug.Log($"[FiresLitParticles] loaded '{ShaderName}'.");
                return s_shader;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresLitParticles] EnsureLoaded threw: {ex}");
                return null;
            }
        }

        // Target only the heavy-mist materials (Mistlands/Ashlands mist), not every LitParticles material.
        private static bool IsMist(Material mat)
        {
            return mat.name.IndexOf("mist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Swap every currently-loaded vanilla mist material. Dedup'd + restorable.</summary>
        public static int ApplyToAll()
        {
            if (!Enabled) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;

            PruneDead();
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || !ShaderNames.Is(mat.shader, VanillaParticleShader)) continue;
                int id = mat.GetInstanceID();
                if (s_notMist.Contains(id)) continue;
                if (!IsMist(mat)) { s_notMist.Add(id); continue; }
                if (TrySwap(mat, shader)) n++;
            }
            if (n > 0) Debug.Log($"[FiresLitParticles] swapped {n} mist material(s).");
            return n;
        }

        private static bool TrySwap(Material mat, Shader shader)
        {
            if (s_swapped.ContainsKey(mat)) return false;
            try
            {
                s_swapped[mat] = new Orig { shader = mat.shader };
                mat.shader = shader;   // value-preserving: textures / colors / fade props carry over
                ApplyTuning(mat);      // seed the tuning knobs (no-op at defaults)
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresLitParticles] swap failed on '{mat.name}': {ex.Message}");
                s_swapped.Remove(mat);
                return false;
            }
        }

        /// <summary>Revert every swapped mist material to its original Custom/LitParticles shader.</summary>
        public static int Restore()
        {
            int r = 0;
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                try { mat.shader = kv.Value.shader; r++; }
                catch { }
            }
            s_swapped.Clear();
            if (r > 0) Debug.Log($"[FiresLitParticles] restored {r} mist material(s).");
            return r;
        }

        private static void ApplyTuning(Material mat)
        {
            // base (config) blended with live weather: tint shifts toward the env fog hue, density thickens in weather.
            Color tint    = Color.Lerp(TuneTint, WeatherEnvFog, Mathf.Clamp01(WeatherColorBlend));
            float density = TuneDensity * Mathf.Max(0f, WeatherDensityMul);
            if (mat.HasProperty(Prop_Tint))    mat.SetColor(Prop_Tint, tint);
            if (mat.HasProperty(Prop_Bright))  mat.SetFloat(Prop_Bright, TuneBrightness);
            if (mat.HasProperty(Prop_Density)) mat.SetFloat(Prop_Density, density);
            if (mat.HasProperty(Prop_Fade))    mat.SetFloat(Prop_Fade, TuneFadeScale);
            if (mat.HasProperty(Prop_Edge))    mat.SetFloat(Prop_Edge, TuneEdgePower);
            if (mat.HasProperty(Prop_Light))   mat.SetFloat(Prop_Light, TuneLightScale);
            if (mat.HasProperty(Prop_Alt))     mat.SetFloat(Prop_Alt, TuneAltitude);
            if (mat.HasProperty(Prop_Patch))   mat.SetFloat(Prop_Patch, TunePatchiness);
            if (mat.HasProperty(Prop_PatchSc)) mat.SetFloat(Prop_PatchSc, TunePatchScale);
            if (mat.HasProperty(Prop_Wind))    mat.SetFloat(Prop_Wind, TuneWindDrift);
            if (mat.HasProperty(Prop_PushStr)) mat.SetFloat(Prop_PushStr, TunePushStrength);
            if (mat.HasProperty(Prop_PushRad)) mat.SetFloat(Prop_PushRad, TunePushRadius);
            if (mat.HasProperty(Prop_Cover))   mat.SetFloat(Prop_Cover, TuneCoverage);
            if (mat.HasProperty(Prop_CoverSc)) mat.SetFloat(Prop_CoverSc, TuneCoverScale);
            if (mat.HasProperty(Prop_Dense))   mat.SetColor(Prop_Dense, TuneDenseTint);
            if (mat.HasProperty(Prop_TwoTone)) mat.SetFloat(Prop_TwoTone, TuneTwoTone);
        }

        /// <summary>Re-push the current Tune* values onto every swapped mist material (config changed).</summary>
        public static void ApplyTuningToAll()
        {
            PruneDead();
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null || !ShaderNames.Is(mat.shader, ShaderName)) continue;
                ApplyTuning(mat);
            }
        }

        private static void PruneDead()
        {
            List<Material> dead = null;
            foreach (var kv in s_swapped)
                if (kv.Key == null) (dead ??= new List<Material>()).Add(kv.Key);
            if (dead != null) foreach (var m in dead) s_swapped.Remove(m);
        }

        public static string Diagnose()
        {
            int vanilla = 0, ours = 0, mist = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || mat.shader == null) continue;
                if (mat.shader.name == VanillaParticleShader) { vanilla++; if (IsMist(mat)) mist++; }
                else if (mat.shader.name == ShaderName) ours++;
            }
            return $"[FiresLitParticles] enabled={Enabled} swapped={SwappedCount} | scan vanilla-LitParticles={vanilla} (mist={mist}) ours={ours}";
        }
    }

    /// <summary>Polls the config each ~1s so the mist swap is the config's mirror (no SettingChanged reliance).</summary>
    public class FiresLitParticlesDriver : MonoBehaviour
    {
        private const float ScanSeconds = 4f;
        private bool _cmdRegistered;
        private float _next;
        private float _nextScan;
        private static readonly int s_playerPosId = Shader.PropertyToID("_FiresFogPlayerPos");
        private void Update()
        {
            if (!_cmdRegistered)
            {
                try { MistSwapCommand.Register(); _cmdRegistered = true; }
                catch { /* Terminal not ready yet; retry next frame */ }
            }
            // Feed the player position to the mist push every frame (smooth), independent of whether FiresVolFog runs.
            if (FiresLitParticlesShaderLoader.Enabled && Player.m_localPlayer != null)
                Shader.SetGlobalVector(s_playerPosId, Player.m_localPlayer.transform.position);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (ZNetScene.instance == null) return;
            FiresLitParticlesShaderLoader.ConfigPoll?.Invoke();   // config-driven enable (if the plugin wired it)
            // The scan lists every loaded material, so it runs every few seconds like the terrain swap's, not every poll.
            if (!FiresLitParticlesShaderLoader.Enabled || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            FiresLitParticlesShaderLoader.ApplyToAll();
        }
    }

    /// <summary><c>va_mist on|off|status</c> — toggle the Mistlands-mist swap live for A/B testing.</summary>
    public static class MistSwapCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand(
                "va_mist",
                "Replace vanilla Custom/LitParticles (Mistlands heavymist) with Custom/FiresLitParticles. Usage: va_mist on|off|status",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off")
                    {
                        FiresLitParticlesShaderLoader.SetEnabledConfig?.Invoke(false); // config = source of truth
                        FiresLitParticlesShaderLoader.Enabled = false;
                        int r = FiresLitParticlesShaderLoader.Restore();
                        args.Context.AddString($"[FiresLitParticles] disabled (config Enabled=false); restored {r} mist material(s).");
                    }
                    else if (a == "status")
                    {
                        args.Context.AddString(FiresLitParticlesShaderLoader.Diagnose());
                    }
                    else
                    {
                        FiresLitParticlesShaderLoader.SetEnabledConfig?.Invoke(true);  // config = source of truth
                        FiresLitParticlesShaderLoader.Enabled = true;
                        int n = FiresLitParticlesShaderLoader.ApplyToAll();
                        args.Context.AddString($"[FiresLitParticles] enabled (config Enabled=true); swapped {n} mist material(s) (total {FiresLitParticlesShaderLoader.SwappedCount}).");
                    }
                },
                isCheat: false);
            s_registered = true;
        }
    }
}
