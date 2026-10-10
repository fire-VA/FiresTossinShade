using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Swaps Valheim's Custom/SkyboxProcedural shader for our
    /// Custom/FiresSkyboxAurora on every active skybox material. The
    /// replacement is a full port of Unity's procedural skybox (Rayleigh +
    /// Mie scattering, sun disk, moon, stars, fog masks) PLUS aurora
    /// rendering in the same fragment shader. EnvMan's per-env property
    /// mutations (_SkyTint, _GroundColor, _AtmosphereThickness, _Exposure,
    /// _MoonColor, etc.) continue to drive day/night transitions exactly
    /// as before because the shader honors the full vanilla property
    /// contract.
    ///
    /// PRIOR REVS (all wrong, kept here as a what-not-to-do list):
    /// * Camera-overlay CommandBuffer → painted on player/water/terrain
    /// * Sphere dome at 5000m → visible edge at high altitude, blocked
    ///   stars, additive blend washed out sky
    /// * Simplified gradient sky shader → didn't match Unity's scattering,
    ///   produced "photo negative" colors during day
    /// * Custom god-ray ray-march → fundamentally wrong direction model
    /// THIS REV: actual procedural skybox shader with the full scattering
    /// math, aurora layered in the same pass with per-channel cap so it
    /// can't blow out sky/stars/sun.
    ///
    /// LIFECYCLE
    /// EnvMan rewrites RenderSettings.skybox on env changes. Driver ticks
    /// every LateUpdate, detects the active skybox is Custom/SkyboxProcedural,
    /// swaps to ours. Originals are recorded per-material-InstanceID so a
    /// config-off restores them cleanly.
    /// </summary>
    public static class FiresSkyboxAurora
    {
        public const string VanillaShaderName = "Custom/SkyboxProcedural";
        public const string OurShaderName = "Custom/FiresSkyboxAurora";

        public static bool Enabled = true;
        public static float Intensity = 1.0f;
        public static bool NightOnly = true;
        public static bool DuringStorms = false;

        // Day-sky tweak knobs (also pushed to the active skybox material)
        public static Color DaySkyTint = Color.white;   // full-colour day-sky tint
        public static float DaySkyHue = 0f;
        public static float DaySkyDensity = 1.0f;
        public static float StarFadeBySky = 3.5f;
        public static float FogMatch = 1f;      // 0.3.159: 0 = the old day sky, 1 = the dome lifted toward the fog colour by day
        public static float DayFogLift = 4f;    // 0.3.159: how far the day fill lifts (_DaySkyDensity + day * this)
        public static float HorizonFogMin = 0f, HorizonFogMax = 0.25f;   // 0.3.160: the shader's horizon band (ray.y) blended to unity_FogColor
        public static float StarSunFade = 5f;   // 0.3.160: stars off as the sun rises (shader: 1 - saturate(sunY * this))
        public static float DayFogLiftWithBlend = 1f;   // 0.3.160: the C# day lift when the shader does the horizon blend itself
        internal static float DayNow;           // 0.3.159: the last day factor (0 night .. 1 day), FiresSkyboxDriver fades the stars by it
        // VISUAL-only multiplier on the sun DISK brightness in the sky (NOT the
        // directional light). >1 makes the disk punch through the bright day sky.
        public static float SunDiskBoost = 2.5f;

        // Wired from configs in ApplyFiresWaterConfig
        public static float CycleSpeed = 0.05f;
        public static float PulseSpeed = 0.35f;
        public static float NorthBiasPower = 3.0f;
        public static float ContribCap = 0.3f;
        public static float AltMin = 0.0f;
        public static float AltMax = 0.85f;
        public static float ColumnFreq = 16f;
        public static float VertFreq = 1.5f;
        public static float Sharpness = 0.55f;
        public static float ScrollSpeed = 0.04f;

        public static Shader AuroraShader { get; private set; }

        // Time-eased 0..1 gate for the aurora layer (see Tick) — shares
        // FiresSkyboxDriver.AuroraGateSeconds so glow + ribbons fade in lockstep.
        private static float s_gateBlend;

        // Map: instanceID → original shader. So if EnvMan creates a new
        // skybox material per env, we don't lose track of any of them.
        private static readonly Dictionary<int, Shader> s_originalShaders
            = new Dictionary<int, Shader>();

        private static readonly int Prop_AuroraEnabled = Shader.PropertyToID("_AuroraEnabled");
        private static readonly int Prop_AuroraIntensity = Shader.PropertyToID("_AuroraIntensity");
        private static readonly int Prop_AuroraColor1 = Shader.PropertyToID("_AuroraColor1");
        private static readonly int Prop_AuroraColor2 = Shader.PropertyToID("_AuroraColor2");
        private static readonly int Prop_AuroraColor3 = Shader.PropertyToID("_AuroraColor3");
        private static readonly int Prop_AuroraNorthBias = Shader.PropertyToID("_AuroraNorthBias");
        private static readonly int Prop_AuroraContribCap = Shader.PropertyToID("_AuroraContribCap");
        private static readonly int Prop_AuroraAltMin = Shader.PropertyToID("_AuroraAltMin");
        private static readonly int Prop_AuroraAltMax = Shader.PropertyToID("_AuroraAltMax");
        private static readonly int Prop_AuroraColumnFreq = Shader.PropertyToID("_AuroraColumnFreq");
        private static readonly int Prop_AuroraVertFreq = Shader.PropertyToID("_AuroraVertFreq");
        private static readonly int Prop_AuroraSharpness = Shader.PropertyToID("_AuroraSharpness");
        private static readonly int Prop_AuroraScrollSpeed = Shader.PropertyToID("_AuroraScrollSpeed");
        private static readonly int Prop_DaySkyTint = Shader.PropertyToID("_DaySkyTint");
        private static readonly int Prop_DaySkyHue = Shader.PropertyToID("_DaySkyHue");
        private static readonly int Prop_DaySkyDensity = Shader.PropertyToID("_DaySkyDensity");
        private static readonly int Prop_StarFadeBySky = Shader.PropertyToID("_StarFadeBySky");
        private static readonly int Prop_SunDiskBoost = Shader.PropertyToID("_SunDiskBoost");
        private static readonly int Prop_FogBlend = Shader.PropertyToID("_FogBlend"), Prop_FogHeightMin = Shader.PropertyToID("_FogHeightMin"), Prop_FogHeightMax = Shader.PropertyToID("_FogHeightMax"), Prop_StarSunFade = Shader.PropertyToID("_StarSunFade");

        public static bool TryLoadShader(AssetBundle bundle)
        {
            if (AuroraShader != null) return true;
            if (bundle == null) return false;

            AuroraShader = bundle.LoadAsset<Shader>(OurShaderName);
            if (AuroraShader == null)
            {
                foreach (var s in bundle.LoadAllAssets<Shader>())
                {
                    if (s != null && s.name == OurShaderName) { AuroraShader = s; break; }
                }
            }
            if (AuroraShader == null) return false;

            FiresLogger.LogVerbose($"[FiresSkyboxAurora] Loaded '{OurShaderName}' from bundle.");
            return true;
        }

        public static void Tick()
        {
            if (AuroraShader == null) return;

            var sky = RenderSettings.skybox;
            if (sky == null || sky.shader == null) return;

            int id = sky.GetInstanceID();
            bool currentlyOurs = sky.shader == AuroraShader;
            bool currentlyVanilla = sky.shader.name == VanillaShaderName;

            // First-time sight: cache the original shader so we can revert
            if (!currentlyOurs && currentlyVanilla && !s_originalShaders.ContainsKey(id))
            {
                s_originalShaders[id] = sky.shader;
            }

            bool wantAurora = Enabled
                && Intensity > 0.001f
                && (!NightOnly || IsNight())
                && (DuringStorms || !IsStormy());

            // Time-eased aurora gate (vanilla-style timed transition): wantAurora flips binary at
            // the night boundary / storm switch, so ease 0..1 and scale _AuroraIntensity by it —
            // the ribbons FADE in/out instead of snapping. No shader change needed (ComputeAurora
            // already multiplies by _AuroraIntensity).
            s_gateBlend = Mathf.MoveTowards(s_gateBlend, wantAurora ? 1f : 0f,
                Time.deltaTime / Mathf.Max(0.05f, FiresSkyboxDriver.AuroraGateSeconds));

            // Swap shader to ours if we're ENABLED and it's still vanilla.
            // Note: even when wantAurora is false at this instant, we keep
            // our shader installed so the procedural sky stays consistent
            // (our shader IS the procedural sky now). We just push
            // _AuroraEnabled = 0 to mute the aurora layer.
            //
            // The Enabled gate here is CRITICAL: without it, this branch re-swaps to our shader
            // one frame after the !Enabled branch below restores vanilla — the skybox SHADER
            // ping-pongs every frame and the whole sky flashes rapidly the entire time the
            // toggle is off (the user-reported master-switch/aurora-override flash).
            if (currentlyVanilla)
            {
                if (!Enabled) return;   // stay vanilla while off — no swap ping-pong
                sky.shader = AuroraShader;
                // Force HQ sun-disk keyword — material keyword state can
                // get cleared on shader swap depending on Unity version,
                // and vanilla Custom/SkyboxProcedural always has
                // _SUNDISK_HIGH_QUALITY enabled. Without this, the sun
                // disk + scattering sample paths are dead and the sky
                // renders with no visible sun.
                sky.DisableKeyword("_SUNDISK_NONE");
                sky.DisableKeyword("_SUNDISK_SIMPLE");
                sky.EnableKeyword("_SUNDISK_HIGH_QUALITY");
                FiresLogger.LogVerbose($"[FiresSkyboxAurora] Swapped skybox material to {OurShaderName} (id={id}). Forced _SUNDISK_HIGH_QUALITY.");
                currentlyOurs = true;
            }
            else if (!Enabled && currentlyOurs && s_originalShaders.TryGetValue(id, out var orig))
            {
                // Master toggle off — restore vanilla
                sky.shader = orig;
                FiresLogger.LogVerbose($"[FiresSkyboxAurora] Restored vanilla {VanillaShaderName} on id={id}.");
                return;
            }

            // Push per-frame uniforms if our shader is active
            if (currentlyOurs)
            {
                // Eased gate: enabled while any blend remains, intensity scaled by the blend so
                // the ribbons fade up at dusk / out at dawn instead of snapping at the boundary.
                sky.SetFloat(Prop_AuroraEnabled, s_gateBlend > 0.001f ? 1f : 0f);
                sky.SetFloat(Prop_AuroraIntensity, Intensity * s_gateBlend);
                sky.SetFloat(Prop_AuroraNorthBias, NorthBiasPower);
                sky.SetFloat(Prop_AuroraContribCap, ContribCap);
                sky.SetFloat(Prop_AuroraAltMin, AltMin);
                sky.SetFloat(Prop_AuroraAltMax, AltMax);
                sky.SetFloat(Prop_AuroraColumnFreq, ColumnFreq);
                sky.SetFloat(Prop_AuroraVertFreq, VertFreq);
                sky.SetFloat(Prop_AuroraSharpness, Sharpness);
                sky.SetFloat(Prop_AuroraScrollSpeed, ScrollSpeed);

                // Day-sky tweak knobs — always pushed so they take effect
                // whether or not aurora is enabled this frame.
                // 0.3.159 (Fire: the World Tree branches flat white on a clear summer day; FDT sky_shot: a STARFIELD at noon, the sky darker than the
                // fog the branches are drawn in): this shader never reads Unity fog, and its stars only fade by sky brightness. By day (sun height from
                // the global _SkyboxSunDir): the star fade raised to its maximum, and the dome lifted toward RenderSettings.fogColor through the
                // existing day-sky fill (_DaySkyTint * (_DaySkyDensity - 1)), so the sky sits near the fog colour the distant meshes fade to
                float day = DayFactor();
                Color fogTint = RenderSettings.fogColor; float fm = Mathf.Max(0.001f, Mathf.Max(fogTint.r, Mathf.Max(fogTint.g, fogTint.b))); fogTint = new Color(fogTint.r / fm, fogTint.g / fm, fogTint.b / fm, 1f);
                sky.SetColor(Prop_DaySkyTint, Color.Lerp(DaySkyTint, DaySkyTint * fogTint, day * FogMatch));
                sky.SetFloat(Prop_DaySkyHue, DaySkyHue);
                // 0.3.160: a bundle with the shader's own horizon fog blend (_FogBlend) gets that band + the sun-height star fade, and a smaller C# lift
                bool shaderFog = sky.HasProperty(Prop_FogBlend);
                if (shaderFog) { sky.SetFloat(Prop_FogBlend, FogMatch); sky.SetFloat(Prop_FogHeightMin, HorizonFogMin); sky.SetFloat(Prop_FogHeightMax, HorizonFogMax); sky.SetFloat(Prop_StarSunFade, StarSunFade); }
                sky.SetFloat(Prop_DaySkyDensity, DaySkyDensity + day * FogMatch * (shaderFog ? DayFogLiftWithBlend : DayFogLift));
                sky.SetFloat(Prop_StarFadeBySky, Mathf.Lerp(StarFadeBySky, 20f, day));
                DayNow = day;
                sky.SetFloat(Prop_SunDiskBoost, SunDiskBoost * FiresSunHaze.DiskBoost);

                // Cycled palette + pulse (matches the legacy horizon-glow
                // flicker). Three stops offset along the palette so the
                // dome shows a moving 3-color gradient up the altitude.
                float palette = Time.unscaledTime * CycleSpeed;
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * PulseSpeed);
                Color c1 = FiresSkyboxDriver.SampleAuroraStops(palette) * pulse;
                Color c2 = FiresSkyboxDriver.SampleAuroraStops(palette + 1.33f) * pulse;
                Color c3 = FiresSkyboxDriver.SampleAuroraStops(palette + 2.66f) * pulse;
                sky.SetColor(Prop_AuroraColor1, c1);
                sky.SetColor(Prop_AuroraColor2, c2);
                sky.SetColor(Prop_AuroraColor3, c3);
            }
        }

        public static void RestoreImmediate()
        {
            var sky = RenderSettings.skybox;
            if (sky == null) return;
            int id = sky.GetInstanceID();
            if (s_originalShaders.TryGetValue(id, out var orig) && orig != null)
            {
                sky.shader = orig;
            }
        }

        // 0.3.159: 0 at night .. 1 once the sun is ~11 deg up (the global sun direction Valheim sets for the skybox)
        internal static float DayFactor()
        {
            try { Vector4 s = Shader.GetGlobalVector("_SkyboxSunDir"); return Mathf.Clamp01(s.y * 5f); } catch { return 0f; }
        }

        private static bool IsNight()
        {
            try { return EnvMan.instance != null && EnvMan.IsNight(); }
            catch { return false; }
        }

        private static bool IsStormy()
        {
            try
            {
                var env = EnvMan.instance?.GetCurrentEnvironment();
                if (env == null) return false;
                string n = env.m_name ?? string.Empty;
                return n.Contains("Storm") || n.Contains("Rain") || n.Contains("Thunder");
            }
            catch { return false; }
        }
    }

    public class FiresSkyboxAuroraDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (FiresSkyboxAurora.AuroraShader == null)
            {
                FiresWaterShaderLoader.EnsureLoaded();
                if (!FiresSkyboxAurora.TryLoadShader(FiresWaterShaderLoader.Bundle))
                    return;
            }
            FiresSkyboxAurora.Tick();
        }

        private void OnDisable()
        {
            FiresSkyboxAurora.RestoreImmediate();
        }
    }
}
