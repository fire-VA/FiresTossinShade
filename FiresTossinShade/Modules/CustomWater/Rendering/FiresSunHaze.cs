using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Scales how far the sky's sun disk blooms into a full-screen haze. Vanilla's SunShafts post-process
    /// keys off bright sky pixels around the sun, so the disk's size and brightness are what decide whether
    /// looking sunward washes the screen out. Driving them from weather + sun elevation keeps a clear midday
    /// Meadows on vanilla and saves the glare for mist, rain and a low sun — where it reads as atmosphere.
    /// Shafts cast by trees and buildings are vanilla's own occlusion and are untouched by this.
    /// </summary>
    public static class FiresSunHaze
    {
        private const float HeavyFogDensity = 0.06f;

        public static bool Enabled = true;
        public static float ClearDiskBoost = 1f;
        public static float HazyDiskBoost = 4f;
        public static float ClearSunSize = 1f;
        public static float HazySunSize = 1.8f;
        public static float LowSunElevation = 0.35f;
        public static float LowSunWeight = 1f;
        public static float WeatherWeight = 1f;

        private static readonly int Prop_SkyboxSunDir = Shader.PropertyToID("_SkyboxSunDir");

        public static float Haze01 { get; private set; }

        public static float DiskBoost => Mathf.Lerp(ClearDiskBoost, HazyDiskBoost, Haze01);
        public static float SunSizeMultiplier => Mathf.Lerp(ClearSunSize, HazySunSize, Haze01);

        internal static void UpdateFromEnv(EnvSetup env, float dayInt, float nightInt, float morningInt, float eveningInt)
        {
            if (!Enabled || env == null)
            {
                Haze01 = 0f;
                return;
            }

            Haze01 = Mathf.Clamp01(Mathf.Max(
                LowSunFactor() * Mathf.Max(0f, LowSunWeight),
                WeatherFactor(env, dayInt, nightInt, morningInt, eveningInt) * Mathf.Max(0f, WeatherWeight)));
        }

        private static float LowSunFactor()
        {
            float sunHeight = Mathf.Max(0f, Shader.GetGlobalVector(Prop_SkyboxSunDir).y);
            float elevation01 = Mathf.InverseLerp(0f, Mathf.Max(0.01f, LowSunElevation), sunHeight);
            return 1f - Mathf.SmoothStep(0f, 1f, elevation01);
        }

        private static float WeatherFactor(EnvSetup env, float dayInt, float nightInt, float morningInt, float eveningInt)
        {
            float blendedFogDensity = env.m_fogDensityNight * nightInt + env.m_fogDensityDay * dayInt
                                    + env.m_fogDensityMorning * morningInt + env.m_fogDensityEvening * eveningInt;
            return Mathf.Clamp01(Mathf.Clamp01(blendedFogDensity / HeavyFogDensity)
                                 + Mathf.Clamp01(env.m_rainCloudAlpha));
        }
    }
}
