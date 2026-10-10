using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Ties the volumetric fog to the live environment/weather. A Postfix on
    /// EnvMan.SetEnv (runs every FixedUpdate with the already-crossfaded env AND the
    /// exact time-of-day blend weights) computes the env's blended fog colour +
    /// density + rain, and shadows them onto the LAND + SEA fog zones on top of the
    /// config base: tint toward the env fog hue, thicken in mist/rain/storms, and
    /// lift the opacity cap in heavy weather. Reads the EnvSetup directly so it's
    /// immune to any RenderSettings override (e.g. FiresFogControl). Pure C# — it
    /// just drives the existing shader globals, so no bundle work. The config values
    /// stay the BASE look; these blend on top by the influence sliders.
    /// </summary>
    [HarmonyPatch(typeof(EnvMan), "SetEnv",
        new[] { typeof(EnvSetup), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) })]
    public static class FiresFogEnvReactive
    {
        public static bool Enabled = true;
        public static float ColorBlend = 0.6f;   // how far the land/sea fog hue shifts toward the env fog colour
        public static float DensityBlend = 0.5f;   // how strongly weather ramps the fog toward full (curve shaping)
        public static float RainBoost = 0.5f;   // extra density per unit rainCloudAlpha (rain/storms)
        public static float StormOpacity = 0.6f;   // opacity cap the fog can reach in heavy weather (so the boost is visible)
        // Fraction of the base fog that shows on a DEAD-CLEAR day. Vanilla clear day is nearly
        // fog-free — the fog belongs to dawn/dusk/mist/rain. This was effectively 1.0 before
        // (a constant floor = "foggy all the time"); a low value makes clear days clear again
        // while the env still drives the fog up in weather.
        public static float ClearDayFog = 0.08f;
        public static bool AffectUnderwater = true;   // tint + thin the underwater murk toward the live surface env
        public static float UwColorBlend    = 0.6f;    // how far the underwater murk hue shifts toward the env fog
        // Underwater density on a DEAD-CLEAR day, as a fraction of the base. Unlike the surface fog
        // (near-zero clear floor) underwater keeps a real floor — water is always a bit murky — but
        // a clear bright day THINS it (see farther, closer to the above-water clarity) so surfacing
        // never JARS with fog that isn't there on the surface. Weather ramps it back toward full.
        public static float UwClearDayFactor = 0.45f;
        public static float MistColorBlend   = 0.6f;  // Mistlands-mist tint shift toward env fog (when the va_mist swap is on)
        public static float MistDensityBlend = 0.5f;  // Mistlands-mist thickening in weather (mirrors DensityBlend, for the mist)

        private static void Postfix(EnvSetup env, float dayInt, float nightInt, float morningInt, float eveningInt)
        {
            if (!Enabled || env == null) return;
            var cm = ConfigManager.Instance;
            if (cm == null) return;

            try
            {
                // The env's blended fog — same weights EnvMan applies, straight off the
                // EnvSetup so a RenderSettings override doesn't mask the real env hue.
                Color envFog = env.m_fogColorNight * nightInt + env.m_fogColorDay * dayInt
                             + env.m_fogColorMorning * morningInt + env.m_fogColorEvening * eveningInt;
                float envDensity = env.m_fogDensityNight * nightInt + env.m_fogDensityDay * dayInt
                                 + env.m_fogDensityMorning * morningInt + env.m_fogDensityEvening * eveningInt;
                float rain = env.m_rainCloudAlpha;

                float cBlend = Mathf.Clamp01(ColorBlend);
                float densBoost = Mathf.Clamp01(envDensity / 0.06f);                  // 0.003 clear .. ~0.06+ heavy → 0..1
                // How "weathery" the environment is right now: 0 = dead-clear day, 1 = heavy
                // mist/storm. Driven straight off the env's own blended fog density + rain, so
                // it rises at dawn/dusk and in mist/rain exactly like vanilla's fog does.
                float weather01 = Mathf.Clamp01((densBoost + rain * RainBoost) * (0.5f + DensityBlend));
                // Density now SCALES from a faint clear-day floor up to the full base in weather,
                // instead of always sitting at the full base (the "foggy all the time" bug).
                float densFactor = Mathf.Lerp(Mathf.Clamp01(ClearDayFog), 1f, weather01);
                float stormOp = Mathf.Clamp01(StormOpacity) * weather01;

                FiresVolumetricFogPass.LandColor = Color.Lerp(cm.configVfLandColor.Value, envFog, cBlend);
                FiresVolumetricFogPass.LandColorThin = Color.Lerp(cm.configVfLandColorThin.Value, envFog, cBlend);
                FiresVolumetricFogPass.LandDensity = cm.configVfLandDensity.Value * densFactor;
                FiresVolumetricFogPass.LandMaxOp = Mathf.Max(cm.configVfLandMaxOp.Value * densFactor, stormOp);

                FiresVolumetricFogPass.SeaColor = Color.Lerp(cm.configVfSeaColor.Value, envFog, cBlend);
                FiresVolumetricFogPass.SeaColorThin = Color.Lerp(cm.configVfSeaColorThin.Value, envFog, cBlend);
                FiresVolumetricFogPass.SeaDensity = cm.configVfSeaDensity.Value * densFactor;
                FiresVolumetricFogPass.SeaMaxOp = Mathf.Max(cm.configVfSeaMaxOp.Value * densFactor, stormOp);

                // High cloud deck — near-clear by day, thickens + darkens into a storm overcast.
                FiresVolumetricFogPass.CloudColor = Color.Lerp(cm.configVfCloudColor.Value, envFog, cBlend);
                FiresVolumetricFogPass.CloudDensity = cm.configVfCloudDensity.Value * densFactor;
                FiresVolumetricFogPass.CloudMaxOp = Mathf.Max(cm.configVfCloudMaxOp.Value * densFactor, stormOp);

                // Underwater murk tracks the surface environment so surfacing never JARS. On a clear
                // bright day it tints toward the light surface fog AND thins (see farther, closer to
                // the above-water clarity); dawn/dusk/mist/rain/storm tint it toward the env hue and
                // thicken it back toward full. The depth-fog (FiresUnderwaterEnv.ApplyDepthFog) reads
                // these same UwColor* so the built-in near tint tracks the env too. MaxOp stays at the
                // config cap so the far world-edge is still hidden even when a clear day thins the reach.
                if (AffectUnderwater)
                {
                    float uwBlend = Mathf.Clamp01(UwColorBlend);
                    FiresVolumetricFogPass.UwColor     = Color.Lerp(cm.configVfUwColor.Value,     envFog, uwBlend);
                    FiresVolumetricFogPass.UwColorThin = Color.Lerp(cm.configVfUwColorThin.Value, envFog, uwBlend);
                    float uwDensFactor = Mathf.Lerp(Mathf.Clamp01(UwClearDayFactor), 1f, weather01);
                    FiresVolumetricFogPass.UwDensity   = cm.configVfUwDensity.Value * uwDensFactor;
                    FiresVolumetricFogPass.UwMaxOp     = Mathf.Max(cm.configVfUwMaxOp.Value, stormOp * 0.9f);
                }

                // Mistlands MIST swap — same env-reactive model, only while the swap is active. Sets the live weather
                // fields the loader folds into _MistTint/_MistDensity, then re-pushes to the swapped mist material(s).
                if (FiresLitParticlesShaderLoader.Enabled)
                {
                    FiresLitParticlesShaderLoader.WeatherEnvFog     = envFog;
                    FiresLitParticlesShaderLoader.WeatherColorBlend = Mathf.Clamp01(MistColorBlend);
                    FiresLitParticlesShaderLoader.WeatherDensityMul = 1f + densBoost * (MistDensityBlend * 2f) + rain * RainBoost;
                    FiresLitParticlesShaderLoader.ApplyTuningToAll();
                }
            }
            catch { /* never let a fog tweak disrupt the env pipeline */ }
        }
    }
}
