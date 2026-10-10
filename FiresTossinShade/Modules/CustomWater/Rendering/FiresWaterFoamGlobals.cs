using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Pushes foam parameters as SHADER GLOBALS (`_FiresFoam*`) so every water
    /// material reads identical values. Solves the foam-pattern seam at zone
    /// boundaries (same root cause as the wind-globals seam fix — see
    /// reference_fireswater_shader.md wrong-turn #7).
    ///
    /// Before this: each captured vanilla water material had its own
    /// `_FoamTilingA/B`, `_FoamSpeed`, etc., baked into the material. The
    /// FiresWater shader read those per-material props, so adjacent meshes
    /// with different captured values produced different foam UVs at the
    /// same world position — visible cracks at zone seams.
    ///
    /// After: shader reads shared globals; this class sets them once at
    /// shader-loader init. Every mesh evaluates foam math with identical
    /// inputs. No divergence. Same pattern as vanilla EnvMan setting
    /// `_GlobalWind*` for wave-height consistency.
    /// </summary>
    public static class FiresWaterFoamGlobals
    {
        public static float FoamShoreAmount = 0.5f;
        public static float FoamShoreContrast = -1.0f;
        public static float FoamSpeed = 4.0f;
        public static float FoamTilingA = 1.0f;
        public static float FoamTilingB = 0.5f;
        public static float FoamIntensityA = 0.6f;
        public static float FoamIntensityB = 0.4f;
        public static float FoamWaveHeightDriven = 1.4f;
        public static Color FoamColor = Color.white;

        // Round-85 — swap the pixelated vanilla foam for the HD baked foam
        // (FiresFoamHD, 2K seamless, shipped in the tossinshade bundle) on every
        // water material. On by default (user request: "default valheim foam is
        // too pixilated"); flip false to keep the captured vanilla foam.
        public static bool ReplaceFoamTexture = true;
        private static Texture2D s_hdFoam;
        private static readonly int Prop_FoamTex = Shader.PropertyToID("_FoamTex");
        private static readonly int Prop_FoamHighTex = Shader.PropertyToID("_FoamHighTex");
        private static readonly int Prop_RandomFoamTex = Shader.PropertyToID("_RandomFoamTex");

        // The HD foam ships in Core's shader bundle beside Custom/FiresWater. Cached on success; null keeps vanilla foam.
        private static Texture2D HdFoam()
        {
            if (s_hdFoam != null) return s_hdFoam;
            s_hdFoam = FiresCore.Materials.FiresShaderBroker.GetTexture("FiresFoamHD");
            if (s_hdFoam != null)
                FiresLogger.LogVerbose($"[FiresWaterFoamGlobals] loaded HD foam ({s_hdFoam.width}x{s_hdFoam.height}).");
            return s_hdFoam;
        }

        private static readonly int Prop_ShoreAmount = Shader.PropertyToID("_FiresFoamShoreAmount");
        private static readonly int Prop_ShoreContrast = Shader.PropertyToID("_FiresFoamShoreContrast");
        private static readonly int Prop_Speed = Shader.PropertyToID("_FiresFoamSpeed");
        private static readonly int Prop_TilingA = Shader.PropertyToID("_FiresFoamTilingA");
        private static readonly int Prop_TilingB = Shader.PropertyToID("_FiresFoamTilingB");
        private static readonly int Prop_IntensityA = Shader.PropertyToID("_FiresFoamIntensityA");
        private static readonly int Prop_IntensityB = Shader.PropertyToID("_FiresFoamIntensityB");
        private static readonly int Prop_WaveHeightDriven = Shader.PropertyToID("_FiresFoamWaveHeightDriven");
        private static readonly int Prop_Color = Shader.PropertyToID("_FiresFoamColor");

        private static bool s_pushed;

        // Per-material property IDs (legacy `_Foam*` names). The bundled-as-of-
        // 2026-06-01 shader still reads these — once the bundle rebuilds with
        // the `_FiresFoam*` globals patch, the per-material writes are
        // redundant but harmless. Forcing the per-material values to match the
        // globals is what makes the seam fix work BEFORE the bundle rebuild.
        private static readonly int Prop_LegacyShoreAmount = Shader.PropertyToID("_FoamShoreAmount");
        private static readonly int Prop_LegacyShoreContrast = Shader.PropertyToID("_FoamShoreContrast");
        private static readonly int Prop_LegacySpeed = Shader.PropertyToID("_FoamSpeed");
        private static readonly int Prop_LegacyTilingA = Shader.PropertyToID("_FoamTilingA");
        private static readonly int Prop_LegacyTilingB = Shader.PropertyToID("_FoamTilingB");
        private static readonly int Prop_LegacyIntensityA = Shader.PropertyToID("_FoamIntensityA");
        private static readonly int Prop_LegacyIntensityB = Shader.PropertyToID("_FoamIntensityB");
        private static readonly int Prop_LegacyWaveHeightDriven = Shader.PropertyToID("_FoamWaveHeightDriven");
        private static readonly int Prop_LegacyColor = Shader.PropertyToID("_FoamColor");

        public static void Push()
        {
            Shader.SetGlobalFloat(Prop_ShoreAmount, FoamShoreAmount);
            Shader.SetGlobalFloat(Prop_ShoreContrast, FoamShoreContrast);
            Shader.SetGlobalFloat(Prop_Speed, FoamSpeed);
            Shader.SetGlobalFloat(Prop_TilingA, FoamTilingA);
            Shader.SetGlobalFloat(Prop_TilingB, FoamTilingB);
            Shader.SetGlobalFloat(Prop_IntensityA, FoamIntensityA);
            Shader.SetGlobalFloat(Prop_IntensityB, FoamIntensityB);
            Shader.SetGlobalFloat(Prop_WaveHeightDriven, FoamWaveHeightDriven);
            Shader.SetGlobalColor(Prop_Color, FoamColor);

            if (!s_pushed)
            {
                FiresLogger.LogVerbose($"[FiresWaterFoamGlobals] Pushed shared foam globals " +
                          $"(tilingA={FoamTilingA}, tilingB={FoamTilingB}, " +
                          $"speed={FoamSpeed}, intensityA={FoamIntensityA}, " +
                          $"intensityB={FoamIntensityB}, waveDriven={FoamWaveHeightDriven}, " +
                          $"shoreAmount={FoamShoreAmount}, shoreContrast={FoamShoreContrast}).");
                s_pushed = true;
            }
        }

        /// <summary>
        /// Write the canonical foam values onto a single material's per-material
        /// property table. Run on every water material at swap-time so the
        /// pre-rebuild shader (which still reads `_Foam*` per-material) ALSO
        /// sees identical values. After the bundle rebuilds with the
        /// `_FiresFoam*` reads, this method becomes a harmless no-op.
        /// </summary>
        public static void WriteToMaterial(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty(Prop_LegacyShoreAmount)) mat.SetFloat(Prop_LegacyShoreAmount, FoamShoreAmount);
            if (mat.HasProperty(Prop_LegacyShoreContrast)) mat.SetFloat(Prop_LegacyShoreContrast, FoamShoreContrast);
            if (mat.HasProperty(Prop_LegacySpeed)) mat.SetFloat(Prop_LegacySpeed, FoamSpeed);
            if (mat.HasProperty(Prop_LegacyTilingA)) mat.SetFloat(Prop_LegacyTilingA, FoamTilingA);
            if (mat.HasProperty(Prop_LegacyTilingB)) mat.SetFloat(Prop_LegacyTilingB, FoamTilingB);
            if (mat.HasProperty(Prop_LegacyIntensityA)) mat.SetFloat(Prop_LegacyIntensityA, FoamIntensityA);
            if (mat.HasProperty(Prop_LegacyIntensityB)) mat.SetFloat(Prop_LegacyIntensityB, FoamIntensityB);
            if (mat.HasProperty(Prop_LegacyWaveHeightDriven)) mat.SetFloat(Prop_LegacyWaveHeightDriven, FoamWaveHeightDriven);
            if (mat.HasProperty(Prop_LegacyColor)) mat.SetColor(Prop_LegacyColor, FoamColor);

            // Round-85 — replace the foam texture on all three layers with the
            // HD baked foam. The shader's per-layer tiling/speed differentiates
            // the shared texture; sharper foam on both ocean and custom water.
            if (ReplaceFoamTexture)
            {
                var hd = HdFoam();
                if (hd != null)
                {
                    if (mat.HasProperty(Prop_FoamTex)) mat.SetTexture(Prop_FoamTex, hd);
                    if (mat.HasProperty(Prop_FoamHighTex)) mat.SetTexture(Prop_FoamHighTex, hd);
                    if (mat.HasProperty(Prop_RandomFoamTex)) mat.SetTexture(Prop_RandomFoamTex, hd);
                }
            }
        }
    }
}
