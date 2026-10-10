using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Live-tunable FiresWater SURFACE globals — fresnel + reflection + slope
    /// foam — pushed via <c>Shader.SetGlobalFloat</c> exactly like
    /// <see cref="FiresWaterFoamGlobals"/>. One <see cref="Push"/> drives every
    /// FiresWater surface at once (zone water, the coverage disk, FAT water), so
    /// the config sliders that set these values take effect in REAL TIME with no
    /// asset-bundle rebuild.
    ///
    /// Reflection is compressed into a [floor, max] band instead of the raw
    /// Fresnel 0..1. The FLOOR keeps a little reflection even looking straight
    /// down (so near water never goes dead-clear), and the MAX caps grazing
    /// reflection (so distant water isn't a bright mirror). That compression is
    /// what stops the camera view-angle from swinging the water between clear
    /// and reflective — the "the camera determines the color" problem.
    ///
    /// NOTE: needs the bundle built with the matching <c>_Fires*</c> global
    /// reads in FiresWater.shader. Until that bundle ships these globals are set
    /// but unread — harmless.
    /// </summary>
    public static class FiresWaterTuning
    {
        public static float FresnelPower = 3.0f;   // falloff exponent — lower = softer near→far blend
        public static float ReflectionFloor = 0.12f;  // min reflection (head-on) — kills the dead-clear band
        public static float ReflectionMax = 0.45f;  // max reflection (grazing) — lower = less mirror-like
        public static float FoamSlopeDriven = 3.0f;   // foam from surface steepness (wave fronts + chop)
        public static float WaveOpacity = 0.5f;   // taller wave crests -> more opaque surface (0 = off)
        // Master on/off for the V1 SURFACE caustics (the in-shader top-pass
        // caustics seen looking down through the water). When false, the
        // _FiresCausticStrength global is forced to 0 regardless of the
        // CausticStrength slider, so the surface caustics are fully off while
        // the slider value is preserved for when it's flipped back on. The V2
        // fullscreen seafloor caustics (UWCausticStrength) are unaffected.
        public static bool SurfaceCausticsEnabled = true;
        public static float CausticStrength = 0.4f;   // seafloor caustics brightness (0 = off)
        public static float CausticScale = 0.12f;  // caustic cell tiling (higher = smaller, denser cells)
        public static float CausticSpeed = 0.05f;  // caustic crawl speed along the wind
        public static float CausticWarp = 0.6f;   // domain-warp -> irregular organic blob shapes
        public static float CausticSharp = 4.0f;   // caustic line sharpness/thickness
        public static float NormalSpeed = 0.5f;   // surface ripple/normal-scroll speed (lower = calmer)
        public static float FoamSunBoost = 1.0f;   // lit foam: foam catches sunlight (0 = flat foam)

        // Stage-2 underwater caustics — INDEPENDENT of the surface caustic
        // scale/sharp/strength above, so the seafloor projection can be tuned
        // without changing the look-down surface caustics (and vice versa).
        public static float UWCausticScale = 0.02f;  // seafloor caustic cell size (lower = bigger)
        public static float UWCausticSharp = 1.0f;   // seafloor caustic contrast (lower = broader coverage)
        public static float UWCausticStrength = 1.5f;   // seafloor caustic additive brightness
        public static float UWCausticSpeed = 0.05f;  // seafloor caustic scroll speed along the wind (independent of V1)
        public static float UWCausticWarp = 0.6f;   // seafloor caustic domain-warp → organic swoop (independent of V1)
        // V3 — the underwater-pass (back-face) normal dapple in FiresWater.shader.
        // Separate from V1 (top-pass) and V2 (fullscreen seafloor). false = off.
        public static bool UnderwaterDappleEnabled = true;
        // Sea fog amount on the water SURFACE passes (0 = none / crisp to horizon,
        // 1 = full vanilla distance fog so far waves dissolve into the haze).
        public static float SurfaceFogAmount = 1.0f;

        // Distant-water tuning (far LOD disk only). Neutral defaults = unchanged.
        public static float DistantReflect = 1.0f;   // far-disk reflection scale (lower = less sky-mirror, more water hue)
        public static UnityEngine.Color DistantTint = UnityEngine.Color.white;
        public static float DistantBright = 1.0f;

        // Subsurface scattering (Crest Water 4 formula, MIT (c) Wave Harmonic) — light
        // glowing THROUGH wave bodies toward the viewer, strongest looking into a low sun.
        public static UnityEngine.Color SSSColor = new UnityEngine.Color(0.09f, 0.50f, 0.46f, 1f);
        public static float SSSBase = 0.1f;        // view-independent scatter
        public static float SSSSun = 1.2f;         // toward-sun lobe strength
        public static float SSSSunFalloff = 5f;    // lobe tightness (pow exponent)
        public static float SSSWaveBoost = 1.0f;   // extra scatter on tall crests

        // Four more Crest Water 4 lifts (MIT (c) Wave Harmonic) — exponential
        // depth absorption, bubble-foam underlayer, distance normal flatten +
        // spec roughen, Schlick fresnel. Shader-side globals default to 0/off
        // (bit-exact old look) until these are pushed.
        public static float AbsorbBlend = 0.5f;    // 0 = old linear depth lerp, 1 = full per-channel absorption
        public static UnityEngine.Color AbsorbExtinction
            = new UnityEngine.Color(0.45f, 0.16f, 0.10f, 1f);   // extinction per metre — red dies first
        public static float FoamBubbleIntensity = 0.35f;   // tinted bubble clouds under the whitecaps (0 = off)
        public static float DistNormalFade = 900f;    // metres to fully flatten far normals (0 = both distance effects off)
        public static float DistSpecRough = 0.6f;     // how much the distance fade widens the far specular lobe
        public static float SchlickBlend = 0f;        // 0 = banded fresnel (old), 1 = physical Schlick — opt-in
        public static float NadirReflCap = 1f;        // luminance cap on the head-on reflection (0 = off); released toward grazing
        public static bool  PixelDepth = true;        // per-pixel vertical depth from the camera depth texture (kills vertex-smear patches)
        public static float SunSpecScale = 1f;        // global sun-specular scale — the lever zone water never had
        public static bool  WaveBandLimit = true;     // Crest-style per-component wave fade by local grid cell (seam-reset fix)

        private static readonly int Prop_FresnelPower = Shader.PropertyToID("_FiresFresnelPower");
        private static readonly int Prop_ReflectionFloor = Shader.PropertyToID("_FiresReflectionFloor");
        private static readonly int Prop_ReflectionMax = Shader.PropertyToID("_FiresReflectionBlend");
        private static readonly int Prop_FoamSlope = Shader.PropertyToID("_FiresFoamSlopeDriven");
        private static readonly int Prop_WaveOpacity = Shader.PropertyToID("_FiresWaveOpacity");
        private static readonly int Prop_CausticStrength = Shader.PropertyToID("_FiresCausticStrength");
        private static readonly int Prop_CausticScale = Shader.PropertyToID("_FiresCausticScale");
        private static readonly int Prop_CausticSpeed = Shader.PropertyToID("_FiresCausticSpeed");
        private static readonly int Prop_CausticWarp = Shader.PropertyToID("_FiresCausticWarp");
        private static readonly int Prop_CausticSharp = Shader.PropertyToID("_FiresCausticSharp");
        private static readonly int Prop_NormalSpeed = Shader.PropertyToID("_FiresNormalSpeed");
        private static readonly int Prop_FoamSunBoost = Shader.PropertyToID("_FiresFoamSunBoost");
        private static readonly int Prop_CausticTex = Shader.PropertyToID("_FiresCausticTex");
        private static readonly int Prop_CausticUseTex = Shader.PropertyToID("_FiresCausticUseTex");
        private static readonly int Prop_UWCausticScale = Shader.PropertyToID("_FiresUWCausticScale");
        private static readonly int Prop_UWCausticSharp = Shader.PropertyToID("_FiresUWCausticSharp");
        private static readonly int Prop_UWCausticStrength = Shader.PropertyToID("_FiresUWCausticStrength");
        private static readonly int Prop_UWCausticSpeed = Shader.PropertyToID("_FiresUWCausticSpeed");
        private static readonly int Prop_UWCausticWarp = Shader.PropertyToID("_FiresUWCausticWarp");
        private static readonly int Prop_UnderwaterDapple = Shader.PropertyToID("_FiresUnderwaterDapple");
        private const float UnderwaterDappleBaseStrength = 0.6f;   // matches the old per-material default
        private static readonly int Prop_SurfaceFogAmount = Shader.PropertyToID("_FiresSurfaceFogAmount");
        private static readonly int Prop_FogMode = Shader.PropertyToID("_FiresFogMode");
        private static readonly int Prop_DistantReflect = Shader.PropertyToID("_FiresDistantReflect");
        private static readonly int Prop_DistantTint = Shader.PropertyToID("_FiresDistantTint");
        private static readonly int Prop_DistantBright = Shader.PropertyToID("_FiresDistantBright");
        private static readonly int Prop_SSSColor = Shader.PropertyToID("_FiresSSSColor");
        private static readonly int Prop_SSSBase = Shader.PropertyToID("_FiresSSSBase");
        private static readonly int Prop_SSSSun = Shader.PropertyToID("_FiresSSSSun");
        private static readonly int Prop_SSSSunFalloff = Shader.PropertyToID("_FiresSSSSunFalloff");
        private static readonly int Prop_SSSWaveBoost = Shader.PropertyToID("_FiresSSSWaveBoost");
        private static readonly int Prop_AbsorbBlend = Shader.PropertyToID("_FiresAbsorbBlend");
        private static readonly int Prop_AbsorbExtinction = Shader.PropertyToID("_FiresAbsorbExtinction");
        private static readonly int Prop_FoamBubbleIntensity = Shader.PropertyToID("_FiresFoamBubbleIntensity");
        private static readonly int Prop_DistNormalFade = Shader.PropertyToID("_FiresDistNormalFade");
        private static readonly int Prop_DistSpecRough = Shader.PropertyToID("_FiresDistSpecRough");
        private static readonly int Prop_SchlickBlend = Shader.PropertyToID("_FiresSchlickBlend");
        private static readonly int Prop_NadirReflCap = Shader.PropertyToID("_FiresNadirReflCap");
        private static readonly int Prop_PixelDepth = Shader.PropertyToID("_FiresPixelDepth");
        private static readonly int Prop_SunSpecScale = Shader.PropertyToID("_FiresSunSpecScale");
        private static readonly int Prop_WaveTexels = Shader.PropertyToID("_FiresWaveTexels");

        // RenderSettings.fogMode → shader int (0=Linear, 1=Exp, 2=Exp2). Valheim
        // never changes the mode at runtime (EnvMan only drives density/colour),
        // so the shader can replicate the EXACT per-mode Unity fog factor.
        //
        // CRITICAL: RenderSettings.fogMode is only safe to read once a real game
        // scene with bound lighting exists. At plugin Awake (BepInEx chainloader,
        // bootstrap scene) the native get_fogMode getter ACCESS-VIOLATES under
        // Unity 6 — there is no RenderSettings object yet, and it hard-crashes the
        // process before the main menu. EnvMan only exists in-game, so it is a
        // clean "scene is up" gate. Until then we return Valheim's actual mode
        // (Exp2); the in-game FiresWaterApplyWhenReady push (gated on ZNetScene)
        // re-reads the real value once the world — and EnvMan — are live.
        private static float FogModeId()
        {
            if (EnvMan.instance == null) return 2f;   // pre-scene → safe default (ExponentialSquared)
            switch (RenderSettings.fogMode)
            {
                case FogMode.Linear: return 0f;
                case FogMode.Exponential: return 1f;
                default: return 2f;   // ExponentialSquared (Valheim default)
            }
        }

        // Authored caustic texture name as it appears in the AssetBundle
        // (the imported file is CausticFireWater.png). Loaded once from the
        // FiresWater bundle and bound as a global; until it's present the
        // shader uses the procedural Worley fallback (_FiresCausticUseTex=0).
        private const string CausticTexName = "CausticFireWater";
        private static Texture2D s_causticTex;
        private static bool s_causticWarned;

        private static bool s_pushed;

        public static void Push()
        {
            Shader.SetGlobalFloat(Prop_FresnelPower, FresnelPower);
            Shader.SetGlobalFloat(Prop_ReflectionFloor, ReflectionFloor);
            Shader.SetGlobalFloat(Prop_ReflectionMax, ReflectionMax);
            Shader.SetGlobalFloat(Prop_FoamSlope, FoamSlopeDriven);
            Shader.SetGlobalFloat(Prop_WaveOpacity, WaveOpacity);
            Shader.SetGlobalFloat(Prop_CausticStrength, SurfaceCausticsEnabled ? CausticStrength : 0f);
            Shader.SetGlobalFloat(Prop_CausticScale, CausticScale);
            Shader.SetGlobalFloat(Prop_CausticSpeed, CausticSpeed);
            Shader.SetGlobalFloat(Prop_CausticWarp, CausticWarp);
            Shader.SetGlobalFloat(Prop_CausticSharp, CausticSharp);
            Shader.SetGlobalFloat(Prop_NormalSpeed, NormalSpeed);
            Shader.SetGlobalFloat(Prop_FoamSunBoost, FoamSunBoost);
            Shader.SetGlobalFloat(Prop_UWCausticScale, UWCausticScale);
            Shader.SetGlobalFloat(Prop_UWCausticSharp, UWCausticSharp);
            Shader.SetGlobalFloat(Prop_UWCausticStrength, UWCausticStrength);
            Shader.SetGlobalFloat(Prop_UWCausticSpeed, UWCausticSpeed);
            Shader.SetGlobalFloat(Prop_UWCausticWarp, UWCausticWarp);
            Shader.SetGlobalFloat(Prop_UnderwaterDapple, UnderwaterDappleEnabled ? UnderwaterDappleBaseStrength : 0f);
            Shader.SetGlobalFloat(Prop_SurfaceFogAmount, SurfaceFogAmount);
            Shader.SetGlobalFloat(Prop_FogMode, FogModeId());
            Shader.SetGlobalFloat(Prop_DistantReflect, DistantReflect);
            Shader.SetGlobalColor(Prop_DistantTint, DistantTint);
            Shader.SetGlobalFloat(Prop_DistantBright, DistantBright);
            Shader.SetGlobalColor(Prop_SSSColor, SSSColor);
            Shader.SetGlobalFloat(Prop_SSSBase, SSSBase);
            Shader.SetGlobalFloat(Prop_SSSSun, SSSSun);
            Shader.SetGlobalFloat(Prop_SSSSunFalloff, SSSSunFalloff);
            Shader.SetGlobalFloat(Prop_SSSWaveBoost, SSSWaveBoost);
            Shader.SetGlobalFloat(Prop_AbsorbBlend, AbsorbBlend);
            Shader.SetGlobalColor(Prop_AbsorbExtinction, AbsorbExtinction);
            Shader.SetGlobalFloat(Prop_FoamBubbleIntensity, FoamBubbleIntensity);
            Shader.SetGlobalFloat(Prop_DistNormalFade, DistNormalFade);
            Shader.SetGlobalFloat(Prop_DistSpecRough, DistSpecRough);
            Shader.SetGlobalFloat(Prop_SchlickBlend, SchlickBlend);
            Shader.SetGlobalFloat(Prop_NadirReflCap, NadirReflCap);
            Shader.SetGlobalFloat(Prop_PixelDepth, PixelDepth ? 1f : 0f);
            Shader.SetGlobalFloat(Prop_SunSpecScale, Mathf.Max(0f, SunSpecScale));
            Shader.SetGlobalFloat(Prop_WaveTexels, WaveBandLimit ? 3f : 0f);
            EnsureCausticTexture();

            if (!s_pushed)
            {
                FiresLogger.LogVerbose("[FiresWaterTuning] Pushed surface globals "
                        + $"(fresnelPower={FresnelPower}, reflFloor={ReflectionFloor}, "
                        + $"reflMax={ReflectionMax}, foamSlope={FoamSlopeDriven}, "
                        + $"waveOpacity={WaveOpacity}, caustic={CausticStrength}/{CausticScale}/{CausticSpeed}).");
                s_pushed = true;
            }
        }

        // Lazily load the authored caustic texture from the FiresWater bundle and
        // bind it as a global, flipping _FiresCausticUseTex on. Until it's found
        // the shader stays on the procedural Worley fallback. Cheap once loaded
        // (early-out); re-tries each Push while the bundle/texture isn't ready.
        private static void EnsureCausticTexture()
        {
            if (s_causticTex != null) { Shader.SetGlobalFloat(Prop_CausticUseTex, 1f); return; }

            var bundle = FiresWaterShaderLoader.Bundle;
            if (bundle == null) { Shader.SetGlobalFloat(Prop_CausticUseTex, 0f); return; }  // bundle not up yet

            s_causticTex = bundle.LoadAsset<Texture2D>(CausticTexName);
            if (s_causticTex == null)
            {
                foreach (var t in bundle.LoadAllAssets<Texture2D>())
                    if (t != null && t.name == CausticTexName) { s_causticTex = t; break; }
            }

            if (s_causticTex != null)
            {
                s_causticTex.wrapMode = TextureWrapMode.Repeat;   // world-tiled projection needs Repeat
                Shader.SetGlobalTexture(Prop_CausticTex, s_causticTex);
                Shader.SetGlobalFloat(Prop_CausticUseTex, 1f);
                FiresLogger.LogVerbose($"[FiresWaterTuning] Caustic texture '{CausticTexName}' loaded from bundle — using textured caustics.");
            }
            else
            {
                Shader.SetGlobalFloat(Prop_CausticUseTex, 0f);
                if (!s_causticWarned)
                {
                    Debug.LogWarning($"[FiresWaterTuning] Caustic texture '{CausticTexName}' not in the FiresWater bundle — using procedural fallback. Assign it to the 'fireswater' AssetBundle and rebuild.");
                    s_causticWarned = true;
                }
            }
        }
    }
}
