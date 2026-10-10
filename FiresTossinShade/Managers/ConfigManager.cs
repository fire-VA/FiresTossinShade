using BepInEx.Configuration;
using System;

namespace VerdantsAscent.Managers
{
    // Slim ConfigManager for FiresTossinShade — exposes only the configs
    // that CustomTerrain and CustomWater actually consume.
    //
    // ORGANIZATION (2026-06-01): sections + entry keys carry zero-padded
    // numeric prefixes ("01 - General", "01 VerboseLogging") so both the
    // generated .cfg and the in-game ConfigurationManager UI list them in
    // intent order rather than alphabetically. Field declarations below are
    // grouped to mirror the Initialize() section order 1:1.
    //
    // C# field names are STABLE — only the BepInEx section/key strings carry
    // the numbering, so renumbering never touches consuming code (the plugin
    // references the fields, not the strings). Changing section/key strings
    // does orphan previously-saved values in the .cfg; they regenerate at the
    // defaults below on next launch.
    //
    // ConfigSync: configServerAuthority is the locking toggle (server forces
    // clients to match); the rest are unlocked client-side tunables. Hook
    // configSync.lockedConfigChanged -> OnConfigLockChanged in plugin Awake.
    public sealed class ConfigManager : IDisposable
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ??= new ConfigManager();

        // ===== 00 - Master =====
        // One switch that turns OFF every cosmetic shader override (vanilla graphics mode). The water
        // stack is exempt — water surface, underwater environments/bubbles, caustics, shore and the
        // ocean rings stay on because Galaxies' tides/currents and swimming ride on them.
        public ConfigEntry<bool> configMasterFxEnabled;

        public const string PresetModName = "FiresTossinShade";
        public const string NoPresetOption = "(none)";

        public ConfigEntry<string> configPresetToLoad;
        public ConfigEntry<string> configPresetSaveName;
        public ConfigEntry<bool> configPresetSaveNow;
        public ConfigEntry<string> configServerPreset;
        public ConfigEntry<bool> configAdminOnlyTuning;

        // Sections whose settings every player should reach: the master switch, presets, and general.
        private static readonly string[] AlwaysBasicSectionPrefixes = { "00 ", "01 - General" };

        // Per-system on/off switches that do not carry the word "Enabled" in their key. Everything named
        // "*Enabled*" is picked up by the rule in IsAdvancedTuning without being listed here.
        private static readonly System.Collections.Generic.HashSet<string> AlwaysBasicKeys =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                "01 SkyboxOverride",
                "02 SwapVanillaOcean",
                "01 HideVanillaLodWater",
                "09 UseClipmapOcean",
            };

        /// <summary>
        /// True for the deep tuning settings — everything except the master/preset/general sections and the
        /// per-shader on/off toggles. Drives both the Advanced filter and the admin lock.
        /// </summary>
        public static bool IsAdvancedTuning(ConfigDefinition definition)
        {
            foreach (string prefix in AlwaysBasicSectionPrefixes)
                if (definition.Section.StartsWith(prefix, StringComparison.Ordinal)) return false;

            if (AlwaysBasicKeys.Contains(definition.Key)) return false;
            return definition.Key.IndexOf("Enabled", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string[] BuildPresetOptions()
        {
            var options = new System.Collections.Generic.List<string> { NoPresetOption };
            options.AddRange(FiresCore.Config.FiresConfigPresets.ListPresetNames(PresetModName));
            return options.ToArray();
        }

        // Null-safe read for the gate sites in FiresAdminTerrain (true before Initialize has run).
        public static bool MasterFx =>
            _instance == null || _instance.configMasterFxEnabled == null || _instance.configMasterFxEnabled.Value;

        // ===== 01 - General =====
        public ConfigEntry<bool> configVerboseLogging;
        public ConfigEntry<bool> configServerAuthority;

        // ===== 04 - FiresWater Shader =====
        public ConfigEntry<bool> configFiresWaterEnabled;
        public ConfigEntry<bool> configFiresWaterSwapVanilla;
        public ConfigEntry<bool> configFiresWaterUnderwaterEnv;
        public ConfigEntry<bool> configFiresWaterUnderwaterBubbles;
        public ConfigEntry<bool> configFiresWaterUnderwaterDepthFog;
        public ConfigEntry<bool> configFiresWaterUnderwaterEnvReactive;
        public ConfigEntry<bool> configFiresLightningFxBlend;
        public ConfigEntry<float> configFiresWaterUnderwaterTransition;
        public ConfigEntry<float> configFiresWaterUnderwaterFogDensity;
        public ConfigEntry<float> configFiresWaterBubbleDensity;
        public ConfigEntry<float> configFiresWaterBubbleSpeed;

        // ===== 21 - FiresLava (Ashlands lava shader swap + live tuning) =====
        public ConfigEntry<bool> configFiresLavaEnabled;
        public ConfigEntry<float> configFiresLavaEmissionBoost;
        public ConfigEntry<bool> configFiresLavaGlowFromTexture;
        public ConfigEntry<float> configFiresLavaFlowSpeed;
        public ConfigEntry<UnityEngine.Color> configFiresLavaMoltenColor;
        public ConfigEntry<float> configFiresLavaMetallic;
        public ConfigEntry<float> configFiresLavaSmoothness;

        // ===== 17 - FiresHeightmap (terrain shader swap + Ashlands lava biome-paint tuning) =====
        public ConfigEntry<bool> configFiresHeightmapEnabled;
        public ConfigEntry<float> configFiresHeightmapLavaBrightness;
        public ConfigEntry<UnityEngine.Color> configFiresHeightmapLavaTint;
        public ConfigEntry<float> configFiresHeightmapFlowSpeed;
        public ConfigEntry<int> configFiresHeightmapArrayLayer;
        public ConfigEntry<float> configFiresHeightmapLavaCoverage;
        public ConfigEntry<float> configFiresHeightmapLavaThreshold;

        // ===== 18 - FiresMist (Mistlands heavymist particle shader swap + tuning) =====
        public ConfigEntry<bool> configFiresMistEnabled;
        public ConfigEntry<UnityEngine.Color> configFiresMistTint;
        public ConfigEntry<float> configFiresMistBrightness;
        public ConfigEntry<float> configFiresMistDensity;
        public ConfigEntry<float> configFiresMistFadeScale;
        public ConfigEntry<float> configFiresMistEdgePower;
        public ConfigEntry<float> configFiresMistLightResponse;
        public ConfigEntry<float> configFiresMistAltitude;
        public ConfigEntry<float> configFiresMistPatchiness;
        public ConfigEntry<float> configFiresMistPatchScale;
        public ConfigEntry<float> configFiresMistWindDrift;
        public ConfigEntry<float> configFiresMistPushStrength;
        public ConfigEntry<float> configFiresMistPushRadius;
        public ConfigEntry<float> configFiresMistCoverage;
        public ConfigEntry<float> configFiresMistCoverScale;
        public ConfigEntry<UnityEngine.Color> configFiresMistDenseTint;
        public ConfigEntry<float> configFiresMistTwoTone;
        public ConfigEntry<float> configFiresMistWeatherColor;
        public ConfigEntry<float> configFiresMistWeatherDensity;

        // ===== 19 - FiresPiece (building-piece shader swap + wet/reflection tuning) =====
        public ConfigEntry<bool> configFiresPieceEnabled;
        public ConfigEntry<float> configFiresPieceWetStrength;
        public ConfigEntry<float> configFiresPieceWetSmoothness;
        public ConfigEntry<float> configFiresPieceSmoothnessScale;
        public ConfigEntry<float> configFiresPieceMetallicScale;

        // ===== 20 - FiresGlass (stained-glass light projection + godrays) =====
        public ConfigEntry<bool> configFiresGlassEnabled;
        public ConfigEntry<float> configFiresGlassIntensity;
        public ConfigEntry<float> configFiresGlassReach;
        public ConfigEntry<float> configFiresGlassSoftness;
        public ConfigEntry<bool> configFiresGlassUseVanillaShadow;
        public ConfigEntry<float> configFiresGlassShadowStrength;
        public ConfigEntry<float> configFiresGlassShadowBias;
        public ConfigEntry<float> configFiresGlassShadowThickness;
        public ConfigEntry<float> configFiresGlassGodrayDensity;
        public ConfigEntry<float> configFiresGlassNeonEmission;
        public ConfigEntry<float> configFiresGlassNeonCastIntensity;
        public ConfigEntry<float> configFiresGlassNeonCastAngle;
        public ConfigEntry<bool> configFiresGlassNeonBeamFront;

        // ===== 10 - FiresPlayer (character shader swap + eye color) =====
        public ConfigEntry<bool> configFiresPlayerEnabled;
        public ConfigEntry<UnityEngine.Color> configFiresPlayerEyeColor;
        public ConfigEntry<float> configFiresPlayerEyeThreshold;

        // ===== 10b - Elf Ears (procedural pointed ears on the local player) =====
        public ConfigEntry<bool> configElfEarsEnabled;
        public ConfigEntry<float> configElfEarsOffsetX, configElfEarsOffsetY, configElfEarsOffsetZ;
        public ConfigEntry<float> configElfEarsScaleX, configElfEarsScaleY, configElfEarsScaleZ, configElfEarsPitch, configElfEarsYaw, configElfEarsRoll;
        public ConfigEntry<UnityEngine.Color> configElfEarsColor;
        public ConfigEntry<string> configElfEarsSet;

        // 10d - Hair & Beard Scale (per-axis size of the vanilla hair/beard meshes)
        public ConfigEntry<bool> configHairScaleEnabled, configBeardScaleEnabled;
        public ConfigEntry<float> configHairScaleX, configHairScaleY, configHairScaleZ;
        public ConfigEntry<float> configBeardScaleX, configBeardScaleY, configBeardScaleZ;

        // 10e - Player Model (prototype body-mesh swap) — REMOVED FOR NOW (didn't pan out). Configs commented out so the
        // model can't be changed until it's corrected. FiresPlayerModel.cs is kept. Uncomment these + the Bind block in
        // Setup() + the wiring in FiresAdminTerrain/FiresElfEars + re-embed the firesplayermodels bundle (csproj) to restore.
        // public ConfigEntry<bool> configPlayerModelEnabled;
        // public ConfigEntry<float> configPlayerModelInflate;
        // public ConfigEntry<int> configPlayerModelIndex;

        // ===== 10c - Hair Physics (verlet swing on the local player's hair + beard) =====
        public ConfigEntry<bool> configHairPhysEnabled;
        public ConfigEntry<float> configHairStiffness, configHairDamping, configHairGravity, configHairWind, configHairMaxSway, configHairCollision;
        public ConfigEntry<float> configHairBodyRadius, configHairBodyTop, configHairBodyBottom;
        public ConfigEntry<float> configHairShoulderRadius, configHairShoulderWidth;
        public ConfigEntry<float> configHairAnchorSide, configHairAnchorUp, configHairAnchorForward;
        public ConfigEntry<float> configBeardAnchorSide, configBeardAnchorUp, configBeardAnchorForward;
        public ConfigEntry<float> configHairSwayFalloff;

        // ===== 10d - Hair & Beard (style + color on the local player) =====
        public ConfigEntry<int> configHairStyle, configBeardStyle;
        public ConfigEntry<bool> configHairColorEnabled, configBeardColorEnabled;
        public ConfigEntry<UnityEngine.Color> configHairColor, configBeardColor;

        // ===== 04 - FiresWater Surface (live shader tuning — pushed as globals) =====
        public ConfigEntry<float> configFiresWaterFoamLayerA;
        public ConfigEntry<float> configFiresWaterFoamLayerB;
        public ConfigEntry<float> configFiresWaterFoamSlope;
        public ConfigEntry<float> configFiresWaterFoamWaveHeight;
        public ConfigEntry<float> configFiresWaterReflectionFloor;
        public ConfigEntry<float> configFiresWaterReflectionMax;
        public ConfigEntry<float> configFiresWaterRefractShoreFade;
        public ConfigEntry<float> configFiresWaterRefractNearStart;
        public ConfigEntry<float> configFiresWaterRefractNearRange;
        public ConfigEntry<float> configFiresWaterFresnelSoftness;
        public ConfigEntry<float> configFiresWaterWaveOpacity;
        public ConfigEntry<float> configFiresWaterFoamScrollSpeed;
        public ConfigEntry<bool> configFiresWaterSurfaceCausticsEnabled;
        public ConfigEntry<float> configFiresWaterCausticStrength;
        public ConfigEntry<float> configFiresWaterCausticPassStrength;
        public ConfigEntry<float> configFiresWaterCausticMoonScale;
        public ConfigEntry<float> configFiresWaterCausticScale;
        public ConfigEntry<float> configFiresWaterCausticSpeed;
        public ConfigEntry<float> configFiresWaterCausticWarp;
        public ConfigEntry<float> configFiresWaterCausticSharp;
        public ConfigEntry<float> configFiresWaterNormalSpeed;
        public ConfigEntry<float> configFiresWaterFoamSunBoost;
        public ConfigEntry<float> configFiresWaterCausticShadow;
        public ConfigEntry<float> configFiresWaterCausticDepthFade;
        public ConfigEntry<bool> configFiresWaterBelowWaterCaustics;
        public ConfigEntry<bool> configFiresWaterCausticDebug;
        public ConfigEntry<float> configFiresWaterUWCausticScale;
        public ConfigEntry<float> configFiresWaterUWCausticSharpness;
        public ConfigEntry<float> configFiresWaterUWCausticStrength;
        public ConfigEntry<float> configFiresWaterUWCausticSpeed;
        public ConfigEntry<float> configFiresWaterUWCausticWarp;
        public ConfigEntry<bool> configFiresWaterUnderwaterDapple;
        public ConfigEntry<float> configFiresWaterSurfaceFog;
        public ConfigEntry<bool> configFiresWaterMeniscusEnabled;
        public ConfigEntry<float> configFiresWaterMeniscusStrength;
        public ConfigEntry<float> configFiresWaterMeniscusWidth;
        public ConfigEntry<bool> configFiresWaterShipMaskFit;
        public ConfigEntry<float> configFiresWaterShipMaskMargin;
        public ConfigEntry<bool> configFiresWaterPlanarRefl;
        public ConfigEntry<float> configFiresWaterPlanarReflResolution;
        public ConfigEntry<float> configFiresWaterPlanarReflDistort;
        public ConfigEntry<float> configFiresWaterPlanarReflMaxHeight;
        public ConfigEntry<bool> configFiresWaterPlanarReflTerrain;
        public ConfigEntry<UnityEngine.Color> configFiresWaterSSSColor;
        public ConfigEntry<float> configFiresWaterSSSBase;
        public ConfigEntry<float> configFiresWaterSSSSun;
        public ConfigEntry<float> configFiresWaterSSSSunFalloff;
        public ConfigEntry<float> configFiresWaterSSSWaveBoost;
        public ConfigEntry<float> configFiresWaterAbsorbBlend;
        public ConfigEntry<UnityEngine.Color> configFiresWaterAbsorbExtinction;
        public ConfigEntry<float> configFiresWaterFoamBubbleIntensity;
        public ConfigEntry<float> configFiresWaterDistNormalFade;
        public ConfigEntry<float> configFiresWaterDistSpecRough;
        public ConfigEntry<float> configFiresWaterSchlickFresnel;
        public ConfigEntry<float> configFiresWaterNadirReflCap;
        public ConfigEntry<float> configFiresWaterSunSpec;
        public ConfigEntry<bool> configFiresWaterWaveBandLimit;
        public ConfigEntry<float> configFiresWaterPlanarReflBankClip;
        public ConfigEntry<bool> configFiresWaterPixelDepth;
        public ConfigEntry<float> configFiresWaterCausticRadius;

        // ===== Distance fog (vanilla RenderSettings fog master control) =====
        public ConfigEntry<bool> configDistanceFogEnabled;
        public ConfigEntry<float> configDistanceFogDensityScale;
        public ConfigEntry<bool> configDistanceFogColorOverride;
        public ConfigEntry<UnityEngine.Color> configDistanceFogColor;
        public ConfigEntry<VerdantsAscent.Water.Core.FiresFogControl.ModeOption> configDistanceFogMode;
        public ConfigEntry<float> configDistanceFogLinearStart;
        public ConfigEntry<float> configDistanceFogLinearEnd;

        // ===== Volumetric fog — master + 3 zones: land / sea / underwater =====
        public ConfigEntry<bool> configVolFogEnabled;
        public ConfigEntry<bool> configVolFogDomainConfine;
        public ConfigEntry<float> configVolFogSkyBlend;
        public ConfigEntry<bool> configVolFogCaveAware;   // 0.3.154
        public ConfigEntry<float> configVolFogCaveFade;
        public ConfigEntry<float> configVolFogUwPerObject;
        public ConfigEntry<float> configVolFogCaveRender;
        public ConfigEntry<float> configVolFogCaveHold;
        public ConfigEntry<bool> configVfShaftOn;   // 0.3.155
        public ConfigEntry<UnityEngine.Color> configVfShaftColor;
        public ConfigEntry<float> configVfShaftDensity, configVfShaftMaxOp, configVfShaftReach, configVfShaftTopGap, configVfShaftVFade;
        // Land
        public ConfigEntry<bool> configVfLandOn;
        public ConfigEntry<UnityEngine.Color> configVfLandColor, configVfLandColorThin;
        public ConfigEntry<float> configVfLandMin, configVfLandMax, configVfLandBotFade, configVfLandTopFade, configVfLandDensity, configVfLandMaxOp;
        public ConfigEntry<float> configVfLandPatchScale, configVfLandPatch, configVfLandCoverage, configVfLandClearOp, configVfLandPatchFade, configVfLandWind, configVfLandFarCut, configVfLandUndulate;
        // Sea (above-water, on the water surface)
        public ConfigEntry<bool> configVfSeaOn;
        public ConfigEntry<UnityEngine.Color> configVfSeaColor, configVfSeaColorThin;
        public ConfigEntry<float> configVfSeaBelow, configVfSeaThick, configVfSeaBotFade, configVfSeaTopFade, configVfSeaDensity, configVfSeaMaxOp;
        public ConfigEntry<float> configVfSeaPatchScale, configVfSeaPatch, configVfSeaCoverage, configVfSeaClearOp, configVfSeaPatchFade, configVfSeaWind, configVfSeaFarCut, configVfSeaUndulate;
        // Underwater
        public ConfigEntry<bool> configVfUwOn;
        public ConfigEntry<UnityEngine.Color> configVfUwColor, configVfUwColorThin;
        public ConfigEntry<float> configVfUwDensity, configVfUwMaxOp, configVfUwPatchScale, configVfUwSwirl, configVfUwCurrent, configVfUwDistStart, configVfUwDistRange, configVfUwFarCut;
        public ConfigEntry<float> configVfUwCoverage, configVfUwPatch, configVfUwClearOp, configVfUwPatchFade, configVfUwDepthDensity, configVfUwDepthSpan;
        // Player push on the land + sea fog — shoves the mist aside around you as you move
        public ConfigEntry<bool> configVfPushOn;
        public ConfigEntry<float> configVfPushRadius, configVfPushStrength;
        // Weather reactivity (drives land/sea fog colour + density from the live env)
        public ConfigEntry<bool> configVfEnvReactive, configVfEnvAffectUw;
        public ConfigEntry<float> configVfEnvColorBlend, configVfEnvDensityBlend, configVfEnvRainBoost, configVfEnvStormOpacity;
        public ConfigEntry<float> configVfClearDayFog;
        // Cloud layer (08f) — high-altitude volumetric cloud band
        public ConfigEntry<bool> configVfCloudOn;
        public ConfigEntry<UnityEngine.Color> configVfCloudColor;
        public ConfigEntry<float> configVfCloudMin, configVfCloudMax, configVfCloudBotFade, configVfCloudTopFade, configVfCloudDensity, configVfCloudMaxOp, configVfCloudCoverage, configVfCloudPatch, configVfCloudPatchScale, configVfCloudUndulate, configVfCloudDrift;

        // ===== Shore field — shore-ramped waves + depth-darkened water =====
        public ConfigEntry<bool> configShoreFieldEnabled;
        public ConfigEntry<float> configShoreWaveFloor, configShoreWaveFullDepth, configShoreDepthDarkness, configShoreDepthForDark, configShoreFieldExtent, configShoreFogDomainBlend;
        public ConfigEntry<int> configShoreFieldResolution;

        // ===== 17 - FiresPlanet (cloud dome as a planet from space) =====
        public ConfigEntry<bool> configFiresPlanetEnabled;
        public ConfigEntry<UnityEngine.Color> configFiresPlanetOceanColor;
        public ConfigEntry<float> configFiresPlanetSurfaceCloudOpacity;
        public ConfigEntry<float> configFiresPlanetCloudCoverage;
        public ConfigEntry<float> configFiresPlanetCloudSoftness;
        public ConfigEntry<float> configFiresPlanetCloudScale;
        public ConfigEntry<float> configFiresPlanetCloudScrollSpeed;
        public ConfigEntry<float> configFiresPlanetDomeRadius;
        public ConfigEntry<float> configFiresPlanetMapOpacity;
        public ConfigEntry<float> configFiresPlanetVibrance;
        public ConfigEntry<float> configFiresPlanetHue;
        public ConfigEntry<bool> configFiresPlanetHideWorld;
        public ConfigEntry<float> configFiresPlanetDomeHeight;
        public ConfigEntry<bool> configFiresPlanetTieWaterToDome;

        // ===== 05 - Ocean Ring (real-zone water extension around the player) =====
        public ConfigEntry<bool> configOceanRingExtendEnabled;
        public ConfigEntry<int> configOceanRingExtendedActiveArea;

        // ===== 06 - Far Water Disk (FiresWater fill past the real water) =====
        public ConfigEntry<bool> configFiresWaterExtendedRingEnabled;
        public ConfigEntry<float> configFiresWaterFollowRadius;
        public ConfigEntry<float> configRingWaveDetailDistance;
        public ConfigEntry<float> configRingWaveDetailFloor;
        public ConfigEntry<int> configRingSeamOverlap;
        public ConfigEntry<float> configDistantReflection, configDistantBrightness;
        public ConfigEntry<UnityEngine.Color> configDistantTint;
        // Clipmap ocean — Crest-style cascade-ring ALTERNATIVE to the disk (09-12).
        public ConfigEntry<bool> configUseClipmapOcean;
        public ConfigEntry<float> configClipmapOceanInnerHole;
        public ConfigEntry<float> configClipmapOceanRadius;
        public ConfigEntry<int> configClipmapOceanLods;
        public ConfigEntry<int> configClipmapOceanResolution;

        // ===== 07 - Vanilla LOD Plane =====
        public ConfigEntry<bool> configHideVanillaLodWater;

        // ===== 08 - FiresSky (procedural skybox overrides + day-sky knobs) =====
        public ConfigEntry<bool> configFiresSkyboxOverride;
        public ConfigEntry<float> configFiresSkyboxAtmosphereThicknessMul;
        public ConfigEntry<float> configFiresSkyboxFogHeightMinOffset;
        public ConfigEntry<float> configFiresSkyboxFogHeightMaxOffset;
        public ConfigEntry<float> configFiresSkyboxGroundColorBlendToSky;
        public ConfigEntry<float> configFiresSkyboxExposureMul;
        public ConfigEntry<float> configFiresSkyboxStormDarken;
        public ConfigEntry<float> configFiresSkyboxStarExposureMul;
        public ConfigEntry<float> configFiresSkyboxSunSizeMul;
        public ConfigEntry<float> configFiresSkyboxSunDiskBrightness;
        public ConfigEntry<float> configFiresSkyboxMoonSizeMul;
        public ConfigEntry<float> configFiresSkyboxSunLightMul;
        public ConfigEntry<float> configFiresSkyboxMoonLightMul;
        public ConfigEntry<float> configDaySkyHue;
        public ConfigEntry<float> configDaySkyDensity;
        public ConfigEntry<float> configStarFadeBySky;
        public ConfigEntry<UnityEngine.Color> configDaySkyColor;

        // ===== 10a - Sun Haze =====
        public ConfigEntry<bool> configSunHazeEnabled;
        public ConfigEntry<float> configSunHazeClearDiskBoost;
        public ConfigEntry<float> configSunHazeHazyDiskBoost;
        public ConfigEntry<float> configSunHazeClearSunSize;
        public ConfigEntry<float> configSunHazeHazySunSize;
        public ConfigEntry<float> configSunHazeLowSunElevation;
        public ConfigEntry<float> configSunHazeLowSunWeight;
        public ConfigEntry<float> configSunHazeWeatherWeight;

        // ===== 09 - FiresAtmosphere (vanilla cloud cylinder/plane rescale) =====
        public ConfigEntry<bool> configFiresAtmosphereEnabled;
        public ConfigEntry<float> configFiresAtmosphereCylinderRadius;
        public ConfigEntry<float> configFiresAtmosphereCylinderHeight;
        public ConfigEntry<float> configFiresAtmosphereCloudPlaneRadius;
        public ConfigEntry<float> configFiresAtmosphereCloudCoverage;
        public ConfigEntry<float> configStormDarken, configStormCoverage, configStormOpacity;
        public ConfigEntry<float> configFiresAtmosphereCloudWorldScale;
        public ConfigEntry<float> configFiresAtmosphereCloudSoftness;
        public ConfigEntry<float> configFiresAtmosphereCloudOpacity;
        public ConfigEntry<float> configFiresAtmosphereCloudDetailStrength;
        public ConfigEntry<float> configFiresAtmosphereCloudScrollSpeed;
        public ConfigEntry<float> configFiresAtmosphereCloudBrightness;
        public ConfigEntry<float> configFiresAtmosphereCloudAltFadeStart;
        public ConfigEntry<float> configFiresAtmosphereCloudAltFadeEnd;
        public ConfigEntry<float> configFiresAtmosphereCloudDensity;
        public ConfigEntry<float> configFiresAtmosphereCloudHue;

        // ===== 11 - Aurora Skybox (primary path: FiresSkyboxAurora shader) =====
        public ConfigEntry<bool> configAuroraOverlayEnabled;
        public ConfigEntry<float> configAuroraOverlayIntensity;
        public ConfigEntry<float> configAuroraNorthBiasPower;
        public ConfigEntry<float> configAuroraContribCap;
        public ConfigEntry<float> configAuroraAltMin;
        public ConfigEntry<float> configAuroraAltMax;
        public ConfigEntry<float> configAuroraColumnFreq;
        public ConfigEntry<float> configAuroraVertFreq;
        public ConfigEntry<float> configAuroraSharpness;
        public ConfigEntry<float> configAuroraScrollSpeed;
        public ConfigEntry<float> configAuroraPulseSpeed;
        public ConfigEntry<bool> configAuroraNightOnly;
        public ConfigEntry<bool> configAuroraDuringStorms;

        // ===== 12 - Aurora Glow (legacy horizon-glow backdrop) =====
        public ConfigEntry<bool> configAuroraGlowEnabled;
        public ConfigEntry<float> configAuroraGlowIntensity;
        public ConfigEntry<float> configAuroraGlowSpeed;
        public ConfigEntry<float> configAuroraGlowAtmosphereMul;
        public ConfigEntry<float> configAuroraGlowFogHeightMin;

        // ===== 14 - Aurora Light (real directional Light for reflections) =====
        public ConfigEntry<bool> configAuroraLightEnabled;
        public ConfigEntry<float> configAuroraLightIntensity;
        public ConfigEntry<float> configAuroraLightElevation;
        public ConfigEntry<float> configAuroraLightAzimuth;

        // ===== 15 - Aurora Shafts (second SunShafts pass) =====
        public ConfigEntry<bool> configAuroraShaftsEnabled;
        public ConfigEntry<float> configAuroraShaftsIntensity;
        public ConfigEntry<float> configAuroraShaftsBlurRadius;
        public ConfigEntry<float> configAuroraShaftsMaxRadius;

        public event Action ConfigLockChanged;

        // 0.3.158 (Fire 10-08: "renumber it now, but make sure to restore all of the values from the current config"): two section numbers were
        // taken twice (10 Player / Sky, 16 Shafts / Lava). Sky moved to the free 07, Lava to 21. On load every value still under an OLD section
        // name (BepInEx keeps it as an orphaned entry) is written into the same key under the new name, so nothing resets; logged once.
        internal static readonly string[][] RenamedSections = { new[] { "10 - FiresSky", "07 - FiresSky" }, new[] { "16 - FiresLava", "21 - FiresLava" } };

        public static int MigrateRenamedSections(ConfigFile cfg, bool log)
        {
            var orphans = HarmonyLib.Traverse.Create(cfg).Property("OrphanedEntries").GetValue<System.Collections.Generic.Dictionary<ConfigDefinition, string>>();
            if (orphans == null) return 0;
            int n = 0, missing = 0;
            foreach (var kv in System.Linq.Enumerable.ToList(orphans))
            {
                string to = null;
                foreach (var r in RenamedSections) if (kv.Key.Section == r[0]) to = r[1];
                if (to == null) continue;
                var def = new ConfigDefinition(to, kv.Key.Key);
                if (cfg.ContainsKey(def)) { cfg[def].SetSerializedValue(kv.Value); n++; }
                else missing++;
                orphans.Remove(kv.Key);
            }
            if (n > 0 || missing > 0) cfg.Save();
            if (log && (n > 0 || missing > 0)) UnityEngine.Debug.Log($"[FiresTossinShade] config: migrated {n} value(s) from the renumbered sections (10 - FiresSky -> 07, 16 - FiresLava -> 21){(missing > 0 ? $"; {missing} old key(s) no longer exist" : "")}");
            return n;
        }

        public void Initialize(ConfigFile config)
        {
            if (config == null) return;

            // ===================================================================
            // 00 - Master
            // ===================================================================
            configMasterFxEnabled = config.Bind(
                "00 - Master", "01 CustomShaderEffects", true,
                "MASTER SWITCH for all FiresTossinShade visual overrides. Turn OFF for vanilla graphics: "
                + "custom sky/skybox, atmosphere/clouds, planet, auroras, distance + volumetric fog, mist, "
                + "lava, terrain lava, building-piece shader, player shader and the stained-glass light "
                + "projection are all disabled and vanilla rendering is restored. The WATER stack stays ON "
                + "too (water surface, underwater environments/bubbles, caustics, shore, ocean rings, and the "
                + "hidden vanilla LOD plane comes back). Stained-glass windows remain placeable and visible; "
                + "only their light cast is disabled. Applies live; a relog cleans up any residue on "
                + "already-loaded objects.");

            configPresetToLoad = config.Bind(
                "00 - Master", "02 Preset", NoPresetOption,
                FiresCore.Config.FiresConfigChoices.InlineDescription(
                    "Pick a saved look and it applies immediately. Presets are plain .cfg files in "
                    + "BepInEx/config/FiresRPGmaker/Presets/FiresTossinShade/ — share one by copying the file. "
                    + "Files added while the game is running show up in this list after a restart; "
                    + "`fires_preset load <name>` reaches them straight away.",
                    BuildPresetOptions()));

            configPresetSaveName = config.Bind(
                "00 - Master", "03 SaveAsName", "MyLook",
                "Name to save the CURRENT settings under when you tick '04 SavePreset' below.");

            configPresetSaveNow = config.Bind(
                "00 - Master", "04 SavePreset", false,
                "Tick to write every current setting to a preset named by '03 SaveAsName'. Unticks itself "
                + "once the file is written.");

            configServerPreset = config.Bind(
                "00 - Master", "05 ServerPreset", "",
                "SERVER-SET. When the server names a preset here, every connecting client applies it, so a "
                + "whole server shares one look. Clients need that preset file present locally. Empty = "
                + "clients keep their own settings. Only meaningful when set on the server.");

            configAdminOnlyTuning = config.Bind(
                "01 - General", "03 AdminOnlyShaderTuning", false,
                "SERVER-SET. When on, only admins can move the shader tuning sliders; everyone else still "
                + "sees and toggles each shader on/off to A/B against vanilla. Sliders show as locked "
                + "rather than disappearing, so players can see what the server chose.");

            // ===================================================================
            // 01 - General
            // ===================================================================
            configVerboseLogging = config.Bind(
                "01 - General", "01 VerboseLogging", false,
                "Enable verbose log output for terrain/water diagnostics.");

            configServerAuthority = config.Bind(
                "01 - General", "02 ServerAuthority", true,
                "When true, the server forces clients to match its config (ConfigSync lock).");

            // ===================================================================
            // 04 - FiresWater Shader
            // ===================================================================
            configFiresWaterEnabled = config.Bind(
                "02 - FiresWater", "01 Enabled", true,
                "Master switch for the Custom/FiresWater shader. When true, FAT custom water meshes "
                + "render with the two-pass shader (visible from below with proper wave-synced surface). "
                + "When false, FAT water uses the captured vanilla material as-is (one-sided).");

            configFiresWaterSwapVanilla = config.Bind(
                "02 - FiresWater", "02 SwapVanillaOcean", true,
                "When true (and Enabled is also true), every vanilla WaterVolume's surface shader is swapped "
                + "to Custom/FiresWater. Affects ALL ocean rendering in the world. The swap is value-preserving "
                + "(all 33 vanilla water properties transfer to the new shader). Flip off for A/B comparison.");

            configFiresWaterUnderwaterEnv = config.Bind(
                "02 - FiresWater", "03 UnderwaterEnv", true,
                "When true, a custom 'FiresUnderwater' environment is registered with EnvMan and force-"
                + "activated whenever the local player's eye drops below a water surface. Drives fog, sun "
                + "color, ambient light, and wet-status together via the game's own env-blend system "
                + "(2-second crossfade). Independent of the shader Enabled toggle.");

            configFiresWaterUnderwaterBubbles = config.Bind(
                "02 - FiresWater", "04 UnderwaterBubbles", true,
                "Spawn rising blue bubble particles around the camera while submerged. Uses an "
                + "'UnderwaterBubble' texture from the bundle if present, else a built-in bubble sprite.");

            configFiresWaterUnderwaterFogDensity = config.Bind(
                "02 - FiresWater", "05 UnderwaterFogDensity", 3f,
                new ConfigDescription(
                    "Thickness of the underwater distance fog (the blue 'wall' that hides the world edge "
                    + "through the water). 1.0 = default; higher = nearer wall / murkier; lower = see farther.",
                    new AcceptableValueRange<float>(0.3f, 6f)));

            configFiresWaterUnderwaterDepthFog = config.Bind(
                "02 - FiresWater", "05b UnderwaterDepthFog", true,
                "Continuous eye-depth fog on nearby geometry underwater — ramps smoothly from the near-surface "
                + "tint to the deep blue-grey (shares the '09c Fog Underwater' colours), so the underwater look no "
                + "longer SNAPS on at the dive threshold. Off = fall back to the flat env fog. Density = '05' above.");

            configFiresWaterUnderwaterEnvReactive = config.Bind(
                "02 - FiresWater", "05c UnderwaterEnvReactive", true,
                "Make the underwater murk react to the live surface weather/time so surfacing never JARS: on a clear "
                + "bright day the murk tints toward the light surface fog AND thins (see farther); dawn/dusk/mist/rain/"
                + "storm tint it toward the env hue and thicken it. Off = the underwater murk stays the fixed '09c' colours.");

            configFiresWaterUnderwaterTransition = config.Bind(
                "02 - FiresWater", "05d UnderwaterTransitionTime", 1.8f,
                new ConfigDescription(
                    "Seconds to cross-fade the underwater LIGHTING (fog, murk, screen tint) when the camera crosses "
                    + "the surface — like vanilla's timed environment transitions, so dunking is never a hard cut no "
                    + "matter how fast you cross. 0 = instant (old snap). The waterline SPLIT stays instant/spatial.",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresWaterBubbleDensity = config.Bind(
                "02 - FiresWater", "06 BubbleDensity", 3f,
                new ConfigDescription(
                    "How many underwater bubbles spawn around you while submerged. 0 = none, 1 = default.",
                    new AcceptableValueRange<float>(0f, 6f)));

            configFiresWaterBubbleSpeed = config.Bind(
                "02 - FiresWater", "07 BubbleSpeed", 0.02f,
                new ConfigDescription(
                    "How fast underwater bubbles rise. 1 = default gentle drift; higher = faster; near 0 = "
                    + "almost suspended.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterMeniscusEnabled = config.Bind(
                "02 - FiresWater", "08 Meniscus", true,
                "Thin dark waterline band across the screen when the camera sits right at the water surface "
                + "(half-submerged) — the 'water on the lens' touch. Costs nothing away from the surface.");

            configFiresWaterMeniscusStrength = config.Bind(
                "02 - FiresWater", "09 MeniscusStrength", 0.35f,
                new ConfigDescription(
                    "Opacity of the waterline band. 0 = invisible.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterMeniscusWidth = config.Bind(
                "02 - FiresWater", "10 MeniscusWidth", 0.012f,
                new ConfigDescription(
                    "Thickness of the waterline band.",
                    new AcceptableValueRange<float>(0.002f, 0.08f)));

            configFiresWaterShipMaskFit = config.Bind(
                "02 - FiresWater", "11 ShipMaskFit", true,
                "When a boat drops BELOW the water surface (a whirlpool sink, or a big storm swell "
                + "heaving over the rail), stretch its water-mask up from the keel to the surface so "
                + "the ocean stops rendering over the deck and player. Restores to vanilla the instant "
                + "the boat floats normally, so ordinary sailing is untouched. Client-only.");

            configFiresWaterShipMaskMargin = config.Bind(
                "02 - FiresWater", "12 ShipMaskMargin", 1.5f,
                new ConfigDescription(
                    "Metres above the surface to raise the mask top so waterline chop doesn't re-expose "
                    + "the deck. 0 disables the submerged-boat mask fit entirely.",
                    new AcceptableValueRange<float>(0f, 6f)));

            configFiresWaterPlanarRefl = config.Bind(
                "02 - FiresWater", "13 PlanarReflection", true,
                "TRUE mirror reflections on the water — a second reflected camera renders the scene "
                + "(you, boats, shore) into the water surface, correct at any zoom. Costs one extra scene "
                + "render per frame, so it only runs near water and tears down when you're high above it. "
                + "Off = the cheap sky-cubemap reflection only.");

            configFiresWaterPlanarReflBankClip = config.Bind(
                "02 - FiresWater", "13b PlanarReflBankClip", 0.6f,
                new ConfigDescription(
                    "Culls terrain hugging the waterline (within this many metres ABOVE the surface) from the "
                    + "mirror — the near-bank sliver that smears into a STRIPE across a pond's otherwise clean "
                    + "reflection. Trees/hills higher than this still reflect normally. 0 = off (reflect everything).",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterPlanarReflResolution = config.Bind(
                "02 - FiresWater", "14 PlanarReflectionResolution", 0.5f,
                new ConfigDescription(
                    "Reflection render-texture scale vs screen. 1 = full-res crisp mirror (heaviest); 0.5 = "
                    + "half (default, soft but cheap); 0.25 = quarter (blurry, cheapest). The wave ripple "
                    + "hides most of the softness.",
                    new AcceptableValueRange<float>(0.25f, 1f)));

            configFiresWaterPlanarReflDistort = config.Bind(
                "02 - FiresWater", "15 PlanarReflectionRipple", 0.02f,
                new ConfigDescription(
                    "How much the wave surface ripples/distorts the mirror reflection. 0 = a dead-flat mirror; "
                    + "higher = wavier, more broken-up reflection.",
                    new AcceptableValueRange<float>(0f, 0.1f)));

            configFiresWaterPlanarReflMaxHeight = config.Bind(
                "02 - FiresWater", "16 PlanarReflectionMaxHeight", 80f,
                new ConfigDescription(
                    "Metres above the water surface past which the reflection stops rendering (flying, on a "
                    + "peak, in space). Lower = saves more performance but drops the reflection sooner as you "
                    + "climb; higher = keeps it from further up.",
                    new AcceptableValueRange<float>(10f, 400f)));

            configFiresWaterPlanarReflTerrain = config.Bind(
                "02 - FiresWater", "17 PlanarReflectionTerrain", true,
                "Include the terrain/shoreline in the mirror. On = the banks and hills reflect too (prettier, "
                + "heavier); off = only characters, boats and pieces reflect (cheaper, and enough for the "
                + "player-on-water look). Cave ceilings reflect as ROCK now that the black underside layer is "
                + "retired (floating disks only).");

            configFiresLightningFxBlend = config.Bind(
                "02 - FiresWater", "18 LightningFxBlend", true,
                "Make storm lightning composite with the custom sky/fog: the opaque flash card is swapped to an "
                + "additive shader (no hard rectangular cutout in the mist) and transient FX stop writing the "
                + "depth texture (no fog holes). Off = vanilla lightning materials, restored live; the fog-hole "
                + "and sky-cutout artifacts return where the volumetric fog is enabled.");

            // ===================================================================
            // 04 - FiresWater Surface (live shader tuning — pushed as shader
            // globals, so changes take effect in real time, NO bundle rebuild)
            // ===================================================================
            configFiresWaterFoamLayerA = config.Bind(
                "03 - FiresWater Surface", "01 FoamLayerAIntensity", 0.6f,
                new ConfigDescription(
                    "Brightness of foam texture layer A. The FiresWater surface composites TWO scrolling "
                    + "foam layers; if the water looks like two foam patterns fighting, lower one layer. "
                    + "Set A or B to 0 for a single clean foam texture.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterFoamLayerB = config.Bind(
                "03 - FiresWater Surface", "02 FoamLayerBIntensity", 0.4f,
                new ConfigDescription(
                    "Brightness of foam texture layer B (scrolls opposite layer A). Drop toward 0 for one "
                    + "clean foam look; raise for busier whitecap detail.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterFoamSlope = config.Bind(
                "03 - FiresWater Surface", "03 FoamSlopeAmount", 8f,
                new ConfigDescription(
                    "Foam driven by surface STEEPNESS — foams the steep faces of breaking waves (hides the "
                    + "foam-UV stretch there) and adds chop-foam to otherwise glassy near water. 0 = off.",
                    new AcceptableValueRange<float>(0f, 16f)));

            configFiresWaterFoamWaveHeight = config.Bind(
                "03 - FiresWater Surface", "04 FoamWaveHeightAmount", 3.414f,
                new ConfigDescription(
                    "Foam driven by wave HEIGHT (crest tops). Higher = foam appears on smaller swells too.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresWaterReflectionFloor = config.Bind(
                "03 - FiresWater Surface", "05 ReflectionFloor", 0.054f,
                new ConfigDescription(
                    "Minimum sky reflection even looking straight down. Raise above 0 so near water isn't "
                    + "dead-clear and the look stops swinging with camera angle. 0 = fully clear head-on.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterReflectionMax = config.Bind(
                "03 - FiresWater Surface", "06 ReflectionMax", 0.795f,
                new ConfigDescription(
                    "Maximum sky reflection at grazing (distant) angles. Lower = distant water reads less "
                    + "like a bright mirror. Pair with ReflectionFloor to compress the angle-driven swing.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterRefractShoreFade = config.Bind(
                "03 - FiresWater Surface", "06b RefractionShoreFade", 2.5f,
                new ConfigDescription(
                    "Fades the water's refraction offset to ZERO at the waterline so characters wading at the "
                    + "shore aren't smeared into the surface as a ghost 'double image'. The offset ramps back to "
                    + "full over the shore depth: LOWER = protects farther into deeper water (kills the ghost more "
                    + "aggressively); HIGHER = refraction returns sooner past the shore; very high = off.",
                    new AcceptableValueRange<float>(0.5f, 12f)));

            configFiresWaterRefractNearStart = config.Bind(
                "03 - FiresWater Surface", "06c RefractionNearStart", 8f,
                new ConfigDescription(
                    "Camera distance (m) inside which the refraction offset stays minimal, so the bottom of the "
                    + "water RIGHT IN FRONT OF YOU reads crisp instead of a fuzzy double image. The offset ramps "
                    + "back to full over NearRange metres past this.",
                    new AcceptableValueRange<float>(0f, 60f)));

            configFiresWaterRefractNearRange = config.Bind(
                "03 - FiresWater Surface", "06d RefractionNearRange", 30f,
                new ConfigDescription(
                    "Metres past NearStart over which the refraction offset ramps to full strength. Longer = the "
                    + "crisp-to-shimmer transition is more gradual.",
                    new AcceptableValueRange<float>(1f, 200f)));

            configFiresWaterFresnelSoftness = config.Bind(
                "03 - FiresWater Surface", "07 FresnelSoftness", 8f,
                new ConfigDescription(
                    "Fresnel falloff exponent. LOWER = reflection ramps in over a wider angle range, so the "
                    + "near->far transition is a soft gradient instead of a hard horizon line. 5 = sharp, 2 = very soft.",
                    new AcceptableValueRange<float>(1f, 16f)));

            configFiresWaterWaveOpacity = config.Bind(
                "03 - FiresWater Surface", "08 WaveOpacity", 3f,
                new ConfigDescription(
                    "How strongly wave HEIGHT drives surface opacity. Taller wave crests become more opaque "
                    + "(read as solid water) while calm/flat water keeps its see-through clarity. 0 = off "
                    + "(uniform see-through). Higher = crests turn opaque sooner. Top-surface only — the "
                    + "see-through underwater view is unaffected.",
                    new AcceptableValueRange<float>(0f, 6f)));

            configFiresWaterFoamScrollSpeed = config.Bind(
                "03 - FiresWater Surface", "09 FoamScrollSpeed", 2.5f,
                new ConfigDescription(
                    "How fast the foam texture scrolls across the surface. The two foam layers scroll "
                    + "opposite directions; on calm water a high speed reads as jarring rapid drift. Lower "
                    + "for a slow, natural creep. 4 = old default, 2 = calm, 0 = static.",
                    new AcceptableValueRange<float>(0f, 15f)));

            configFiresWaterSurfaceCausticsEnabled = config.Bind(
                "03 - FiresWater Surface", "09b SurfaceCausticsEnabled", false,
                "Master on/off for the V1 SURFACE caustics — the in-shader caustics you see looking DOWN "
                + "through the water surface from above. When off, they're fully suppressed regardless of the "
                + "CausticStrength slider below (the slider value is preserved). DEFAULT OFF because the V2 "
                + "fullscreen seafloor caustics (BelowWaterCaustics) now run ALWAYS and project onto the same "
                + "floor, so leaving V1 on would double them up. Turn this on to A/B the two systems or if you "
                + "prefer the V1 look. Does not affect V2.");

            configFiresWaterCausticStrength = config.Bind(
                "03 - FiresWater Surface", "10 CausticStrength", 2.562f,
                new ConfigDescription(
                    "Brightness of the V1 surface caustic light cast onto the seafloor seen THROUGH the water "
                    + "surface. 0 = off (or use SurfaceCausticsEnabled above for a clean toggle). V1 shows them "
                    + "looking down through the surface from above; caustics while actually swimming come in v2.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterCausticPassStrength = config.Bind(
                "03 - FiresWater Surface", "10b CausticPassStrength", 1f,
                new ConfigDescription(
                    "Amplitude of the SEABED caustics pass (BelowWaterCaustics) — the pass's OWN strength lever. "
                    + "This is the player-centred caustic light painted on the bottom; '10 CausticStrength' above "
                    + "only drives the separate surface term and does NOT affect this pass.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterCausticMoonScale = config.Bind(
                "03 - FiresWater Surface", "10c CausticMoonScale", 1f,
                new ConfigDescription(
                    "Extra scale on NIGHT caustics. Caustics are now driven by the live light (sun elevation x "
                    + "sun/moon brightness — dawn/dusk dim, storms dim), and at night only the moon's light shows "
                    + "them, already much dimmer; this scales that night contribution. 0 = none, 1 = physical.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterCausticScale = config.Bind(
                "03 - FiresWater Surface", "11 CausticScale", 0.007f,
                new ConfigDescription(
                    "Caustic feature size. LOWER = bigger bands (0.12 ~= 8m, 0.04 ~= 25m, 0.01 ~= 100m, "
                    + "0.004 ~= huge lazy bands); higher = small tight pool-caustics. Drop this for bigger bands.",
                    new AcceptableValueRange<float>(0.004f, 1f)));

            configFiresWaterCausticSpeed = config.Bind(
                "03 - FiresWater Surface", "12 CausticSpeed", 0.024f,
                new ConfigDescription(
                    "How fast the caustic web crawls along the wind direction. 0 = static, 0.05 = slow "
                    + "natural drift, higher = faster shimmer.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            configFiresWaterCausticWarp = config.Bind(
                "03 - FiresWater Surface", "13 CausticWarp", 0f,
                new ConfigDescription(
                    "Caustic blob irregularity. 0 = uniform cells, higher = bends them into varied organic "
                    + "shapes/sizes like drifting clouds (the realistic look).",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterCausticSharp = config.Bind(
                "03 - FiresWater Surface", "14 CausticSharpness", 0.3f,
                new ConfigDescription(
                    "Caustic contrast. HIGH = only the brightest cores show (thin sharp lines); ~1 = full "
                    + "linear band; BELOW 1 brightens the mid-tones so you see the WHOLE band, not just the "
                    + "brightest parts. Drop toward 0.3-1 to reveal more of each band.",
                    new AcceptableValueRange<float>(0.3f, 12f)));

            configFiresWaterNormalSpeed = config.Bind(
                "03 - FiresWater Surface", "15 RippleSpeed", 0.222f,
                new ConfigDescription(
                    "Speed of the surface normal-map shimmer (the fast same-direction 'reflective ripple / "
                    + "current' on the surface). 1.0 = old speed; lower to calm it if it's hard on the eyes; "
                    + "0 = static surface detail.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterFoamSunBoost = config.Bind(
                "03 - FiresWater Surface", "16 FoamSunBoost", 2.542f,
                new ConfigDescription(
                    "Lit foam: how much the foam brightens where sunlight strikes the surface (light "
                    + "reflecting off the foam). 0 = flat additive foam (old look); higher = foam catches "
                    + "the sun more.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterCausticShadow = config.Bind(
                "03 - FiresWater Surface", "17 CausticShadow", 0f,
                new ConfigDescription(
                    "Stage-2 (below-water) wave shadows: how dark the defocused cells between the bright "
                    + "caustic lines go on the seafloor. 0 = light only, no shadowing.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterCausticDepthFade = config.Bind(
                "03 - FiresWater Surface", "18 CausticDepthFade", 21.771f,
                new ConfigDescription(
                    "Stage-2 (below-water) caustics: metres below the surface over which the caustics fade "
                    + "out. Lower = caustics only in the shallows; higher = they reach deeper. Raise this if "
                    + "caustics only show when looking at shallow patches.",
                    new AcceptableValueRange<float>(2f, 80f)));

            configFiresWaterBelowWaterCaustics = config.Bind(
                "03 - FiresWater Surface", "19 BelowWaterCaustics", true,
                "Stage-2 seafloor caustics (the main caustic system). Projects caustic light + wave shadows "
                + "onto any ocean floor below the CURRENT TIDE level, drawn ALWAYS — from the shore looking "
                + "down through the shallows, from overhead, or while diving — independent of where the camera "
                + "is (no more 'only show at some angles'). Off = no seafloor caustics. Requires the "
                + "Custom/FiresCaustics shader in the bundle.");

            configFiresWaterCausticDebug = config.Bind(
                "03 - FiresWater Surface", "20 CausticDebug", false,
                "Diagnostic: render the raw Stage-2 caustic field instead of modulating the scene, on any "
                + "below-tide seafloor pixel. Use to verify the depth->world reconstruction is correct. Leave "
                + "off in normal play.");

            // Stage-2 underwater caustics get their OWN scale/sharpness/strength
            // (independent of the 11/14/10 surface caustic sliders) so the
            // seafloor projection and the look-down surface caustics tune apart.
            configFiresWaterUWCausticScale = config.Bind(
                "03 - FiresWater Surface", "21 UnderwaterCausticScale", 0.01f,
                new ConfigDescription(
                    "Seafloor (below-water) caustic feature size. LOWER = bigger bands. Independent of the "
                    + "surface CausticScale (11).",
                    new AcceptableValueRange<float>(0.004f, 1f)));

            configFiresWaterUWCausticSharpness = config.Bind(
                "03 - FiresWater Surface", "22 UnderwaterCausticSharpness", 0.706f,
                new ConfigDescription(
                    "Seafloor caustic contrast — controls how much of the floor reads as lit. LOWER = broader, "
                    + "more coverage; HIGHER = thin bright lines. Independent of the surface CausticSharpness (14).",
                    new AcceptableValueRange<float>(0.3f, 12f)));

            configFiresWaterUWCausticStrength = config.Bind(
                "03 - FiresWater Surface", "23 UnderwaterCausticStrength", 0.194f,
                new ConfigDescription(
                    "Seafloor caustic brightness (added as light, so it shows on dark deep floor). Independent "
                    + "of the surface CausticStrength (10). NOTE the dark cells/shadow between the bright web "
                    + "are driven by CausticShadow (17) — raise that to make the big swooping bands read on the "
                    + "brighter sun-facing side too.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresWaterUWCausticSpeed = config.Bind(
                "03 - FiresWater Surface", "24 UnderwaterCausticSpeed", 0.019f,
                new ConfigDescription(
                    "How fast the seafloor caustic web crawls along the wind direction. Independent of the "
                    + "surface CausticSpeed (12). 0 = static, 0.05 = slow natural drift, higher = faster.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            configFiresWaterUWCausticWarp = config.Bind(
                "03 - FiresWater Surface", "25 UnderwaterCausticWarp", 0.6f,
                new ConfigDescription(
                    "Seafloor caustic domain-warp — bends the cells into the big organic SWOOPING shapes "
                    + "(vs a uniform grid). Independent of the surface CausticWarp (13). 0 = regular cells, "
                    + "higher = more drifting-cloud swoop.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterUnderwaterDapple = config.Bind(
                "03 - FiresWater Surface", "26 UnderwaterDappleCaustics", false,
                "The THIRD caustic: a faint normal-based dapple drawn in the FiresWater underwater (back-face) "
                + "pass — separate from the V1 surface caustics (09b) and the V2 seafloor pass "
                + "(BelowWaterCaustics). It's subtle and unclamped. Toggle off to A/B it against the other two "
                + "and decide which caustic systems to keep.");

            configFiresWaterSurfaceFog = config.Bind(
                "03 - FiresWater Surface", "27 SurfaceSeaFog", 0.45f,
                new ConfigDescription(
                    "How much the water SURFACE fogs into the distance. The water shader used to ignore fog "
                    + "entirely (crisp waves to the horizon, but a hard edge where fogged land meets clear "
                    + "water). 1 = full fog so distant waves dissolve into the sea haze and the shoreline "
                    + "matches the land; 0 = the old no-fog look; in-between keeps some wave detail in the haze.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterCausticRadius = config.Bind(
                "03 - FiresWater Surface", "28 CausticRadius", 135.694f,
                new ConfigDescription(
                    "How far out (metres from the player) the seafloor caustics (V2/BelowWaterCaustics) draw, "
                    + "with a soft ~30m fade at the edge so distant floor shows none and there's no hard ring. "
                    + "Keeping it to the directly-loaded zones (not the far extended ocean) is also the main "
                    + "performance saver — the pass also fully switches off when you're far above the water "
                    + "(flying or in space). Lower = tighter pool of light around you.",
                    new AcceptableValueRange<float>(30f, 400f)));

            configFiresWaterSSSColor = config.Bind(
                "03 - FiresWater Surface", "29 SSSColor", new UnityEngine.Color(0.09f, 0.50f, 0.46f, 1f),
                "Subsurface-scattering tint — the colour wave bodies glow from within when light passes "
                + "through them (Crest Water formula). Teal-green reads like deep clean ocean.");

            configFiresWaterSSSBase = config.Bind(
                "03 - FiresWater Surface", "30 SSSBase", 0.16f,
                new ConfigDescription(
                    "View-independent subsurface glow on all water, regardless of sun angle. 0 = off.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterSSSSun = config.Bind(
                "03 - FiresWater Surface", "31 SSSSun", 1.7f,
                new ConfigDescription(
                    "Extra subsurface glow when looking TOWARD the sun through the waves — the backlit "
                    + "'light through the crest' effect, strongest at low sun. 0 = off.",
                    new AcceptableValueRange<float>(0f, 6f)));

            configFiresWaterSSSSunFalloff = config.Bind(
                "03 - FiresWater Surface", "32 SSSSunFalloff", 5f,
                new ConfigDescription(
                    "Tightness of the toward-sun glow lobe. Higher = narrower halo around the sun direction.",
                    new AcceptableValueRange<float>(1f, 16f)));

            configFiresWaterSSSWaveBoost = config.Bind(
                "03 - FiresWater Surface", "33 SSSWaveBoost", 1.5f,
                new ConfigDescription(
                    "How much tall wave crests boost the scatter (crests are thinner, so more light gets "
                    + "through — Crest models this from displacement pinch; we drive it from wave height).",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresWaterAbsorbBlend = config.Bind(
                "03 - FiresWater Surface", "34 AbsorbBlend", 0.85f,
                new ConfigDescription(
                    "Physically-based per-channel depth absorption (Crest Water formula): deeper water eats "
                    + "red light first, then green, so the seafloor shifts HUE toward the water colour as it "
                    + "drops away instead of just fading. 0 = the old linear depth blend exactly; 1 = full "
                    + "exponential absorption; in-between mixes the two looks.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterAbsorbExtinction = config.Bind(
                "03 - FiresWater Surface", "35 AbsorbExtinction", new UnityEngine.Color(0.45f, 0.16f, 0.10f, 1f),
                "Per-channel extinction for the depth absorption (34) — how fast each colour channel dies per "
                + "metre of water. The default kills red fastest (real ocean behaviour, which is why depths go "
                + "teal). Raise a channel to make that colour vanish sooner; lower all three for clearer, more "
                + "tropical water.");

            configFiresWaterFoamBubbleIntensity = config.Bind(
                "03 - FiresWater Surface", "36 FoamBubbleIntensity", 0.6f,
                new ConfigDescription(
                    "Bubble-cloud underlayer beneath the crisp white foam (Crest Water bubble foam): a second, "
                    + "dimmer foam pattern tinted by the water colour, so crests read as churned aerated water "
                    + "under the whitecaps instead of paint on glass. It only appears where foam already lives "
                    + "(same wave/shore/slope drivers). 0 = off; higher = thicker bubble clouds.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresWaterDistNormalFade = config.Bind(
                "03 - FiresWater Surface", "37 DistNormalFade", 900f,
                new ConfigDescription(
                    "Metres over which distant water normals flatten out (Crest fades normal strength with "
                    + "distance). Up close the ripple detail sparkles; far away that same detail aliases into "
                    + "white speckle noise and strobing sun glints. Lower = calm the surface sooner; higher = "
                    + "keep detail further out; 0 = off (also disables DistSpecRough 38).",
                    new AcceptableValueRange<float>(0f, 4000f)));

            configFiresWaterDistSpecRough = config.Bind(
                "03 - FiresWater Surface", "38 DistSpecRough", 0.6f,
                new ConfigDescription(
                    "How much the specular highlight WIDENS with distance (paired with DistNormalFade 37): far "
                    + "water swaps pinpoint strobing glints for one broad soft sun lane. 0 = highlight stays "
                    + "sharp everywhere; 1 = fully broad at the DistNormalFade range.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterSchlickFresnel = config.Bind(
                "03 - FiresWater Surface", "39 SchlickFresnel", 0.65f,
                new ConfigDescription(
                    "Blend toward the physical Schlick fresnel curve (Crest Water, IOR-derived): water goes "
                    + "nearly clear looking straight down and fully mirrored at grazing angles — more realistic, "
                    + "but the camera angle swings the look more than the default banded curve (the band exists "
                    + "to stop exactly that). 0 = banded curve only (old look); 1 = full Schlick. Opt-in.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresWaterPixelDepth = config.Bind(
                "03 - FiresWater Surface", "41 PixelDepthColor", true,
                "Measure the water's depth PER PIXEL from the camera depth texture instead of per water-mesh "
                + "vertex. Fixes the hard polygon-shaped clear patches where a shallow terrain spike (stairs, "
                + "mounds) under one vertex smeared across whole triangles. Off = the old per-vertex depth.");

            configFiresWaterWaveBandLimit = config.Bind(
                "03 - FiresWater Surface", "43 WaveBandLimit", true,
                "Crest-style wave band-limiting: each of the 10 wave components fades out where the local mesh "
                + "grid is too coarse to carry it (the far clipmap rings), and zone water fades the same "
                + "components on approach to its edge so the spectrum matches across the seam. Fixes waves "
                + "visibly dying/'resetting' where the fine water ends and crawling on the far rings. "
                + "Off = full spectrum everywhere (the old look).");

            configFiresWaterSunSpec = config.Bind(
                "03 - FiresWater Surface", "42 SunSpecular", 1f,
                new ConfigDescription(
                    "Global scale on the sun's specular highlight on ALL water (zone rivers/ocean AND ponds — "
                    + "the lever zone water never had; the FiresLiquid 'Specular Intensity' only feeds pond "
                    + "materials). The specular is now fresnel-gated (2% looking down, full at grazing) so the "
                    + "midday white blanket is gone by physics; this scales what remains. 0 = no sun highlight.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresWaterNadirReflCap = config.Bind(
                "03 - FiresWater Surface", "40 NadirReflectionCap", 1f,
                new ConfigDescription(
                    "Brightness cap on the reflection when looking DOWN at the water (the opaque-white sun/sky "
                    + "blob at your feet — an HDR-bright sky overpowers even a few % reflection). Lower = dimmer "
                    + "head-on reflection; the cap releases toward grazing so sun glitter and the horizon mirror "
                    + "keep full punch. Applies to ocean AND river/pond water. 0 = off (old look).",
                    new AcceptableValueRange<float>(0f, 4f)));

            // ===================================================================
            // 21 - FiresLava (swap vanilla Ashlands lava -> Custom/FiresFlowOpaque,
            // live tuning — no bundle rebuild, applies to the swapped materials)
            // ===================================================================
            configFiresLavaEnabled = config.Bind(
                "21 - FiresLava", "01 Enabled", true,
                "Swap vanilla Ashlands lava (Custom/FlowOpaque) to Custom/FiresFlowOpaque with the "
                + "FireLavaCrust / FireLavaFlow textures. Flip OFF for an instant A/B against vanilla lava.");

            configFiresLavaEmissionBoost = config.Bind(
                "21 - FiresLava", "02 EmissionBoost", 1.375f,
                new ConfigDescription(
                    "Brightness of the lava's glow. 1 = vanilla emission; higher = hotter/brighter molten; 0 = no glow.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresLavaGlowFromTexture = config.Bind(
                "21 - FiresLava", "03 GlowFromOurTexture", true,
                "When ON, the GLOW comes from your FireLavaFlow texture instead of vanilla's emission map, so the "
                + "lava reads as distinctly yours. Pair with EmissionBoost to set how bright.");

            configFiresLavaFlowSpeed = config.Bind(
                "21 - FiresLava", "04 FlowSpeed", 0.094f,
                new ConfigDescription(
                    "Scales how fast the molten surface scrolls. 0 = static crust, 1 = vanilla drift, higher = faster.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresLavaMoltenColor = config.Bind(
                "21 - FiresLava", "05 MoltenTint", new UnityEngine.Color(1f, 0f, 0f, 0f),
                "Tint multiplied into the molten/flow color. White = your texture's own colors; warm it for hotter lava.");

            configFiresLavaMetallic = config.Bind(
                "21 - FiresLava", "06 Metallic", 0.24f,
                new ConfigDescription(
                    "Lava metalness — usually low. Higher = more reflective metallic sheen on the crust.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresLavaSmoothness = config.Bind(
                "21 - FiresLava", "07 Smoothness", 0.818f,
                new ConfigDescription(
                    "Lava surface smoothness — low = matte crust, high = glassy/wet sheen that catches highlights.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ===================================================================
            // 17 - FiresHeightmap (swap vanilla terrain -> Custom/FiresHeightmap;
            // tune the Ashlands lava biome paint live — no bundle rebuild)
            // ===================================================================
            configFiresHeightmapEnabled = config.Bind(
                "17 - FiresHeightmap", "01 Enabled", false,
                "Swap vanilla terrain (Custom/Heightmap) to Custom/FiresHeightmap, which renders the Ashlands "
                + "lava biome paint as glowing molten ground. Flip OFF for an instant A/B against vanilla terrain. "
                + "Mountain and Deep North zones are always left on vanilla terrain: Valheim 1.0 added snow to its "
                + "terrain shader (snow glint, and Deep North snow depth that raises the ground you walk on) that "
                + "our reconstruction does not carry yet. Console: va_terrain on|off|status.");

            configFiresHeightmapLavaBrightness = config.Bind(
                "17 - FiresHeightmap", "02 LavaBrightness", 6f,
                new ConfigDescription(
                    "How brightly the molten ground glows (HDR). 6 = the vanilla-baked intensity; lower = dimmer; 0 = no glow.",
                    new AcceptableValueRange<float>(0f, 12f)));

            configFiresHeightmapLavaTint = config.Bind(
                "17 - FiresHeightmap", "03 LavaTint", new UnityEngine.Color(1f, 0.1333f, 0f, 1f),
                "Hue of the lava glow (multiplied by LavaBrightness). Default reproduces vanilla's orange (6,0.8,0).");

            configFiresHeightmapFlowSpeed = config.Bind(
                "17 - FiresHeightmap", "04 FlowSpeed", 0.01f,
                new ConfigDescription(
                    "How fast the molten surface scrolls. 0.01 = vanilla; 0 = frozen crust; higher = faster flow.",
                    new AcceptableValueRange<float>(0f, 0.1f)));

            configFiresHeightmapArrayLayer = config.Bind(
                "17 - FiresHeightmap", "05 LavaArrayLayer", 15,
                new ConfigDescription(
                    "Which terrain texture-array layer supplies the molten surface. Vanilla = 15. Only change if the "
                    + "lava reads as the wrong texture.",
                    new AcceptableValueRange<int>(0, 20)));

            configFiresHeightmapLavaCoverage = config.Bind(
                "17 - FiresHeightmap", "06 LavaCoverage", 1f,
                new ConfigDescription(
                    "Scales the lava amount. Lava is placed by the REAL paint-mask channel (the same data the game "
                    + "uses for lava), so 1 = exactly the real lava; lower = fade it back; 0 = none. Does NOT move the channels.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresHeightmapLavaThreshold = config.Bind(
                "17 - FiresHeightmap", "07 LavaThreshold", 0.2f,
                new ConfigDescription(
                    "Paint-alpha cutoff for the lava channel (vanilla shader uses 0.2; IsLava/damage uses 0.6). RAISE to "
                    + "pull the molten edge in toward the hottest core; LOWER to spread it. Same mask as collision/damage.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ===================================================================
            // 18 - FiresMist (swap vanilla heavymist -> Custom/FiresLitParticles;
            // tune the Mistlands mist look live — no bundle rebuild)
            // ===================================================================
            configFiresMistEnabled = config.Bind(
                "18 - FiresMist", "01 Enabled", false,
                "Swap the Mistlands/Ashlands heavy mist (Custom/LitParticles) to Custom/FiresLitParticles so the "
                + "knobs below take effect. Flip OFF for an instant A/B against vanilla mist. Console: va_mist on|off|status.");

            configFiresMistTint = config.Bind(
                "18 - FiresMist", "02 Tint", UnityEngine.Color.white,
                "Color tint of the mist (multiplies the mist color). White = vanilla; tint it blue/green/etc to recolor the fog.");

            configFiresMistBrightness = config.Bind(
                "18 - FiresMist", "03 Brightness", 1f,
                new ConfigDescription(
                    "How bright the mist renders. 1 = vanilla; lower = darker/moodier; higher = brighter/glowier.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresMistDensity = config.Bind(
                "18 - FiresMist", "04 Density", 1f,
                new ConfigDescription(
                    "Mist thickness/opacity. 1 = vanilla; lower = thinner/clearer; higher = thicker (toward fully opaque).",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresMistFadeScale = config.Bind(
                "18 - FiresMist", "05 FadeReach", 1f,
                new ConfigDescription(
                    "How close the mist comes to the camera before it fades out. 1 = vanilla; <1 lets it crowd in closer; "
                    + ">1 pushes it further away (clearer near the player).",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            configFiresMistEdgePower = config.Bind(
                "18 - FiresMist", "06 EdgeSoftness", 1f,
                new ConfigDescription(
                    "Particle edge sharpness (power on the cloud alpha). 1 = vanilla; <1 = softer/fuller edges; "
                    + ">1 = sharper, wispier edges.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            configFiresMistLightResponse = config.Bind(
                "18 - FiresMist", "07 LightResponse", 1f,
                new ConfigDescription(
                    "How much the mist shades against the sun direction. 1 = vanilla (mostly flat); 0 = fully flat; "
                    + "higher = more 3D, directional shading.",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresMistAltitude = config.Bind(
                "18 - FiresMist", "08 AltitudeFloor", 30f,
                new ConfigDescription(
                    "World height below which the mist fades out. 30 = vanilla (the water line). Raise to lift the mist "
                    + "up off the ground; lower to let it pool deeper.",
                    new AcceptableValueRange<float>(0f, 200f)));

            configFiresMistPatchiness = config.Bind(
                "18 - FiresMist", "09 Patchiness", 0.55f,
                new ConfigDescription(
                    "Procedural density variation - patchy/clumpy mist like the volumetric fog. 0 = uniform (vanilla); "
                    + "1 = strongly patchy (clear gaps and thick clumps).",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresMistPatchScale = config.Bind(
                "18 - FiresMist", "10 PatchScale", 0.05f,
                new ConfigDescription(
                    "Size of the patches (noise frequency). Lower = bigger, broader patches; higher = finer, tighter ones.",
                    new AcceptableValueRange<float>(0.005f, 0.5f)));

            // Tier-2 fog-ify (defaults make the mist read like FiresVolFog; set the relevant one to 0 for vanilla-exact).
            configFiresMistWindDrift = config.Bind(
                "18 - FiresMist", "11 WindDrift", 0.02f,
                new ConfigDescription("Coverage drifts with the wind. 0 = static; higher = faster drift.",
                    new AcceptableValueRange<float>(0f, 0.2f)));
            configFiresMistPushStrength = config.Bind(
                "18 - FiresMist", "12 PlayerPush", 0.6f,
                new ConfigDescription("Mist parts / flows around the player as you move. 0 = off; 1 = strong.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configFiresMistPushRadius = config.Bind(
                "18 - FiresMist", "13 PushRadius", 18f,
                new ConfigDescription("Radius (metres) of the near-player parting.",
                    new AcceptableValueRange<float>(2f, 60f)));
            configFiresMistCoverage = config.Bind(
                "18 - FiresMist", "14 Coverage", 1f,
                new ConfigDescription("Coverage character: 0 = simple patch noise, 1 = richer banks+wisps (FiresVolFog-style).",
                    new AcceptableValueRange<float>(0f, 1f)));
            configFiresMistCoverScale = config.Bind(
                "18 - FiresMist", "15 CoverageScale", 0.015f,
                new ConfigDescription("Coverage frequency. Lower = bigger banks; higher = finer wisps.",
                    new AcceptableValueRange<float>(0.002f, 0.1f)));
            configFiresMistDenseTint = config.Bind(
                "18 - FiresMist", "16 DenseTint", new UnityEngine.Color(0.82f, 0.88f, 1f, 1f),
                "Hue the DENSE cores shift toward (two-tone). White = no shift.");
            configFiresMistTwoTone = config.Bind(
                "18 - FiresMist", "17 TwoTone", 0.45f,
                new ConfigDescription("Two-tone strength: how far dense cores tint toward DenseTint. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configFiresMistWeatherColor = config.Bind(
                "18 - FiresMist", "18 WeatherTint", 0.6f,
                new ConfigDescription("How far the mist tint shifts toward the env fog hue with weather. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configFiresMistWeatherDensity = config.Bind(
                "18 - FiresMist", "19 WeatherDensity", 0.5f,
                new ConfigDescription("How much weather (fog/rain/storms) thickens the mist. 0 = off.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // ===================================================================
            // 19 - FiresPiece (swap Custom/Piece -> Custom/FiresPiece; vanilla-exact at defaults)
            // ===================================================================
            configFiresPieceEnabled = config.Bind(
                "19 - FiresPiece", "01 Enabled", true,
                "Render building pieces with the Custom/FiresPiece reconstruction (so the wet/reflection knobs below "
                + "take effect). ON by default: at default knob values it is vanilla-exact, so it's a no-op look until "
                + "you tune. Flip OFF (or 'va_piece off') for an instant A/B against the stock Custom/Piece shader. "
                + "Console: va_piece on|off|status.");

            configFiresPieceWetStrength = config.Bind(
                "19 - FiresPiece", "02 WetStrength", 1f,
                new ConfigDescription(
                    "How wet pieces get in the rain. 1 = vanilla; lower = drier (less water sheen); 0 = never wet.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresPieceWetSmoothness = config.Bind(
                "19 - FiresPiece", "03 WetSmoothness", 0.70f,
                new ConfigDescription(
                    "How reflective/glossy pieces become when fully wet. 0.70 = vanilla; lower = less mirror-like in rain.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresPieceSmoothnessScale = config.Bind(
                "19 - FiresPiece", "04 ReflectionScale", 1f,
                new ConfigDescription(
                    "Scales base (dry) smoothness -> overall reflection intensity on pieces. 1 = vanilla; lower = less shiny.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresPieceMetallicScale = config.Bind(
                "19 - FiresPiece", "05 MetallicScale", 1f,
                new ConfigDescription(
                    "Scales metalness -> how strongly metal pieces mirror the reflection probe/sky. 1 = vanilla; lower = duller metal.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // ===================================================================
            // 20 - FiresGlass (stained-glass light projection + godrays)
            // ===================================================================
            configFiresGlassEnabled = config.Bind(
                "20 - FiresGlass", "01 Enabled", true,
                "Master switch for the stained-glass light projection (the coloured light a window casts onto the "
                + "ground + the godray shafts). A window still has to be placed/planted to project. "
                + "Console: va_glass on|off|status|spawn|here|debug.");

            configFiresGlassIntensity = config.Bind(
                "20 - FiresGlass", "02 Intensity", 2.5f,
                new ConfigDescription(
                    "Brightness/opacity of the coloured light thrown on the floor. Higher = stronger, more saturated cast.",
                    new AcceptableValueRange<float>(0f, 12f)));

            configFiresGlassReach = config.Bind(
                "20 - FiresGlass", "03 Reach", 40f,
                new ConfigDescription(
                    "How far (metres) the cast image + shafts throw from the window before fading out.",
                    new AcceptableValueRange<float>(2f, 80f)));

            configFiresGlassSoftness = config.Bind(
                "20 - FiresGlass", "04 EdgeSoftness", 0.12f,
                new ConfigDescription(
                    "Soft feather on the edge of the projected image (0 = hard rectangle edge).",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            configFiresGlassUseVanillaShadow = config.Bind(
                "20 - FiresGlass", "04b UseVanillaShadow", true,
                "Cull the coloured cast with the GAME'S OWN sun shadow (screen-space) instead of a separate "
                + "shadow render. On = the coloured light cuts out exactly where the shadow you already see falls "
                + "(the player's own shadow etc), pixel-aligned, and skips an extra render. Off = the older custom "
                + "sun-shadow-map + per-window occlusion test (fallback if your setup doesn't expose the vanilla "
                + "shadow to the pass).");

            configFiresGlassShadowStrength = config.Bind(
                "20 - FiresGlass", "05 ShadowStrength", 1f,
                new ConfigDescription(
                    "How much the world's REAL sun shadow (e.g. the player standing in the beam) suppresses the coloured "
                    + "light — so the real shadow shows through the cast. 0 = ignore the shadow (flood), 1 = fully respect it. "
                    + "(Only used when UseVanillaShadow is OFF.)",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresGlassShadowBias = config.Bind(
                "20 - FiresGlass", "06 ShadowLow", 0.20f,
                new ConfigDescription(
                    "Scene brightness at/below which a spot counts as FULLY shadowed (no coloured light). Raise if lit "
                    + "ground is wrongly dimmed; lower if shadows still show colour.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresGlassShadowThickness = config.Bind(
                "20 - FiresGlass", "07 ShadowHigh", 0.55f,
                new ConfigDescription(
                    "Scene brightness at/above which a spot counts as FULLY lit (full coloured light). Between Low and High "
                    + "the light ramps in, so the shadow edge is soft.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresGlassGodrayDensity = config.Bind(
                "20 - FiresGlass", "08 GodrayDensity", 1.2f,
                new ConfigDescription(
                    "Strength of the volumetric light shafts (god rays) visible in the air between the window and the "
                    + "ground. 0 = off (ground projection only).",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresGlassNeonEmission = config.Bind(
                "20 - FiresGlass", "09 NeonEmission", 1f,
                new ConfigDescription(
                    "Live brightness multiplier for the NEON SIGN pieces (Custom/FiresGlassNeon — e.g. the Dvergr Den "
                    + "sign). 1 = authored brightness; raise until it reads as a lit sign, lower for a dying tube. "
                    + "Applies on the next frame, no rebuild.",
                    new AcceptableValueRange<float>(0.1f, 8f)));

            configFiresGlassNeonCastIntensity = config.Bind(
                "20 - FiresGlass", "09b NeonCastIntensity", 1f,
                new ConfigDescription(
                    "Strength of the glow POOL a neon sign throws on the ground in front of it (its own light, not "
                    + "the sun — works at night and indoors). 0 = sign glows but casts nothing.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresGlassNeonCastAngle = config.Bind(
                "20 - FiresGlass", "09c NeonCastAngle", 40f,
                new ConfigDescription(
                    "Downward tilt (degrees) of the neon sign's cast beam. Low = throws far out in front (long pool), "
                    + "high = pools tight under the sign.",
                    new AcceptableValueRange<float>(10f, 80f)));

            configFiresGlassNeonBeamFront = config.Bind(
                "20 - FiresGlass", "09d NeonBeamFront", true,
                "Which face the neon sign's glow projects from: ON = out the visible front face (correct for a "
                + "sign you read from the front); OFF = out the back. Flip this if the glow lands behind the sign.");

            // ===================================================================
            // 10 - FiresPlayer (swap Custom/Player -> Custom/FiresPlayer + eye color)
            // ===================================================================
            configFiresPlayerEnabled = config.Bind(
                "10 - FiresPlayer", "01 Enabled", true,
                "Swap character bodies (Custom/Player) to Custom/FiresPlayer, which adds eye-color control. "
                + "At default eye color (white) it looks identical to vanilla. Flip OFF to revert.");

            configFiresPlayerEyeColor = config.Bind(
                "10 - FiresPlayer", "02 EyeColor", new UnityEngine.Color(1f, 1f, 1f, 0f),
                "Recolors YOUR character's iris only (companions/NPCs are unaffected). The ALPHA channel is "
                + "the strength: alpha 0 = no eye change (default), alpha 1 = full. Pick an RGB then raise alpha.");

            configFiresPlayerEyeThreshold = config.Bind(
                "10 - FiresPlayer", "03 EyeBlueSensitivity", 0.05f,
                new ConfigDescription(
                    "How strongly blue a skin texel must be to count as 'eye' (the iris is the only blue part of "
                    + "the skin, so this won't touch fleshtone). LOWER = catches more of the iris; RAISE if "
                    + "non-eye pixels tint. ~0.05 is typical.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            // ===================================================================
            // 10b - Elf Ears (procedural pointed ears on the local player)
            // ===================================================================
            configElfEarsEnabled = config.Bind(
                "10b - Elf Ears", "01 Enabled", false,
                "Add procedural pointed 'elf' ears to your character. OFF by default. Toggle live or with "
                + "`va_ears on|off`. Then place them with the sliders below (they update instantly).");

            configElfEarsScaleX = config.Bind(
                "10b - Elf Ears", "02 ScaleX (width)", 0.41f,
                new ConfigDescription("Ear WIDTH — size across the head.", new AcceptableValueRange<float>(0.01f, 2.0f)));

            configElfEarsScaleY = config.Bind(
                "10b - Elf Ears", "02a ScaleY (height)", 0.41f,
                new ConfigDescription("Ear HEIGHT — size up the head.", new AcceptableValueRange<float>(0.01f, 2.0f)));

            configElfEarsScaleZ = config.Bind(
                "10b - Elf Ears", "02b ScaleZ (depth)", 0.15f,
                new ConfigDescription("Ear DEPTH out from the head — the real 3D thickness. Raise it so the ear stays solid when you "
                    + "turn your head side-on (instead of vanishing like paper) and so it covers the vanilla ear underneath.",
                    new AcceptableValueRange<float>(0.01f, 1.0f)));

            configElfEarsOffsetX = config.Bind(
                "10b - Elf Ears", "03 OffsetSide", 0.037f,
                new ConfigDescription("Sideways distance from head centre to each ear (m). Negative flips the ears to the opposite sides.", new AcceptableValueRange<float>(-0.6f, 0.6f)));

            configElfEarsOffsetY = config.Bind(
                "10b - Elf Ears", "04 OffsetUp", -0.2f,
                new ConfigDescription("Up/down placement on the head (m).", new AcceptableValueRange<float>(-0.4f, 0.4f)));

            configElfEarsOffsetZ = config.Bind(
                "10b - Elf Ears", "05 OffsetForward", -0.04f,
                new ConfigDescription("Forward/back placement on the head (m).", new AcceptableValueRange<float>(-0.4f, 0.4f)));

            configElfEarsRoll = config.Bind(
                "10b - Elf Ears", "06 Splay", -34.375f,
                new ConfigDescription("Splay OUTWARD from the head (deg).", new AcceptableValueRange<float>(-180f, 180f)));

            configElfEarsYaw = config.Bind(
                "10b - Elf Ears", "07 SweepBack", 46.875f,
                new ConfigDescription("Sweep toward the BACK of the head (deg).", new AcceptableValueRange<float>(-180f, 180f)));

            configElfEarsPitch = config.Bind(
                "10b - Elf Ears", "08 Tilt", 9.375f,
                new ConfigDescription("Tilt forward/back (deg).", new AcceptableValueRange<float>(-180f, 180f)));

            configElfEarsColor = config.Bind(
                "10b - Elf Ears", "09 Color", UnityEngine.Color.white,
                "Tint multiplied into the player's skin tone. WHITE = matches your skin exactly; warm/cool it from there.");

            configElfEarsSet = config.Bind(
                "10b - Elf Ears", "00 Ear Set", "FiresEar_Pointed2",
                FiresCore.Config.FiresConfigChoices.InlineDescription(
                    "Which ear shape to wear (sculpted sets from the elf-ear pack). Switch live with "
                    + "`va_ears set <name>` or `va_ears next`. Then place/scale it with the sliders below.",
                    new[]
                    {
                        "FiresEar_Pointed1", "FiresEar_Pointed2", "FiresEar_Pointed3",
                        "FiresEar_Horned", "FiresEar_Blush", "FiresEar_Earringed",
                    }));

            // ===================================================================
            // 10d - Hair & Beard Scale (per-axis size of the vanilla hair & beard)
            // ===================================================================
            configHairScaleEnabled = config.Bind(
                "10d - Hair & Beard Scale", "01 HairEnabled", false,
                "Resize your character's HAIR mesh with the per-axis sliders below. OFF leaves it vanilla.");
            configHairScaleX = config.Bind(
                "10d - Hair & Beard Scale", "02 HairScaleX", 1.0f,
                new ConfigDescription("Hair size on the head-bone X axis (1 = unchanged). Tune live to see which way each axis runs.",
                    new AcceptableValueRange<float>(0.1f, 3.0f)));
            configHairScaleY = config.Bind(
                "10d - Hair & Beard Scale", "03 HairScaleY", 1.0f,
                new ConfigDescription("Hair size on the head-bone Y axis (1 = unchanged).", new AcceptableValueRange<float>(0.1f, 3.0f)));
            configHairScaleZ = config.Bind(
                "10d - Hair & Beard Scale", "04 HairScaleZ", 1.0f,
                new ConfigDescription("Hair size on the head-bone Z axis (1 = unchanged).", new AcceptableValueRange<float>(0.1f, 3.0f)));

            configBeardScaleEnabled = config.Bind(
                "10d - Hair & Beard Scale", "05 BeardEnabled", false,
                "Resize your character's BEARD mesh with the per-axis sliders below. OFF leaves it vanilla.");
            configBeardScaleX = config.Bind(
                "10d - Hair & Beard Scale", "06 BeardScaleX", 1.0f,
                new ConfigDescription("Beard size on the head-bone X axis (1 = unchanged).", new AcceptableValueRange<float>(0.1f, 3.0f)));
            configBeardScaleY = config.Bind(
                "10d - Hair & Beard Scale", "07 BeardScaleY", 1.0f,
                new ConfigDescription("Beard size on the head-bone Y axis (1 = unchanged).", new AcceptableValueRange<float>(0.1f, 3.0f)));
            configBeardScaleZ = config.Bind(
                "10d - Hair & Beard Scale", "08 BeardScaleZ", 1.0f,
                new ConfigDescription("Beard size on the head-bone Z axis (1 = unchanged).", new AcceptableValueRange<float>(0.1f, 3.0f)));

            // ===================================================================
            // 10e - Player Model — REMOVED FOR NOW (prototype body-mesh swap didn't pan out). The configs below are
            // commented out so the model can't be changed in-game until the swap is corrected. FiresPlayerModel.cs is
            // kept intact; uncomment this block + the field declarations + the wiring in FiresAdminTerrain/FiresElfEars
            // + re-embed the firesplayermodels bundle (csproj) to bring it back.
            // ===================================================================
            /*
            configPlayerModelEnabled = config.Bind(
                "10e - Player Model", "01 Enabled", false,
                "PROTOTYPE: swap the local player's body mesh to a vanilla-derived stand-in (proves the runtime "
                + "model-swap path). Toggle live or with `va_model on|off`. The real custom-model swap plugs in here.");
            configPlayerModelInflate = config.Bind(
                "10e - Player Model", "02 StandInInflate", 0.05f,
                new ConfigDescription("Stand-in proof: how far (m) the body puffs out along its normals when enabled, so you can "
                    + "SEE the swap took. Purely a prototype knob.", new AcceptableValueRange<float>(0f, 0.3f)));
            configPlayerModelIndex = config.Bind(
                "10e - Player Model", "03 Model", 0,
                new ConfigDescription("Which bundled model to swap in — slide between models (0 = first). `va_model list` "
                    + "shows the order; the selected model's name is logged when you slide. Persists across restarts.",
                    new AcceptableValueRange<int>(0, 9)));
            */

            // ===================================================================
            // 10c - Hair Physics (verlet swing on the local player's hair)
            // ===================================================================
            configHairPhysEnabled = config.Bind(
                "10c - Hair Physics", "01 Enabled", false,
                "Give your character's hair physics (swings with movement/gravity/wind instead of staying "
                + "rigid). OFF by default. Toggle live or with `va_hair on|off`. Tune the feel below.");

            configHairStiffness = config.Bind(
                "10c - Hair Physics", "02 Stiffness", 6f,
                new ConfigDescription("How strongly the hair springs back to rest. Higher = stiffer/less floppy.",
                    new AcceptableValueRange<float>(0.5f, 30f)));

            configHairDamping = config.Bind(
                "10c - Hair Physics", "03 Damping", 0.92f,
                new ConfigDescription("Inertia retained per frame. Lower = more drag (settles fast); higher = more swing/overshoot.",
                    new AcceptableValueRange<float>(0.5f, 0.99f)));

            configHairGravity = config.Bind(
                "10c - Hair Physics", "04 Gravity", 2.5f,
                new ConfigDescription("Downward pull on the hair. 0 = floaty; higher = hangs heavier.",
                    new AcceptableValueRange<float>(0f, 12f)));

            configHairWind = config.Bind(
                "10c - Hair Physics", "05 WindInfluence", 0.4f,
                new ConfigDescription("How much the world wind pushes the hair. 0 = ignore wind.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configHairMaxSway = config.Bind(
                "10c - Hair Physics", "06 MaxSway", 0.30f,
                new ConfigDescription("Safety clamp: max distance (m) any hair/beard point may swing from rest. Raise for long, "
                    + "free-swinging hair; lower if it stretches weirdly.",
                    new AcceptableValueRange<float>(0.02f, 1.0f)));

            configHairCollision = config.Bind(
                "10c - Hair Physics", "07 HeadRadius", 0.11f,
                new ConfigDescription("Radius of the head sphere the strands can't pass through (stops clipping into the head).",
                    new AcceptableValueRange<float>(0f, 0.3f)));

            configHairBodyRadius = config.Bind(
                "10c - Hair Physics", "08 BodyRadius", 0.15f,
                new ConfigDescription("Radius of the torso capsule the hair/beard drape over instead of clipping through the body. "
                    + "0 = body collision off. Match it to your character's front-to-back thickness (the beard sits on the chest at this radius).",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            configHairBodyTop = config.Bind(
                "10c - Hair Physics", "09 BodyTopHeight", 1.5f,
                new ConfigDescription("Top of the torso capsule (shoulders), in metres up from the feet. Tune if hair clips the upper body.",
                    new AcceptableValueRange<float>(0.5f, 2.0f)));

            configHairBodyBottom = config.Bind(
                "10c - Hair Physics", "10 BodyBottomHeight", 0.6f,
                new ConfigDescription("Bottom of the torso capsule (hips), in metres up from the feet.",
                    new AcceptableValueRange<float>(0f, 1.5f)));

            configHairShoulderRadius = config.Bind(
                "10c - Hair Physics", "11 ShoulderRadius", 0.12f,
                new ConfigDescription("Radius of the two shoulder collision spheres that keep hair/beard riding OVER the shoulders "
                    + "and upper chest (instead of sinking in when you look up/down). 0 = off. The spine capsule alone can't reach the shoulders.",
                    new AcceptableValueRange<float>(0f, 0.3f)));

            configHairShoulderWidth = config.Bind(
                "10c - Hair Physics", "12 ShoulderWidth", 0.16f,
                new ConfigDescription("Half-distance from the spine to each shoulder sphere (m). Widen to match broad shoulders.",
                    new AcceptableValueRange<float>(0f, 0.4f)));

            // ===================================================================
            // 10e - Strand Anchor (nudge where hair/beard physics is pinned on the head)
            // ===================================================================
            // Head-local metres. The rest pose is otherwise pinned to vanilla's exact hair position;
            // these let you shove the whole strand onto the head by eye if the auto anchor is off.
            configHairAnchorSide = config.Bind(
                "10e - Strand Anchor", "01 HairSide", 0f,
                new ConfigDescription("Hair root: sideways nudge (m). + = toward the right ear.", new AcceptableValueRange<float>(-1.0f, 1.0f)));
            configHairAnchorUp = config.Bind(
                "10e - Strand Anchor", "02 HairUp", 0f,
                new ConfigDescription("Hair root: up/down nudge (m). + = up toward the crown.", new AcceptableValueRange<float>(-1.0f, 1.0f)));
            configHairAnchorForward = config.Bind(
                "10e - Strand Anchor", "03 HairForward", 0f,
                new ConfigDescription("Hair root: forward/back nudge (m). + = toward the face.", new AcceptableValueRange<float>(-1.0f, 1.0f)));

            configBeardAnchorSide = config.Bind(
                "10e - Strand Anchor", "04 BeardSide", 0f,
                new ConfigDescription("Beard root: sideways nudge (m).", new AcceptableValueRange<float>(-1.0f, 1.0f)));
            configBeardAnchorUp = config.Bind(
                "10e - Strand Anchor", "05 BeardUp", 0f,
                new ConfigDescription("Beard root: up/down nudge (m).", new AcceptableValueRange<float>(-1.0f, 1.0f)));
            configBeardAnchorForward = config.Bind(
                "10e - Strand Anchor", "06 BeardForward", 0f,
                new ConfigDescription("Beard root: forward/back nudge (m). + = toward the face.", new AcceptableValueRange<float>(-1.0f, 1.0f)));

            configHairSwayFalloff = config.Bind(
                "10e - Strand Anchor", "07 SwayFalloff", 1.5f,
                new ConfigDescription("How motion is distributed along the strand (hair AND beard). 0 = the whole strand sways "
                    + "equally; higher = the part near the root stays anchored and the sway is pushed toward the tips. "
                    + "~1.5 keeps the scalp/jaw stiff while the ends flow.",
                    new AcceptableValueRange<float>(0f, 5f)));

            // ===================================================================
            // 10d - Hair & Beard (style + color on the local player)
            // ===================================================================
            configHairStyle = config.Bind(
                "10d - Hair & Beard", "01 HairStyle", -1,
                new ConfigDescription("Hair style index. -1 = leave your character's hair as-is. Drag to swap styles "
                    + "(applies instantly and persists to your character). Run `va_styles` in the console to see which "
                    + "index is which name.", new AcceptableValueRange<int>(-1, 63)));

            configBeardStyle = config.Bind(
                "10d - Hair & Beard", "02 BeardStyle", -1,
                new ConfigDescription("Beard style index. -1 = leave as-is. Same as hair: drag to swap, `va_styles` lists "
                    + "the indices. (Female model 1 has no beard.)", new AcceptableValueRange<int>(-1, 63)));

            configHairColorEnabled = config.Bind(
                "10d - Hair & Beard", "03 HairColorEnabled", false,
                "Override your hair color with the color below. OFF = keep your character's existing hair color. "
                + "This uses the vanilla hair-color path, so it also tints the beard unless a beard override is set.");

            configHairColor = config.Bind(
                "10d - Hair & Beard", "04 HairColor", UnityEngine.Color.white,
                "Hair color (used when HairColorEnabled is ON). Persists to your character and is visible to other players.");

            configBeardColorEnabled = config.Bind(
                "10d - Hair & Beard", "05 BeardColorEnabled", false,
                "Give the beard its OWN color, separate from the hair (vanilla ties them together). OFF = beard follows "
                + "the hair color. This override is local to your client.");

            configBeardColor = config.Bind(
                "10d - Hair & Beard", "06 BeardColor", UnityEngine.Color.white,
                "Beard color (used when BeardColorEnabled is ON).");

            configDistanceFogEnabled = config.Bind(
                "08 - Distance Fog", "01 VanillaFogEnabled", true,
                "Master switch for Valheim's distance fog (RenderSettings.fog), applied by EnvMan every frame. "
                + "OFF = no distance fog at all. DIAGNOSTIC: if turning this off makes a distant/blue object snap "
                + "back to its real look, that object was being saturated by the fog colour. Leave ON for normal play.");

            configDistanceFogDensityScale = config.Bind(
                "08 - Distance Fog", "02 FogDensityScale", 1.0f,
                new ConfigDescription(
                    "Multiplies the environment's fog density (ONLY affects Exponential / ExponentialSquared "
                    + "modes — a no-op if the env fog is Linear). 1 = vanilla, 0 = none, 0.5 = thinner. NOTE: "
                    + "for far objects this barely moves the haze (exp fog is already saturated km out) — to "
                    + "actually control render distance use FogMode = Linear with the START/END below.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configDistanceFogColorOverride = config.Bind(
                "08 - Distance Fog", "03 FogColorOverride", false,
                "Replace the environment's fog COLOUR/HUE with a fixed colour (04 below). OFF = use the "
                + "biome/time-of-day fog colour. This sets what colour the distance haze — and therefore far "
                + "vanilla-material objects — fade toward.");

            configDistanceFogColor = config.Bind(
                "08 - Distance Fog", "04 FogColor", new UnityEngine.Color(0.6f, 0.66f, 0.72f, 1f),
                "Fog colour used when '03 FogColorOverride' is ON.");

            configDistanceFogMode = config.Bind(
                "08 - Distance Fog", "05 FogMode", VerdantsAscent.Water.Core.FiresFogControl.ModeOption.ExponentialSquared,
                "Fog falloff curve. Auto = keep the environment's mode (density-driven). Linear = explicit "
                + "START/END distance (06/07) — THIS is the 'how far before things fog out' lever, since "
                + "density can't push exponential fog's range. Exponential / ExponentialSquared force that "
                + "curve and use FogDensityScale.");

            configDistanceFogLinearStart = config.Bind(
                "08 - Distance Fog", "06 FogLinearStart", 200f,
                new ConfigDescription(
                    "Metres from camera where LINEAR fog STARTS (fully clear nearer than this). Only used when "
                    + "FogMode = Linear.",
                    new AcceptableValueRange<float>(0f, 8000f)));

            configDistanceFogLinearEnd = config.Bind(
                "08 - Distance Fog", "07 FogLinearEnd", 2500f,
                new ConfigDescription(
                    "Metres from camera where LINEAR fog reaches FULL fog colour (everything past this is solid "
                    + "haze). Raise to see distant terrain / baked meshes clearly. Only used when FogMode = Linear.",
                    new AcceptableValueRange<float>(50f, 12000f)));

            // ===================================================================
            // 08 - Volumetric Fog (custom height-banded, patchy, wind-blown — REAL fog)
            // One master switch + three independent zones, each with its own Enabled
            // toggle. Picks the zone per-pixel: camera underwater -> Underwater; ray
            // over water -> Sea (sits on the water, bobs with the tide); else -> Land.
            // Turn the flat distance fog (section 03) OFF and use this so distant
            // objects aren't masked white.
            // NEEDS 'fireswater' rebuilt with the Custom/FiresVolFog shader.
            // ===================================================================
            configVolFogEnabled = config.Bind("09 - Volumetric Fog", "01 MasterEnabled", true,
                "Master switch for all three volumetric-fog zones. Off = the whole pass is removed (no cost).");
            configVolFogDomainConfine = config.Bind("09 - Volumetric Fog", "02 ConfineToDomains", true,
                "Keep land fog over land and sea fog over water, blending across the coastline (instead of both bands "
                + "overlapping everywhere). Uses the shore field's water-ness — the coastline crossfade width is "
                + "'07 - Shore & Waves / 08 FogCoastlineBlend'. Off = the old full-overlap behaviour.");
            configVolFogSkyBlend = config.Bind("09 - Volumetric Fog", "02b SkyBlend", 0.35f,
                new ConfigDescription(
                    "How far up the ground fog (LAND + SEA) fades to clear as you look above the horizon, so it DISSOLVES "
                    + "into the sky instead of walling up with a hard top line / a hard bright silhouette against distant "
                    + "terrain. Value ~= sin(elevation) where the fog reaches clear: 0.35 ~ 20 degrees. Higher = fades higher "
                    + "up (softer, more sky shows through); lower = keeps the fog higher; 0 = OFF (old hard-topped look).",
                    new AcceptableValueRange<float>(0f, 1f)));
            configVolFogCaveAware = config.Bind("09 - Volumetric Fog", "02c CaveAware", true,
                "While the camera is in a cave (FiresAdminTerrain's cave test, the same one its clutter uses), the LAND and SEA fog bands "
                + "fade out, so a grotto inside the land band's height range is not washed. Outside caves nothing changes. Off = the old behaviour.");
            // 0.3.157: new key name so the 4 s default reaches existing cfgs (the old 02d CaveFadeSeconds held 1 s).
            configVolFogCaveFade = config.Bind("09 - Volumetric Fog", "02d CaveFadeSecs", 4f,
                new ConfigDescription("Seconds the land and sea fog take to fade out on entering a cave and back on leaving (unused when FAT gives a continuous cave factor).", new AcceptableValueRange<float>(0f, 10f)));
            configVolFogCaveHold = config.Bind("09 - Volumetric Fog", "02g CaveFlipHoldSeconds", 3f,
                new ConfigDescription("An in/out-of-cave flip only counts once it has held this long, so cave mouths don't flicker the fog (unused when FAT gives a continuous cave factor).", new AcceptableValueRange<float>(0f, 10f)));
            configVolFogUwPerObject = config.Bind("09 - Volumetric Fog", "02e UnderwaterPerObjectFog", 0.15f,
                new ConfigDescription("Underwater, how much of the old per-object fog stays while the volumetric murk is on (0 = off, 1 = old look).", new AcceptableValueRange<float>(0f, 1f)));
            configVolFogCaveRender = config.Bind("09 - Volumetric Fog", "02f CaveFogStrength", 0.25f,
                new ConfigDescription("In caves and grottos, how much of the weather's own fog stays (0 = none, 1 = the full outdoor fog). Live.", new AcceptableValueRange<float>(0f, 1f)));
            // 0.3.155: the sinkhole-shaft fog band (needs FiresAdminTerrain 0.2.825+, which lists the shafts)
            configVfShaftOn = config.Bind("09e - Fog Shaft Band", "01 Enabled", true,
                "A soft, translucent fog band in each nearby sinkhole shaft, from the river bed to just under the river surface, seen from the grotto below. Never opaque.");
            configVfShaftColor = config.Bind("09e - Fog Shaft Band", "02 Color", new UnityEngine.Color(0.70f, 0.76f, 0.82f, 1f), "The band's haze colour.");
            configVfShaftDensity = config.Bind("09e - Fog Shaft Band", "03 Density", 0.02f,
                new ConfigDescription("How fast the haze builds along a view ray through the band (per metre). Higher = thicker sooner.", new AcceptableValueRange<float>(0f, 0.5f)));
            configVfShaftMaxOp = config.Bind("09e - Fog Shaft Band", "04 MaxOpacity", 0.18f,
                new ConfigDescription("The band's opacity cap, so distant things keep their colours through it (hard limit 0.6).", new AcceptableValueRange<float>(0f, 0.6f)));
            configVfShaftReach = config.Bind("09e - Fog Shaft Band", "05 Reach", 16f,
                new ConfigDescription("Metres the band extends past the shaft's radius (its soft outer edge), so it reads from the grotto floor.", new AcceptableValueRange<float>(0f, 64f)));
            configVfShaftTopGap = config.Bind("09e - Fog Shaft Band", "06 TopGap", 0.5f,
                new ConfigDescription("Metres below the river surface where the band stops.", new AcceptableValueRange<float>(0f, 10f)));
            configVfShaftVFade = config.Bind("09e - Fog Shaft Band", "07 EdgeFade", 1.5f,
                new ConfigDescription("Softness (m) of the band's top and bottom edges.", new AcceptableValueRange<float>(0.01f, 10f)));

            // ── 08a LAND (over terrain) ──
            configVfLandOn = config.Bind("09a - Fog Land", "01 Enabled", true, "Fog over land (terrain).");
            configVfLandColor = config.Bind("09a - Fog Land", "02 Color", new UnityEngine.Color(0.72f, 0.78f, 0.84f, 1f), "DENSE land-fog hue (the thick patches).");
            configVfLandColorThin = config.Bind("09a - Fog Land", "02b ColorThin", new UnityEngine.Color(0.72f, 0.78f, 0.84f, 1f),
                "THIN land-fog hue (the wispy sections). Set different from Color and the fog blends between the two hues by patch density.");
            configVfLandMin = config.Bind("09a - Fog Land", "03 HeightMin", 14.236f,
                new ConfigDescription("Bottom of the land fog slab (world Y).", new AcceptableValueRange<float>(-50f, 2000f)));
            configVfLandMax = config.Bind("09a - Fog Land", "04 HeightMax", 50f,
                new ConfigDescription("Top of the land fog slab (world Y) — hills above this poke out clear.", new AcceptableValueRange<float>(-50f, 3000f)));
            configVfLandBotFade = config.Bind("09a - Fog Land", "04a BottomFade", 0.01f,
                new ConfigDescription("Soft bottom edge (m) — the fog fades in over this height instead of a hard line.", new AcceptableValueRange<float>(0.01f, 200f)));
            configVfLandTopFade = config.Bind("09a - Fog Land", "04b TopFade", 159.38f,
                new ConfigDescription("Soft top edge (m) — the fog thins out over this height into clear air.", new AcceptableValueRange<float>(0.01f, 300f)));
            configVfLandDensity = config.Bind("09a - Fog Land", "05 Density", 1.5f,
                new ConfigDescription("Thickness per metre of slab the view ray crosses (before the cap).", new AcceptableValueRange<float>(0f, 8f)));
            configVfLandMaxOp = config.Bind("09a - Fog Land", "06 MaxOpacity", 0.593f,
                new ConfigDescription("Hard cap so detail always shows through (0.8 = always >=20% visible).", new AcceptableValueRange<float>(0f, 1f)));
            configVfLandPatchScale = config.Bind("09a - Fog Land", "07 PatchScale", 0.1f,
                new ConfigDescription("Patch size — smaller = broader patches.", new AcceptableValueRange<float>(0.0005f, 0.3f)));
            configVfLandPatch = config.Bind("09a - Fog Land", "08 Patchiness", 1f,
                new ConfigDescription("Overall patch strength. 0 = flat uniform fog, 1 = full dense/thin variation.", new AcceptableValueRange<float>(0f, 1f)));
            configVfLandCoverage = config.Bind("09a - Fog Land", "08a Coverage", 1f,
                new ConfigDescription("How much of the area is dense vs thin. Low = mostly thin with a few dense banks, high = mostly dense with a few thin lanes.", new AcceptableValueRange<float>(0f, 1f)));
            configVfLandClearOp = config.Bind("09a - Fog Land", "08b ThinOpacity", 0.048f,
                new ConfigDescription("Density floor of the THIN sections (relative to dense). 0 = thin spots nearly clear, 1 = no variation. They never go fully clear unless you set 0.", new AcceptableValueRange<float>(0f, 1f)));
            configVfLandPatchFade = config.Bind("09a - Fog Land", "08c PatchFade", 0.267f,
                new ConfigDescription("Softness of the thin<->dense transition. Low = crisper patch edges, high = long rolling gradients.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            configVfLandWind = config.Bind("09a - Fog Land", "09 WindSpeed", 0.109f,
                new ConfigDescription("Drift speed along the world wind. 0 = static.", new AcceptableValueRange<float>(0f, 0.5f)));
            configVfLandFarCut = config.Bind("09a - Fog Land", "12 FarCutoff", 2000f,
                new ConfigDescription("Fog fades back out past this distance (m) so it never washes the sky dome / "
                    + "orbitals / moon (which sit ~5000m+). Keep it below your dome radius. Near/mid terrain still hazes.",
                    new AcceptableValueRange<float>(200f, 12000f)));
            configVfLandUndulate = config.Bind("09a - Fog Land", "13 EdgeRoll", 5f,
                new ConfigDescription("How far (m) the fog top + bottom roll up/down from HeightMin/Max, so the edges undulate "
                    + "like real fog instead of a flat plane. HeightMax becomes the CENTRE of the roll, not a hard cap. 0 = flat edges.",
                    new AcceptableValueRange<float>(0f, 40f)));

            // ── 08b SEA (above water, on the water surface) ──
            configVfSeaOn = config.Bind("09b - Fog Sea", "01 Enabled", true, "Fog over open water — sits ON the surface and bobs with the tide.");
            configVfSeaColor = config.Bind("09b - Fog Sea", "02 Color", new UnityEngine.Color(0.74f, 0.82f, 0.88f, 1f), "DENSE sea-fog hue (the thick patches).");
            configVfSeaColorThin = config.Bind("09b - Fog Sea", "02b ColorThin", new UnityEngine.Color(0.74f, 0.82f, 0.88f, 1f),
                "THIN sea-fog hue (the wispy sections). Set different from Color and the fog blends between the two hues by patch density.");
            configVfSeaBelow = config.Bind("09b - Fog Sea", "03 SlabBelow", 6f,
                new ConfigDescription("How far the slab reaches BELOW the water surface (m).", new AcceptableValueRange<float>(0f, 100f)));
            configVfSeaThick = config.Bind("09b - Fog Sea", "04 SlabAbove", 16.669f,
                new ConfigDescription("How far the slab reaches ABOVE the water surface (m) — the fog ceiling.", new AcceptableValueRange<float>(0.1f, 300f)));
            configVfSeaBotFade = config.Bind("09b - Fog Sea", "04a BottomFade", 26.914f,
                new ConfigDescription("Soft bottom edge (m) — fog fades in from below the surface instead of a hard line.", new AcceptableValueRange<float>(0.01f, 100f)));
            configVfSeaTopFade = config.Bind("09b - Fog Sea", "04b TopFade", 15f,
                new ConfigDescription("Soft top edge (m) — fog thins out into clear air over this height.", new AcceptableValueRange<float>(0.01f, 200f)));
            configVfSeaDensity = config.Bind("09b - Fog Sea", "05 Density", 2.572f,
                new ConfigDescription("Thickness per metre of slab crossed (before the cap).", new AcceptableValueRange<float>(0f, 4f)));
            configVfSeaMaxOp = config.Bind("09b - Fog Sea", "06 MaxOpacity", 0.242f,
                new ConfigDescription("Hard cap so detail always shows through.", new AcceptableValueRange<float>(0f, 1f)));
            configVfSeaPatchScale = config.Bind("09b - Fog Sea", "07 PatchScale", 0.05f,
                new ConfigDescription("Patch size — smaller = broader patches.", new AcceptableValueRange<float>(0.0005f, 0.1f)));
            configVfSeaPatch = config.Bind("09b - Fog Sea", "08 Patchiness", 1f,
                new ConfigDescription("Overall patch strength. 0 = flat uniform fog, 1 = full dense/thin variation.", new AcceptableValueRange<float>(0f, 1f)));
            configVfSeaCoverage = config.Bind("09b - Fog Sea", "08a Coverage", 0.851f,
                new ConfigDescription("How much of the area is dense vs thin. Low = mostly thin with a few dense banks, high = mostly dense.", new AcceptableValueRange<float>(0f, 1f)));
            configVfSeaClearOp = config.Bind("09b - Fog Sea", "08b ThinOpacity", 0.044f,
                new ConfigDescription("Density floor of the THIN sections (relative to dense). 0 = thin spots nearly clear, 1 = no variation.", new AcceptableValueRange<float>(0f, 1f)));
            configVfSeaPatchFade = config.Bind("09b - Fog Sea", "08c PatchFade", 0.205f,
                new ConfigDescription("Softness of the thin<->dense transition. Low = crisper patch edges, high = long rolling gradients.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            configVfSeaWind = config.Bind("09b - Fog Sea", "09 WindSpeed", 0.05f,
                new ConfigDescription("Drift speed along the world wind.", new AcceptableValueRange<float>(0f, 0.5f)));
            configVfSeaFarCut = config.Bind("09b - Fog Sea", "12 FarCutoff", 4000f,
                new ConfigDescription("Fog fades back out past this distance (m) so it never washes the sky dome / "
                    + "horizon. Keep it below your dome radius.", new AcceptableValueRange<float>(200f, 12000f)));
            configVfSeaUndulate = config.Bind("09b - Fog Sea", "13 EdgeRoll", 5f,
                new ConfigDescription("How far (m) the fog top + bottom roll up/down from the SlabBelow/SlabAbove band, so the "
                    + "edges undulate like real fog instead of a flat plane. The ceiling becomes the CENTRE of the roll. 0 = flat edges.",
                    new AcceptableValueRange<float>(0f, 40f)));

            // ── 08c UNDERWATER (camera below the surface) ──
            configVfUwOn = config.Bind("09c - Fog Underwater", "01 Enabled", true, "Fog while the camera is below the water surface — reacts to currents.");
            configVfUwColor = config.Bind("09c - Fog Underwater", "02 Color", new UnityEngine.Color(0.07f, 0.17f, 0.27f, 1f),
                "DEEP/far murk colour — a natural desaturated blue-grey (NOT neon). The water grades from ColorThin (near) to this. "
                + "Keep it close to ColorThin for a soft, realistic deepening; the extra blue here reads as water absorbing warm light with depth.");
            configVfUwColorThin = config.Bind("09c - Fog Underwater", "02b ColorThin", new UnityEngine.Color(0.16f, 0.28f, 0.30f, 1f),
                "NEAR/shallow murk colour — a slightly warmer grey-teal. The water grades from this (close) to 02 Color (far/deep). Set both equal for a single flat tone.");
            // One-time palette migration: retire the old neon-blue deep + teal near defaults in favour of the
            // natural blue-grey. Only rewrites values still sitting at the OLD defaults, so any real tuning survives.
            if (SameColor(configVfUwColor.Value, new UnityEngine.Color(0f, 0.263f, 1f, 1f)))
                configVfUwColor.Value = new UnityEngine.Color(0.07f, 0.17f, 0.27f, 1f);
            if (SameColor(configVfUwColorThin.Value, new UnityEngine.Color(0.10f, 0.30f, 0.38f, 1f)))
                configVfUwColorThin.Value = new UnityEngine.Color(0.16f, 0.28f, 0.30f, 1f);
            configVfUwDensity = config.Bind("09c - Fog Underwater", "03 Density", 1.728f,
                new ConfigDescription("Overall murk thickness (distance-driven).", new AcceptableValueRange<float>(0f, 2f)));
            configVfUwMaxOp = config.Bind("09c - Fog Underwater", "04 MaxOpacity", 0.847f,
                new ConfigDescription("Hard cap so you still see SOME of the seafloor through it.", new AcceptableValueRange<float>(0f, 1f)));
            configVfUwPatch = config.Bind("09c - Fog Underwater", "04a Patchiness", 0.3f,
                new ConfigDescription("Break the murk into clearer pockets + denser banks (same model as the land/sea fog). "
                    + "0 = uniform murk, higher = more varied. Keep modest underwater so clear pockets don't reveal the world edge.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configVfUwCoverage = config.Bind("09c - Fog Underwater", "04b Coverage", 0.5f,
                new ConfigDescription("How much of the water the dense banks cover. Low = mostly clear with a few thick spots, high = mostly thick.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configVfUwClearOp = config.Bind("09c - Fog Underwater", "04c ThinOpacity", 0.6f,
                new ConfigDescription("Murk opacity INSIDE the clear pockets (fraction of full). 1 = pockets stay full (no clearing), "
                    + "lower = clearer windows. Underwater keep this fairly high so the pockets don't expose the world edge.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configVfUwPatchFade = config.Bind("09c - Fog Underwater", "04d PatchFade", 0.3f,
                new ConfigDescription("Soft edge width between clear and dense — small = crisp pockets, large = gentle blend.",
                    new AcceptableValueRange<float>(0.02f, 1f)));
            configVfUwPatchScale = config.Bind("09c - Fog Underwater", "05 SwirlScale", 0.02f,
                new ConfigDescription("Size of the murk swirls — smaller = broader.", new AcceptableValueRange<float>(0.001f, 0.2f)));
            configVfUwSwirl = config.Bind("09c - Fog Underwater", "06 SwirlAmount", 1.558f,
                new ConfigDescription("How much the murk churns/turns (domain warp). 0 = smooth, higher = roiling.", new AcceptableValueRange<float>(0f, 2f)));
            configVfUwCurrent = config.Bind("09c - Fog Underwater", "07 CurrentSpeed", 0.05f,
                new ConfigDescription("How fast the murk drifts along the current (wind vector under water). 0 = still.", new AcceptableValueRange<float>(0f, 0.5f)));
            configVfUwDistStart = config.Bind("09c - Fog Underwater", "08 ClearDistance", 2f,
                new ConfigDescription("Clear within this distance (m) of the camera.", new AcceptableValueRange<float>(0f, 100f)));
            configVfUwDistRange = config.Bind("09c - Fog Underwater", "09 FadeInDistance", 263.51f,
                new ConfigDescription("Distance (m) to ramp to full murk — short = thick soup, long = clear water.", new AcceptableValueRange<float>(1f, 500f)));
            configVfUwFarCut = config.Bind("09c - Fog Underwater", "10 FarCutoff", 400f,
                new ConfigDescription("Murk fades out past this distance (m) so it hides the near seabed but never paints the "
                    + "sky dome when you look up through the surface (the 'weird textures on the dome' fix). Keep it well below the dome radius.",
                    new AcceptableValueRange<float>(50f, 3000f)));
            configVfUwDepthDensity = config.Bind("09c - Fog Underwater", "11 DepthDensity", 1f,
                new ConfigDescription("Murk multiplier for DEEP water — 1 = uniform with depth, >1 = the deeper you look the thicker/darker "
                    + "the murk (denser trenches, lighter shallows).", new AcceptableValueRange<float>(0f, 3f)));
            configVfUwDepthSpan = config.Bind("09c - Fog Underwater", "11a DepthSpan", 30f,
                new ConfigDescription("Metres below the surface to reach the full 11 DepthDensity (only matters when DepthDensity ≠ 1).",
                    new AcceptableValueRange<float>(1f, 200f)));

            // ===================================================================
            // 09d - Near-Player Push (shove the land/sea fog aside as you move)
            // Within Radius the fog's coverage is warped OUTWARD so the mist parts and
            // flows around you as you walk, like you are the wind. Density-preserving —
            // the fog is displaced, not cleared/faded. Works best with some Patchiness
            // (09a/09b) so there are wisps to part.
            // ===================================================================
            configVfPushOn = config.Bind("09d - Near-Player Push", "01 Enabled", true,
                "Shove the land + sea fog aside around you as you move — the mist parts and flows around you like you're the "
                + "wind. It's a displacement (the fog moves away), NOT a clear/fade. Most visible on patchy/wispy fog.");
            configVfPushRadius = config.Bind("09d - Near-Player Push", "02 Radius", 33.642f,
                new ConfigDescription("How far from you the push reaches (m). The mist is shoved aside within this radius, settling back to "
                    + "normal past it.", new AcceptableValueRange<float>(1f, 80f)));
            configVfPushStrength = config.Bind("09d - Near-Player Push", "03 Strength", 0.455f,
                new ConfigDescription("How hard the mist is shoved aside (0 = none, 1 = maximum). Higher = a bigger, clearer pocket parts "
                    + "around you.", new AcceptableValueRange<float>(0f, 1f)));

            // ===================================================================
            // 08e - Fog Weather (drive the land/sea fog from the live environment)
            // Reads the current env's blended fog colour + density + rain each frame
            // and blends it onto your configured fog: tint toward the env hue, thicken
            // in mist/rain/storms. Your 08a/08b colours + density stay the BASE.
            // ===================================================================
            configVfEnvReactive = config.Bind("09e - Fog Weather", "01 EnvReactive", true,
                "Make the land + sea fog react to the current weather/environment (tint + thickness shift with mist, rain, "
                + "storms, time of day). Off = the fog stays exactly at your 08a/08b settings.");
            configVfEnvColorBlend = config.Bind("09e - Fog Weather", "02 ColorBlend", 0.6f,
                new ConfigDescription("How far the land/sea fog hue shifts toward the current environment's fog colour. 0 = keep your "
                    + "configured colours, 1 = fully match the env fog colour.", new AcceptableValueRange<float>(0f, 1f)));
            configVfEnvDensityBlend = config.Bind("09e - Fog Weather", "03 DensityBlend", 0.5f,
                new ConfigDescription("How much the environment's fog density thickens your fog (mist/foggy envs get denser). 0 = ignore "
                    + "env density, 1 = strong response.", new AcceptableValueRange<float>(0f, 2f)));
            configVfEnvRainBoost = config.Bind("09e - Fog Weather", "04 RainBoost", 2f,
                new ConfigDescription("Extra fog density during rain/storms (scaled by the env's rain-cloud amount). 0 = rain doesn't "
                    + "thicken the fog.", new AcceptableValueRange<float>(0f, 4f)));
            configVfEnvStormOpacity = config.Bind("09e - Fog Weather", "05 StormOpacity", 0.381f,
                new ConfigDescription("Opacity cap the fog is allowed to reach in heavy weather, so the density boost is actually visible "
                    + "(your 08a/08b MaxOpacity is the clear-weather cap). 0 = never raise the cap.", new AcceptableValueRange<float>(0f, 1f)));
            configVfEnvAffectUw = config.Bind("09e - Fog Weather", "06 AffectUnderwater", true,
                "Also tint the underwater murk toward the env fog colour. Usually off — underwater has its own look.");
            configVfClearDayFog = config.Bind("09e - Fog Weather", "07 ClearDayFog", 0.08f,
                new ConfigDescription("Fraction of the base fog that shows on a DEAD-CLEAR day. Vanilla clear days are nearly "
                    + "fog-free — the fog belongs to dawn/dusk, mist and rain. 0 = perfectly clear midday, the env still ramps "
                    + "fog up in weather; 1 = the old 'foggy all the time' look. Low values (0.05–0.15) match vanilla.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ===================================================================
            // 08f - Fog Clouds (high-altitude volumetric cloud band)
            // A second fog band floating ABOVE the ground fog (own world-Y min/max),
            // drawn everywhere with no player dip or land/sea confine — overhead cloud
            // cover. Weather-reactive via 08e (thickens/darkens in storms).
            // ===================================================================
            configVfCloudOn = config.Bind("09f - Fog Clouds", "01 Enabled", false,
                "A high-altitude volumetric CLOUD band, separate from the ground fog — sits between MinHeight and MaxHeight "
                + "(world Y) as overhead cloud cover. Off by default; turn on and set the heights above your play area.");
            configVfCloudColor = config.Bind("09f - Fog Clouds", "02 Color", new UnityEngine.Color(0.70f, 0.74f, 0.80f, 1f),
                "Cloud colour. Weather (08e) tints it toward the env fog in storms.");
            configVfCloudDensity = config.Bind("09f - Fog Clouds", "03 Density", 4f,
                new ConfigDescription("Cloud thickness.", new AcceptableValueRange<float>(0f, 8f)));
            configVfCloudMaxOp = config.Bind("09f - Fog Clouds", "04 MaxOpacity", 1f,
                new ConfigDescription("Opacity cap (1 = fully solid/light-blocking overcast).", new AcceptableValueRange<float>(0f, 1f)));
            configVfCloudMin = config.Bind("09f - Fog Clouds", "05 MinHeight", 600f,
                new ConfigDescription("Bottom of the cloud band (world Y). Keep it above your normal play altitude + the ground fog so there's a clear GAP between them.",
                    new AcceptableValueRange<float>(0f, 6000f)));
            configVfCloudMax = config.Bind("09f - Fog Clouds", "06 MaxHeight", 7250f,
                new ConfigDescription("Top of the cloud band (world Y). The band's thickness (Max−Min) sets how deep the cloud deck is.",
                    new AcceptableValueRange<float>(0f, 8000f)));
            configVfCloudBotFade = config.Bind("09f - Fog Clouds", "07 BottomFade", 27.917f,
                new ConfigDescription("Soft bottom edge of the deck (m) — fades in instead of a hard underside.", new AcceptableValueRange<float>(0.01f, 1000f)));
            configVfCloudTopFade = config.Bind("09f - Fog Clouds", "08 TopFade", 1000f,
                new ConfigDescription("Soft top edge of the deck (m).", new AcceptableValueRange<float>(0.01f, 2000f)));
            configVfCloudCoverage = config.Bind("09f - Fog Clouds", "09 Coverage", 0.608f,
                new ConfigDescription("How much of the sky is covered vs broken. Low = scattered clouds, high = solid overcast.", new AcceptableValueRange<float>(0f, 1f)));
            configVfCloudPatch = config.Bind("09f - Fog Clouds", "10 Patchiness", 0f,
                new ConfigDescription("Dense/thin variation across the deck (0 = flat sheet, 1 = broken cloud masses).", new AcceptableValueRange<float>(0f, 1f)));
            configVfCloudPatchScale = config.Bind("09f - Fog Clouds", "11 CloudScale", 0.0002f,
                new ConfigDescription("Cloud cell size — smaller = larger, broader cloud masses.", new AcceptableValueRange<float>(0.00005f, 0.02f)));
            configVfCloudUndulate = config.Bind("09f - Fog Clouds", "12 EdgeRoll", 400f,
                new ConfigDescription("How far (m) the deck's top/bottom roll so the edges undulate instead of flat planes. The rolls drift at "
                    + "DriftSpeed (below), so even a big roll stays a slow heave rather than a storm churn.", new AcceptableValueRange<float>(0f, 800f)));
            configVfCloudDrift = config.Bind("09f - Fog Clouds", "13 DriftSpeed", 0.02f,
                new ConfigDescription("How fast the deck drifts — in the wind's DIRECTION but at a FIXED speed, independent of wind strength, so "
                    + "the clouds stay a calm overcast and do NOT speed up / churn / fall when storms pick the wind up. 0 = static.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            // ===================================================================
            // 07 - Shore & Waves (shore-ramped wave height + depth-darkened water)
            // A world-XZ field baked from the terrain feeds the water shader:
            // waves calm near shore and build to full in deep ocean, and water
            // darkens with depth. Seam-free (sampled by world position).
            // ===================================================================
            configShoreFieldEnabled = config.Bind("07 - Shore & Waves", "01 Enabled", true,
                "Ramp waves down near shore (calm at the waterline → full in deep ocean) and darken water by depth. "
                + "Off = full-height waves everywhere and flat 'deep' colour (the old behaviour).");
            configShoreWaveFloor = config.Bind("07 - Shore & Waves", "02 ShoreWaveStrength", 0f,
                new ConfigDescription("Wave strength right at the shoreline. 0 = vanilla (waves vanish at the water's edge, so "
                    + "the rendered surface matches the buoyancy surface the body floats on); above 0 raises near-shore waves "
                    + "but re-introduces a body-vs-visual mismatch there.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configShoreWaveFullDepth = config.Bind("07 - Shore & Waves", "03 FullWaveDepth", 10f,
                new ConfigDescription("Water DEPTH (m) at which waves reach full strength — welded to vanilla WaterVolume, which "
                    + "uses 10. Keep at 10 so the visual wave height equals the physics/swim surface; lower makes waves build up "
                    + "faster from shore (and drift off vanilla), higher makes them build slower.",
                    new AcceptableValueRange<float>(2f, 40f)));
            configShoreDepthDarkness = config.Bind("07 - Shore & Waves", "04 DepthDarkness", 1f,
                new ConfigDescription("How dark/light deep water gets, relative to shallow. 1 = no change, below 1 = darker "
                    + "deeps, above 1 = lighter deeps. (Needs Enabled on so depth actually varies.)",
                    new AcceptableValueRange<float>(0.2f, 2f)));
            configShoreDepthForDark = config.Bind("07 - Shore & Waves", "05 DepthForFullDark", 8f,
                new ConfigDescription("Water depth (m) at which the darkening reaches full. Lower = water darkens faster as it deepens.",
                    new AcceptableValueRange<float>(1f, 40f)));
            configShoreFieldExtent = config.Bind("07 - Shore & Waves", "06 FieldRadius", 1024f,
                new ConfigDescription("Half-size (m) of the baked field around the player. Beyond it = deep ocean default. "
                    + "Larger covers more coastline but costs more to bake.", new AcceptableValueRange<float>(256f, 4000f)));
            configShoreFieldResolution = config.Bind("07 - Shore & Waves", "07 FieldResolution", 96,
                new ConfigDescription("Texels per side of the field. Higher = sharper shore/depth detail, more bake cost.",
                    new AcceptableValueRange<int>(32, 256)));
            configShoreFogDomainBlend = config.Bind("07 - Shore & Waves", "08 FogCoastlineBlend", 96f,
                new ConfigDescription("Coastline crossfade width (m) used by the volumetric fog's 'ConfineToDomains'. Land fog "
                    + "fades out over the first this-many metres of water, sea fog fades in over the same band. Wider = a softer, "
                    + "longer land↔sea blend at the shore.", new AcceptableValueRange<float>(16f, 400f)));

            // ===================================================================
            // 17 - FiresPlanet (the cloud dome rendered as a planet from space)
            // ===================================================================
            configFiresPlanetEnabled = config.Bind(
                "12 - FiresPlanet", "01 Enabled", true,
                "Render the cloud dome as a PLANET when seen from above (your world map exactly as M shows "
                + "it, sun-lit, atmosphere rim) and as the weather cloud layer when seen from the ground. "
                + "Off = the dome stays the vanilla cloud plane (FiresAtmosphere still rescales it).");

            configFiresPlanetOceanColor = config.Bind(
                "12 - FiresPlanet", "02 OceanColor", new UnityEngine.Color(0.04f, 0.18f, 0.42f, 1f),
                "Colour of the bare globe under the map. The map brings its own sea, so this shows only where "
                + "10 MapOpacity is below 1, and on no-map worlds, where the planet has no map at all.");

            configFiresPlanetSurfaceCloudOpacity = config.Bind(
                "12 - FiresPlanet", "03 SurfaceCloudOpacity", 0.55f,
                new ConfigDescription(
                    "How strongly the cloud layer veils the planet surface when viewed from space. "
                    + "0 = bare map, 1 = heavy cloud cover over the globe.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresPlanetCloudCoverage = config.Bind(
                "12 - FiresPlanet", "04 CloudCoverage", 0.5f,
                new ConfigDescription(
                    "OUTSIDE/planet cloud fill (the clouds on the globe seen from space). Independent of the "
                    + "ground sky — see 14 InsideCloudCoverage for that. Lower = more cloud, higher = clearer.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresPlanetCloudSoftness = config.Bind(
                "12 - FiresPlanet", "05 CloudSoftness", 0.6f,
                new ConfigDescription(
                    "OUTSIDE/planet cloud edge softness. Higher = wispier, feathered edges; lower = hard puffs.",
                    new AcceptableValueRange<float>(0.001f, 1f)));

            configFiresPlanetCloudScale = config.Bind(
                "12 - FiresPlanet", "07 CloudWorldScale", 5E-05f,
                new ConfigDescription(
                    "World-space tiling of the cloud texture. Smaller = larger, broader cloud masses.",
                    new AcceptableValueRange<float>(1E-05f, 0.003f)));

            configFiresPlanetCloudScrollSpeed = config.Bind(
                "12 - FiresPlanet", "08 CloudScrollSpeed", 1.0f,
                new ConfigDescription(
                    "Speed the clouds drift across the sky/planet (multiplies the vanilla wind-driven offset).",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresPlanetDomeRadius = config.Bind(
                "12 - FiresPlanet", "09 DomeRadius", 1878.473f,
                new ConfigDescription(
                    "Horizontal radius of the dome — how far the rim reaches out around you (the dome wraps "
                    + "down to the ground at this distance). Seen from above, the whole world map, out to the "
                    + "edge of the world, is fit to this radius. Bigger = the horizon wraps further out.",
                    new AcceptableValueRange<float>(500f, 12000f)));

            configFiresPlanetMapOpacity = config.Bind(
                "12 - FiresPlanet", "10 MapOpacity", 1f,
                new ConfigDescription(
                    "How strongly your world map shows on the planet. It is the map M shows: biomes, relief, "
                    + "what you have explored and the fog over the rest. 1 = the map as M shows it, 0 = bare "
                    + "ocean-blue globe.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresPlanetVibrance = config.Bind(
                "12 - FiresPlanet", "11 Vibrance", 1.0f,
                new ConfigDescription(
                    "Colour saturation of the planet surface. 1 = as-is, 0 = greyscale, >1 = punchier.",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresPlanetHue = config.Bind(
                "12 - FiresPlanet", "12 HueShift", 0f,
                new ConfigDescription(
                    "Rotate the hue of the planet surface (-0.5..0.5 = a full colour wheel either way).",
                    new AcceptableValueRange<float>(-1.0f, 1.0f)));

            configFiresPlanetHideWorld = config.Bind(
                "12 - FiresPlanet", "13 HideWorldInSpaceView", true,
                "When you rise above the dome (the planet view), cull the world (terrain + water) off the "
                + "camera so it clears to the star skybox around the planet, instead of vanilla terrain/water "
                + "poking out past the dome's edge. Restores instantly when you drop back below the dome.");

            // (INSIDE/ground clouds are the VANILLA weather cloud layer now — the
            //  dome only draws the planet from above, so there are no inside sliders.)

            configFiresPlanetDomeHeight = config.Bind(
                "12 - FiresPlanet", "17 DomeHeight", 925f,
                new ConfigDescription(
                    "Height of the dome's TOP above its ground-level rim. The rim wraps to the ground; this is "
                    + "how high it arcs overhead. Fly above this and you pop out into the planet/space view, so "
                    + "keep it above where you normally fly. Pairs with 09 DomeRadius for the overall shape.",
                    new AcceptableValueRange<float>(500f, 8000f)));

            configFiresPlanetTieWaterToDome = config.Bind(
                "12 - FiresPlanet", "18 TieWaterRingToDome", true,
                "Clamp the FiresWater ocean ring's radius to the dome radius (09 DomeRadius), so the water "
                + "and the dome end at the same edge and you never see ocean poking past the planet in space "
                + "view. Off = the ocean uses its own '06 - Extended Water Tiles / FollowRadius' instead.");

            // ===================================================================
            // (18..21 Orbitals removed — moved to the Galaxies mod)

            // ===================================================================
            // 05 - Ocean Ring (real-zone water extension around the player)
            // ===================================================================
            configOceanRingExtendEnabled = config.Bind(
                "04 - Ocean Ring", "01 OceanRingExtendEnabled", true,
                "When true, bump ZoneSystem.m_activeArea so the REAL vanilla zone water (the animated "
                + "FiresWater mesh right below you) stays loaded in a wider ring around the player — "
                + "storm waves no longer cut off at the vanilla boundary. The extra zones load water + "
                + "terrain ONLY: vegetation, locations, creature spawners and re-streamed ZDO objects "
                + "(bases/mobs) are suppressed by ZoneContentSuppressor, so the bump runs everywhere "
                + "(no deep-ocean gate) without waking land content. Client-only.");

            configOceanRingExtendedActiveArea = config.Bind(
                "04 - Ocean Ring", "02 OceanRingExtendedActiveArea",
                2,
                new ConfigDescription(
                    "ZoneSystem.m_activeArea value used for the extension. The extra zones beyond the "
                    + "vanilla radius load water + terrain only (content suppressed), so this is the "
                    + "'how far does the real animated water reach' knob. 1=vanilla (3x3 zones, ~96m), "
                    + "2=5x5 (~160m), 4=9x9 (~288m). Each step adds a zone ring so memory grows — a few "
                    + "is cheap, raise gradually and watch FPS on megabases.",
                    new AcceptableValueRange<int>(1, 16)));

            // ===================================================================
            // 06 - Far Water Disk (FiresWater fill past the real water)
            // ===================================================================
            configFiresWaterExtendedRingEnabled = config.Bind(
                "05 - Far Water Disk", "01 ExtendedRingEnabled", true,
                "Draw a single recentering FiresWater disk that fills the horizon PAST the real zone "
                + "water (section 05). It has a hole punched out over the loaded zones so it never "
                + "double-draws on top of the real animated mesh; the hole auto-tracks the Ocean Ring "
                + "radius. Render-only (no ZoneSystem registration, no ZDO, no Collider) so it doesn't "
                + "wake mobs/vegetation/builds, and it only draws while real zone water is rendering so "
                + "it can't flood under the player. Locked at sea level (y=30).");

            configFiresWaterFollowRadius = config.Bind(
                "05 - Far Water Disk", "02 FollowRadius",
                3000f,
                new ConfigDescription(
                    "Radius (m) of the far FiresWater disk around the player, used only when "
                    + "FiresPlanet TieWaterToDome is OFF (otherwise the disk radius follows the dome "
                    + "radius so no water pokes past the planet in space view). Default 3000m. Raise "
                    + "toward 6000 if you want water visible to a more distant horizon.",
                    new AcceptableValueRange<float>(64f, 12000f)));

            configRingWaveDetailDistance = config.Bind(
                "05 - Far Water Disk", "03 WaveDetailDistance", 1000f,
                new ConfigDescription(
                    "Range (m) over which the disk keeps FULL wave tessellation before it coarsens with "
                    + "distance. Higher = animated waves read further out (near/mid water looks alive "
                    + "instead of flat), at the cost of triangles. Watch the perf overlay's tri count.",
                    new AcceptableValueRange<float>(200f, 4000f)));

            configRingWaveDetailFloor = config.Bind(
                "05 - Far Water Disk", "04 WaveDetailFloor", 48f,
                new ConfigDescription(
                    "Minimum wave tessellation for the FAR disk (past WaveDetailDistance). Higher = the "
                    + "distant horizon still ripples instead of going dead-flat — but this applies across "
                    + "the whole multi-km disk, so raise it cautiously and watch the tri count.",
                    new AcceptableValueRange<float>(1f, 96f)));

            configRingSeamOverlap = config.Bind(
                "05 - Far Water Disk", "05 SeamOverlap", 1,
                new ConfigDescription(
                    "How many 64m cells the disk OVERLAPS the real zone water to close the seam. The disk "
                    + "shares zone water's stencil and draws after it, so zone water wins the overlap (one "
                    + "water layer, no z-fight) and the disk only fills the gap. 0 = abut exactly (old, "
                    + "gap-prone); 1 = recommended; 2+ if a seam still slips through.",
                    new AcceptableValueRange<int>(0, 4)));

            // Distant-water look — only the far disk (so near water is untouched). The
            // flat far disk mirrors the grey sky; these dial it back to the near hue.
            configDistantReflection = config.Bind("05 - Far Water Disk", "06 DistantReflection", 0.5f,
                new ConfigDescription("Sky reflection on the FAR water only. 1 = unchanged (mirror-like, grey), lower = less sky, "
                    + "more of the water's own colour — drop toward ~0.3 so the distance matches the near water.",
                    new AcceptableValueRange<float>(0f, 1f)));
            configDistantTint = config.Bind("05 - Far Water Disk", "07 DistantTint", UnityEngine.Color.white,
                "Colour multiply on the FAR water only — nudge it toward your near water's hue (green-teal) so the two match. White = unchanged.");
            configDistantBrightness = config.Bind("05 - Far Water Disk", "08 DistantBrightness", 1f,
                new ConfigDescription("Brightness of the FAR water only. Lower if the distance reads too bright/washed vs the near water.",
                    new AcceptableValueRange<float>(0.2f, 2f)));

            // Clipmap ocean (09-12) — Crest-style cascade rings as a drop-in
            // ALTERNATIVE to the disk above. One toggle, never both systems:
            // when 09 is on, 01 ExtendedRingEnabled is stood down in
            // ApplyFiresWaterConfig so the two can't double-draw the horizon.
            configUseClipmapOcean = config.Bind(
                "05 - Far Water Disk", "09 UseClipmapOcean", true,
                "DEFAULT far-water system (ON): a Crest-style clipmap ocean — a "
                + "high-density center square wrapped in concentric LOD rings, each ring 2x the world "
                + "size of the previous at the same vertex count, recentered on the camera with "
                + "grid snapping at the coarsest ring's cell size so vertices never swim. Wears the "
                + "same captured FiresWater material as the disk (waves/foam/colors identical to the "
                + "near water) and defers to real zone water the same way. When ON, the disk "
                + "(01 ExtendedRingEnabled) is disabled automatically and restored when toggled back "
                + "OFF — hot-toggleable, no restart. Sliders 03/04 tune both systems. Client-only.");

            configClipmapOceanRadius = config.Bind(
                "05 - Far Water Disk", "10 ClipmapRadius", 4000f,
                new ConfigDescription(
                    "Half-size (m) of the clipmap's OUTERMOST ring — total ocean coverage when "
                    + "09 UseClipmapOcean is on. The last vert row is additionally pushed far past the "
                    + "fog dome (a horizon skirt), so the square edge itself is never visible. Default "
                    + "4000m ~ the disk-era far coverage.",
                    new AcceptableValueRange<float>(512f, 16000f)));

            configClipmapOceanLods = config.Bind(
                "05 - Far Water Disk", "11 ClipmapLodCount", 6,
                new ConfigDescription(
                    "Number of clipmap LOD rings. Each ring doubles the world size of the previous at "
                    + "the same vertex count, so at a fixed 10 ClipmapRadius every +1 LOD HALVES the "
                    + "finest cell size at the player. 6 LODs at 4000m/res 32 = ~2m cells near you "
                    + "(matching the real zone-water mesh), 62.5m cells at the horizon.",
                    new AcceptableValueRange<int>(2, 8)));

            configClipmapOceanResolution = config.Bind(
                "05 - Far Water Disk", "12 ClipmapPatchResolution", 40,
                new ConfigDescription(
                    "Vertex density per clipmap patch side (Crest's vertDensity). Every ring shares "
                    + "this — the shared density is exactly what makes ring seams line up. Higher = "
                    + "finer wave sampling at every distance for quadratically more triangles; watch "
                    + "the perf overlay before raising past 64.",
                    new AcceptableValueRange<int>(8, 128)));

            configClipmapOceanInnerHole = config.Bind(
                "05 - Far Water Disk", "13 ClipmapInnerHole", 64f,
                new ConfigDescription(
                    "Radius (m) the clipmap leaves EMPTY around you — loaded-zone water owns the near "
                    + "field, and without this hole the dense inner rings overlap it: a second full-"
                    + "amplitude surface pokes through the shore-attenuated zone waves (double shoreline) "
                    + "and burns millions of triangles. QUANTIZED DOWN to ring footprints (with default "
                    + "radius/LODs the steps are 125m, 250m, ...) so the hole can never overshoot the "
                    + "zone-water coverage — an oversized hole shows a dry stripe of naked seabed. Values "
                    + "below the smallest ring footprint mean no hole (solid centre).",
                    new AcceptableValueRange<float>(0f, 512f)));

            // ===================================================================
            // 07 - Vanilla LOD Plane (vanilla far-ocean WaterPlane handling)
            // ===================================================================
            configHideVanillaLodWater = config.Bind(
                "06 - Vanilla LOD Plane", "01 HideVanillaLodWater", true,
                "When true (default), hide vanilla's WaterPlane (water_lod) GameObject entirely. "
                + "This is the flat follower disc that vanilla draws under the player; with our real-"
                + "zone extension + far FiresWater disk it's redundant and its grazing-angle reflection "
                + "shows as a bright ring on the horizon. Hiding it leaves only our water (real zone "
                + "meshes + far disk). Flip OFF to get vanilla's follower disc back. Client-only.");

            // ===================================================================
            // 08 - FiresSky (procedural skybox property overrides + day-sky)
            // Moved here from the old "FiresWater" section — these are skybox
            // controls, not water controls.
            // ===================================================================
            configFiresSkyboxOverride = config.Bind(
                "07 - FiresSky", "01 SkyboxOverride", true,
                "When true (default), the skybox-override sliders below are applied to the active "
                + "skybox material every frame. Multipliers default to 1.0 = vanilla appearance, so "
                + "this can stay on without changing anything until you move a slider.");

            configFiresSkyboxAtmosphereThicknessMul = config.Bind(
                "07 - FiresSky", "02 AtmosphereThicknessMul",
                1.0f,
                new ConfigDescription(
                    "Multiplier applied to the captured original _AtmosphereThickness when SkyboxOverride "
                    + "is true. 1.0 = unchanged. 0.0 = no atmospheric scattering (sharp horizon line). "
                    + "> 1.0 = thicker atmosphere (heavier horizon haze).",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresSkyboxFogHeightMinOffset = config.Bind(
                "07 - FiresSky", "03 FogHeightMinOffset",
                0f,
                new ConfigDescription(
                    "World-meters offset added to the captured _FogHeightMin. Push positive to move the "
                    + "bottom of the height-fog band higher (e.g. above sea level Y=30 so the band stops "
                    + "mixing with horizon waves).",
                    new AcceptableValueRange<float>(-500f, 500f)));

            configFiresSkyboxFogHeightMaxOffset = config.Bind(
                "07 - FiresSky", "04 FogHeightMaxOffset",
                0f,
                new ConfigDescription(
                    "World-meters offset added to the captured _FogHeightMax. Pair with FogHeightMinOffset "
                    + "to translate the entire fog band up or down without changing its width.",
                    new AcceptableValueRange<float>(-500f, 500f)));

            configFiresSkyboxGroundColorBlendToSky = config.Bind(
                "07 - FiresSky", "05 GroundColorBlendToSky",
                0f,
                new ConfigDescription(
                    "Lerp factor from captured _GroundColor toward captured _SkyTint. 0.0 = unchanged "
                    + "(vanilla colors, visible horizon-color discontinuity). 1.0 = ground color exactly "
                    + "matches sky tint (kills the discontinuity entirely).",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresSkyboxExposureMul = config.Bind(
                "07 - FiresSky", "06 ExposureMultiplier", 1.0f,
                new ConfigDescription("Multiplier on the procedural skybox _Exposure. <1 dims the sky; >1 brightens. 1.0 = vanilla.",
                    new AcceptableValueRange<float>(0f, 4f)));
            configFiresSkyboxStormDarken = config.Bind(
                "07 - FiresSky", "06a StormSkyDarken", 0.6f,
                new ConfigDescription("How much the SKY itself dims in a full storm — drops the skybox exposure by the weather's "
                    + "rain-cloud amount so a downpour/thunderstorm reads as a dim overcast (EnvMan already dims the scene light, but "
                    + "nothing was dimming the bright sky). 0 = sky never darkens, 0.6 = down to 40% brightness at full storm, 1 = goes black.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configFiresSkyboxStarExposureMul = config.Bind(
                "07 - FiresSky", "07 StarExposureMultiplier", 1.0f,
                new ConfigDescription("Multiplier on _StarExposure. Brighten/dim stars at night without changing time-of-day fade.",
                    new AcceptableValueRange<float>(0f, 5f)));

            configFiresSkyboxSunSizeMul = config.Bind(
                "07 - FiresSky", "08 SunSizeMultiplier", 1.0f,
                new ConfigDescription("Multiplier on _SunSize — visual sun disk SIZE in the sky.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            configFiresSkyboxSunDiskBrightness = config.Bind(
                "07 - FiresSky", "08b SunDiskBrightness", 1.0f,
                new ConfigDescription(
                    "VISUAL brightness of the sun DISK in the sky — does NOT touch the sunlight/shadows. The "
                    + "daytime atmospheric scattering was washing the disk out so it was barely visible above "
                    + "water; >1 makes it punch through. 1 = old (faint), 2.5 = clearly visible, higher = "
                    + "brighter. Pair with SunSizeMultiplier (08) for a bigger disk.",
                    new AcceptableValueRange<float>(0f, 16f)));

            configFiresSkyboxMoonSizeMul = config.Bind(
                "07 - FiresSky", "09 MoonSizeMultiplier", 1.0f,
                new ConfigDescription("Multiplier on _MoonSize — visual moon disk size.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            configFiresSkyboxSunLightMul = config.Bind(
                "07 - FiresSky", "10 SunLightMultiplier", 1.0f,
                new ConfigDescription("Multiplier on the SUN's directional Light.intensity (daytime). Affects shadows, ambient, water reflections.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configFiresSkyboxMoonLightMul = config.Bind(
                "07 - FiresSky", "11 MoonLightMultiplier", 1.0f,
                new ConfigDescription("Multiplier on the MOON's directional Light.intensity (nighttime). Use to brighten/dim moonlit scenes.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configDaySkyHue = config.Bind(
                "07 - FiresSky", "12 DaySkyHue", 0f,
                new ConfigDescription("Fine R/B hue trim on the daytime sky (on top of DaySkyColor). "
                    + "Negative = cooler/bluer, positive = warmer/redder. 0 = none.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            configDaySkyDensity = config.Bind(
                "07 - FiresSky", "13 DaySkyDensity", 1.0f,
                new ConfigDescription("Daytime sky density / haze. >1 adds a UNIFORM tinted fill across the "
                    + "whole dome (drowns stars evenly WITHOUT blowing out the bright horizon); <1 dims the "
                    + "whole sky. 1 = unchanged.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configStarFadeBySky = config.Bind(
                "07 - FiresSky", "14 StarFadeBySky", 3.5f,
                new ConfigDescription("How aggressively bright daytime sky masks the starfield. "
                    + "0 = stars ALWAYS visible, 3.5 = vanilla-like, 20 = even faint sky fully wipes them.",
                    new AcceptableValueRange<float>(0f, 20f)));

            configDaySkyColor = config.Bind(
                "07 - FiresSky", "15 DaySkyColor", new UnityEngine.Color(1f, 1f, 1f, 1f),
                "Full-colour tint multiplied onto the daytime sky (HDR — channels can exceed 1 to brighten). "
                + "White = unchanged. This is the main 'paint the day sky any colour' control.");

            // ===================================================================
            // 10a - Sun Haze (weather/elevation curve over the sun disk's bloom)
            // ===================================================================
            configSunHazeEnabled = config.Bind(
                "10a - Sun Haze", "01 Enabled", true,
                "Gate the sunward glare on WEATHER and SUN HEIGHT instead of leaving it on all day. "
                + "Vanilla's SunShafts keys off bright sky pixels near the sun, so the disk size/brightness "
                + "below decide how much the screen washes out when you look at the sun. OFF = the disk uses "
                + "the flat '07 - FiresSky' multipliers at all times. Shafts cast by trees and buildings are "
                + "vanilla's own occlusion and are never affected by this section.");

            configSunHazeClearDiskBoost = config.Bind(
                "10a - Sun Haze", "02 ClearDiskBoost", 1.0f,
                new ConfigDescription("Sun-disk brightness multiplier on a clear day with a high sun. 1 = vanilla.",
                    new AcceptableValueRange<float>(0.25f, 8f)));

            configSunHazeHazyDiskBoost = config.Bind(
                "10a - Sun Haze", "03 HazyDiskBoost", 4.0f,
                new ConfigDescription("Sun-disk brightness multiplier at full haze (heavy mist/rain, or a low sun). "
                    + "This is where the glare lives — raise for drama in swamps and at dawn.",
                    new AcceptableValueRange<float>(0.25f, 16f)));

            configSunHazeClearSunSize = config.Bind(
                "10a - Sun Haze", "04 ClearSunSize", 1.0f,
                new ConfigDescription("Sun-disk SIZE multiplier on a clear day with a high sun. 1 = vanilla (_SunSize 0.04).",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            configSunHazeHazySunSize = config.Bind(
                "10a - Sun Haze", "05 HazySunSize", 1.8f,
                new ConfigDescription("Sun-disk SIZE multiplier at full haze. A wider disk feeds vanilla's SunShafts "
                    + "more screen area, which is what turns the glare into a soft bloom rather than a hard dot.",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            configSunHazeLowSunElevation = config.Bind(
                "10a - Sun Haze", "06 LowSunElevation", 0.35f,
                new ConfigDescription("Sun height (sin of elevation; 0 = horizon, 1 = zenith) at which the low-sun "
                    + "haze has fully faded out. 0.35 keeps it to roughly the first and last couple of hours.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            configSunHazeLowSunWeight = config.Bind(
                "10a - Sun Haze", "07 LowSunWeight", 1.0f,
                new ConfigDescription("How much a LOW SUN alone drives the haze. 0 = dawn/dusk get no extra glare "
                    + "and only weather does.",
                    new AcceptableValueRange<float>(0f, 1f)));

            configSunHazeWeatherWeight = config.Bind(
                "10a - Sun Haze", "08 WeatherWeight", 1.0f,
                new ConfigDescription("How much FOG AND RAIN drive the haze, from the environment's own blended fog "
                    + "density plus its rain amount. 0 = weather gets no extra glare and only a low sun does.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ===================================================================
            // 09 - FiresAtmosphere (vanilla CloudCylinder + cloud_plane rescale)
            // ===================================================================
            configFiresAtmosphereEnabled = config.Bind(
                "11 - FiresAtmosphere", "01 Enabled", true,
                "MASTER ON/OFF for the atmosphere layer: rescales the vanilla CloudCylinder + cloud planes "
                + "to wrap the play area (cylinder ~3000m). Turning this OFF now reverts them to vanilla size "
                + "and shaders LIVE (was previously startup-only). NOTE: the vanilla cylinder runs a GrabPass-"
                + "refraction shader; if you're hunting a colored haze/band over distant terrain, toggle this "
                + "OFF first to rule the cylinder in or out.");

            configFiresAtmosphereCylinderRadius = config.Bind(
                "11 - FiresAtmosphere", "02 CylinderRadius", 2000f,
                new ConfigDescription("Atmosphere bubble radius — how far out the swirling cloud "
                    + "cylinder wraps. Should sit just OUTSIDE the visible play volume. "
                    + "3000m matches moon orbit altitude so the bubble looks like a finite "
                    + "atmosphere from space.",
                    new AcceptableValueRange<float>(1000f, 12000f)));

            configFiresAtmosphereCylinderHeight = config.Bind(
                "11 - FiresAtmosphere", "03 CylinderHeight", 1020.833f,
                new ConfigDescription("Atmosphere bubble height. 3000m default matches moon "
                    + "altitude so the cylinder top reaches the moon's orbital plane.",
                    new AcceptableValueRange<float>(500f, 8000f)));

            configFiresAtmosphereCloudPlaneRadius = config.Bind(
                "11 - FiresAtmosphere", "04 CloudPlaneRadius", 3000f,
                new ConfigDescription("Cloud ceiling radius — matches CylinderRadius by default so "
                    + "the dome cap aligns with the atmosphere walls.",
                    new AcceptableValueRange<float>(1000f, 12000f)));

            configFiresAtmosphereCloudCoverage = config.Bind(
                "11 - FiresAtmosphere", "05 CloudCoverage", 1f,
                new ConfigDescription("How much of the sky the clouds cover (Custom/FiresClouds). Higher = "
                    + "more overcast; lower = scattered/clear.", new AcceptableValueRange<float>(0f, 1f)));

            configFiresAtmosphereCloudWorldScale = config.Bind(
                "11 - FiresAtmosphere", "06 CloudWorldScale", 5E-05f,
                new ConfigDescription("Cloud cell size. LOWER = bigger, fewer clouds; higher = smaller, busier.",
                    new AcceptableValueRange<float>(1E-05f, 0.0015f)));

            configFiresAtmosphereCloudSoftness = config.Bind(
                "11 - FiresAtmosphere", "07 CloudSoftness", 0.6f,
                new ConfigDescription("Cloud edge feather. Higher = wispier/softer edges; lower = hard-edged.",
                    new AcceptableValueRange<float>(0.01f, 1f)));

            configFiresAtmosphereCloudOpacity = config.Bind(
                "11 - FiresAtmosphere", "08 CloudOpacity", 0.167f,
                new ConfigDescription("Cloud opacity. 1 = normal; lower = thin/translucent.",
                    new AcceptableValueRange<float>(0f, 2f)));

            // Storm reactivity for the overhead (ground-view) dome clouds — driven by
            // the env's rain-cloud amount so a thunderstorm reads dark + overcast.
            configStormDarken = config.Bind(
                "11 - FiresAtmosphere", "08a StormDarken", 0.35f,
                new ConfigDescription("Cloud brightness at full storm. 1 = no darkening, lower = darker/more ominous overcast "
                    + "(0.35 ≈ moody storm). Drops in as the weather's rain-cloud amount rises.", new AcceptableValueRange<float>(0.1f, 1f)));
            configStormCoverage = config.Bind(
                "11 - FiresAtmosphere", "08b StormCoverage", 0.15f,
                new ConfigDescription("How much the cloud cover thickens in a storm — LOWER = heavier overcast (more of the sky "
                    + "covered). 1 = same as clear weather.", new AcceptableValueRange<float>(0.05f, 1f)));
            configStormOpacity = config.Bind(
                "11 - FiresAtmosphere", "08c StormOpacity", 1.6f,
                new ConfigDescription("Cloud opacity multiplier at full storm — higher = a more solid, light-blocking sky so the "
                    + "bright sky behind stops showing through.", new AcceptableValueRange<float>(1f, 4f)));

            configFiresAtmosphereCloudDetailStrength = config.Bind(
                "11 - FiresAtmosphere", "09 CloudDetailStrength", 0.5f,
                new ConfigDescription("How much the second (detail) cloud texture breaks up the main shape. "
                    + "0 = main only.", new AcceptableValueRange<float>(0f, 1f)));

            configFiresAtmosphereCloudScrollSpeed = config.Bind(
                "11 - FiresAtmosphere", "10 CloudScrollSpeed", 1.0f,
                new ConfigDescription("How fast clouds drift with the wind. 1 = default; 0 = static.",
                    new AcceptableValueRange<float>(0f, 4f)));

            configFiresAtmosphereCloudBrightness = config.Bind(
                "11 - FiresAtmosphere", "11 CloudBrightness", 1.0f,
                new ConfigDescription("Cloud brightness multiplier (on top of the day/night sun+ambient light).",
                    new AcceptableValueRange<float>(0f, 2f)));

            configFiresAtmosphereCloudAltFadeStart = config.Bind(
                "11 - FiresAtmosphere", "12 CloudAltFadeStart", 1500f,
                new ConfigDescription("Altitude (Y) where clouds START fading out as you fly up.",
                    new AcceptableValueRange<float>(200f, 6000f)));

            configFiresAtmosphereCloudAltFadeEnd = config.Bind(
                "11 - FiresAtmosphere", "13 CloudAltFadeEnd", 2600f,
                new ConfigDescription("Altitude (Y) where clouds are FULLY gone (kills the box around the "
                    + "moon). Keep above CloudAltFadeStart.", new AcceptableValueRange<float>(400f, 8000f)));

            configFiresAtmosphereCloudDensity = config.Bind(
                "11 - FiresAtmosphere", "14 DomeCloudDensity", 3.0f,
                new ConfigDescription(
                    "Dome cloud density boost (vanilla _Opacity equivalent). Higher = a faint PNG reads as "
                    + "solid cloud; lower = thinner/wispier.",
                    new AcceptableValueRange<float>(1f, 16f)));

            configFiresAtmosphereCloudHue = config.Bind(
                "11 - FiresAtmosphere", "15 DomeCloudHue", 0f,
                new ConfigDescription(
                    "Hue shift of the dome clouds (-0.5..0.5 = full colour wheel). 0 = the PNG's own colour.",
                    new AcceptableValueRange<float>(-1.0f, 1.0f)));

            // ===================================================================
            // 11 - Aurora Skybox (primary path — Custom/FiresSkyboxAurora shader)
            // ===================================================================
            configAuroraOverlayEnabled = config.Bind(
                "13 - Aurora Skybox", "01 OverlayEnabled", true,
                "INDEPENDENT MASTER ON/OFF for the SKY AURORA. (Despite the legacy 'Overlay' name it's a "
                + "proper skybox SHADER REPLACEMENT — Custom/FiresSkyboxAurora swapped onto "
                + "RenderSettings.skybox.shader, rendered intrinsically in the skybox fragment; no camera "
                + "overlay or CommandBuffer.) NOW DECOUPLED from Aurora Glow (12): turning Glow off no longer "
                + "turns the sky aurora off, so you can isolate each. Default ON.");

            configAuroraOverlayIntensity = config.Bind(
                "13 - Aurora Skybox", "02 OverlayIntensity",
                0.5f,
                new ConfigDescription(
                    "Brightness of the SKY AURORA (procedural skybox shader). This slider ALONE drives it "
                    + "now — no longer multiplied by Aurora Glow. Real aurora is moderately bright (not "
                    + "blinding) — default 0.5 matches naked-eye aurora; 1.0+ is photograph-style brightness.",
                    new AcceptableValueRange<float>(0f, 5f)));

            configAuroraNorthBiasPower = config.Bind(
                "13 - Aurora Skybox", "03 NorthBiasPower",
                2.5f,
                new ConfigDescription(
                    "How sharply aurora concentrates toward the north sky. The real auroral oval "
                    + "spans a ~15-20° arc across the polar sky, so a moderate bias is more "
                    + "realistic than a tight cap. 0.5 = visible nearly everywhere (mild), "
                    + "1.0 = balanced N/EW spread, 2.5 = wide-arc auroral-oval-like default, "
                    + "6.0 = tight north cap only.",
                    new AcceptableValueRange<float>(0.25f, 6f)));

            configAuroraContribCap = config.Bind(
                "13 - Aurora Skybox", "04 ContribCap",
                0.35f,
                new ConfigDescription(
                    "Maximum per-channel brightness the aurora can add to the underlying sky. "
                    + "Real aurora is vivid without obliterating stars — 0.35 lets curtains show "
                    + "as bright color but keeps stars visible through them. 0.1 = subtle haze, "
                    + "0.5 = bright dominant aurora, 1.0+ = aurora replaces sky in lit regions.",
                    new AcceptableValueRange<float>(0.05f, 1.5f)));

            configAuroraAltMin = config.Bind(
                "13 - Aurora Skybox", "05 AltMin",
                0.0f,
                new ConfigDescription(
                    "Altitude (-1..+1, where 0 is horizon and 1 is zenith) at which aurora STARTS "
                    + "rendering. Negative = aurora extends below the visible horizon line; 0 = "
                    + "starts exactly at horizon; positive = floats above the horizon with a gap.",
                    new AcceptableValueRange<float>(-0.2f, 0.5f)));

            configAuroraAltMax = config.Bind(
                "13 - Aurora Skybox", "06 AltMax",
                0.85f,
                new ConfigDescription(
                    "Altitude (0..1) at which aurora FADES to nothing as it approaches the zenith. "
                    + "0.5 = aurora only in lower-mid sky, 0.85 = covers most of north hemisphere, "
                    + "1.0 = aurora reaches straight overhead.",
                    new AcceptableValueRange<float>(0.2f, 1.0f)));

            configAuroraColumnFreq = config.Bind(
                "13 - Aurora Skybox", "07 ColumnFreq",
                10f,
                new ConfigDescription(
                    "How many ribbon columns wrap around the dome. Real aurora curtains have "
                    + "relatively wide spacing — 10 columns / full circle ≈ 36° per ribbon, "
                    + "matching observed curtain widths. Higher = denser thinner streaks, "
                    + "lower = fewer thicker bands.",
                    new AcceptableValueRange<float>(2f, 64f)));

            configAuroraVertFreq = config.Bind(
                "13 - Aurora Skybox", "08 VertFreq",
                2.0f,
                new ConfigDescription(
                    "Vertical noise frequency — controls how much each column varies along its "
                    + "length. Real auroral curtains fold and pleat several times along their "
                    + "height. 2.0 ≈ a couple of folds per ribbon, matching real curtain structure.",
                    new AcceptableValueRange<float>(0.2f, 8f)));

            configAuroraSharpness = config.Bind(
                "13 - Aurora Skybox", "09 Sharpness",
                0.0f,
                new ConfigDescription(
                    "How sharp the ribbon edges are. Real aurora ribbons have DIFFUSE soft edges "
                    + "(curtains of glowing gas, not solid sheets), so 0.0 is the realistic "
                    + "default. 0.5 = visible bands with soft edges, 0.8 = hard sharp streaks.",
                    new AcceptableValueRange<float>(0f, 0.8f)));

            configAuroraScrollSpeed = config.Bind(
                "13 - Aurora Skybox", "10 ScrollSpeed",
                0.012f,
                new ConfigDescription(
                    "How fast the ribbons + colors drift along the auroral arc. Real auroral "
                    + "curtains drift slowly (tens of seconds per noticeable motion). 0.012 = "
                    + "slow naturalistic drift, 0.04 = brisk motion, 0.2 = fast horizontal sweep.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            configAuroraPulseSpeed = config.Bind(
                "13 - Aurora Skybox", "11 PulseSpeed",
                0.073f,
                new ConfigDescription(
                    "Brightness pulsation rate — colors pulse with sin(t × PulseSpeed). Real "
                    + "pulsating aurora cycles every ~10-30 seconds. 0.20 = ~30s natural breath, "
                    + "0.35 = ~18s cycle, 1.0 = visible flicker, 3+ = strobing.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraNightOnly = config.Bind(
                "13 - Aurora Skybox", "12 NightOnly", true,
                "When true, aurora (skybox + glow + curtains) only shows at night (EnvMan.IsNight). "
                + "When false, it shows whenever enabled regardless of time-of-day.");

            configAuroraDuringStorms = config.Bind(
                "13 - Aurora Skybox", "13 DuringStorms", true,
                "When true, aurora renders even during rain/thunder/storm envs. When false (default), "
                + "it hides during storms because it doesn't read well through heavy precipitation.");

            // ===================================================================
            // 12 - Aurora Glow (LEGACY horizon-glow backdrop; also feeds skybox)
            // ===================================================================
            configAuroraGlowEnabled = config.Bind(
                "14 - Aurora Glow", "01 GlowEnabled", true,
                "INDEPENDENT ON/OFF for the HORIZON GLOW only (FiresSkyboxDriver): animates the procedural "
                + "skybox's _GroundColor through a green→cyan→purple gradient and clamps _AtmosphereThickness "
                + "low so the colored hemisphere bleeds up at the horizon. Decoupled from the sky aurora (11) "
                + "— this no longer gates it. HAZE SUSPECT: it writes the skybox ground color + fog height, so "
                + "toggle OFF to rule the horizon glow out of a colored tint. Night-only via Aurora Skybox/NightOnly.");

            configAuroraGlowIntensity = config.Bind(
                "14 - Aurora Glow", "02 GlowIntensity",
                0.729f,
                new ConfigDescription(
                    "Brightness of the HORIZON GLOW only. Independent of the sky aurora (11) now. "
                    + "0 = off, 1 = default, higher = more saturated.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraGlowSpeed = config.Bind(
                "14 - Aurora Glow", "03 GlowSpeed",
                0.145f,
                new ConfigDescription(
                    "Color-cycle speed for the glow gradient. Lower = slower morphing. 0.05 ≈ one "
                    + "full cycle every ~80 seconds.",
                    new AcceptableValueRange<float>(0.005f, 0.5f)));

            configAuroraGlowAtmosphereMul = config.Bind(
                "14 - Aurora Glow", "04 GlowAtmosphereMultiplier",
                0.701f,
                new ConfigDescription(
                    "Multiplier on _AtmosphereThickness while glow is active. Lower = thinner "
                    + "atmosphere = more aurora bleed-through. 0.25 ≈ vanilla×0.25 (sharp glow); "
                    + "1.0 = vanilla atmosphere (no extra bleed).",
                    new AcceptableValueRange<float>(0f, 1f)));

            configAuroraGlowFogHeightMin = config.Bind(
                "14 - Aurora Glow", "05 GlowFogHeightMin",
                -212.257f,
                new ConfigDescription(
                    "While glow is active, force the procedural-skybox _FogHeightMin to this Y "
                    + "value (typically below sea level Y=30) so the height-fog band stops masking "
                    + "the colored ground hemisphere at the horizon. Drag lower if the glow still "
                    + "doesn't bleed up to the horizon line.",
                    new AcceptableValueRange<float>(-500f, 30f)));

            // ===================================================================
            // 14 - Aurora Light (real directional Light for water reflections)
            // ===================================================================
            configAuroraLightEnabled = config.Bind(
                "15 - Aurora Light", "01 LightEnabled", true,
                "Spawn a real Unity directional Light at the aurora's position. Gives water specular reflections + "
                + "subtle ambient rim light + makes bloom/shaft systems treat aurora as a real light source.");

            configAuroraLightIntensity = config.Bind(
                "15 - Aurora Light", "02 LightIntensity", 0.5f,
                new ConfigDescription("Brightness of the aurora directional light. 0 = off, 0.5 = subtle rim, 1.0+ = pronounced aurora-tinted scene.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraLightElevation = config.Bind(
                "15 - Aurora Light", "03 LightElevationDeg", 60f,
                new ConfigDescription("Elevation angle of the aurora light source (degrees above horizon).",
                    new AcceptableValueRange<float>(0f, 90f)));

            configAuroraLightAzimuth = config.Bind(
                "15 - Aurora Light", "04 LightAzimuthDeg", 0f,
                new ConfigDescription("Azimuth bearing of the aurora light source. 0 = north (+Z), 90 = east, 180 = south.",
                    new AcceptableValueRange<float>(0f, 360f)));

            // ===================================================================
            // 15 - Aurora Shafts (second SunShafts pass at the aurora)
            // ===================================================================
            configAuroraShaftsEnabled = config.Bind(
                "16 - Aurora Shafts", "01 ShaftsEnabled", true,
                "Add a SECOND SunShafts post-process pass pointing at the aurora light. God rays radiate from the aurora's "
                + "screen-space position alongside the sun/moon's normal shafts. Off = vanilla SunShafts only.");

            configAuroraShaftsIntensity = config.Bind(
                "16 - Aurora Shafts", "02 ShaftsIntensity", 1.0f,
                new ConfigDescription("Brightness of the aurora shaft pass. 0 = no shafts, 1 = match vanilla SunShafts intensity, 2+ = dramatic.",
                    new AcceptableValueRange<float>(0f, 3f)));

            configAuroraShaftsBlurRadius = config.Bind(
                "16 - Aurora Shafts", "03 ShaftsBlurRadius", 2.5f,
                new ConfigDescription("Radial blur radius for the aurora shaft pass. Larger = longer streaks. Vanilla default 2.5.",
                    new AcceptableValueRange<float>(0.5f, 8f)));

            configAuroraShaftsMaxRadius = config.Bind(
                "16 - Aurora Shafts", "04 ShaftsMaxRadius", 0.75f,
                new ConfigDescription("Max screen-space radius the aurora shafts cover (0..1). Larger = shafts visible further from the aurora's screen origin.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            MigrateRenamedSections(config, true);   // 0.3.158: the renumbered sections keep their values
        }

        // Wired via configSync.lockedConfigChanged += Instance.OnConfigLockChanged.
        // ConfigSync's lockedConfigChanged is an Action (no payload) — fires whenever
        // the server's lock state changes; the locked vs unlocked decision lives on
        // ConfigSync.IsLocked / IsSourceOfTruth at the moment of inspection.
        public void OnConfigLockChanged()
        {
            ConfigLockChanged?.Invoke();
        }

        public void Dispose()
        {
            // Reserved for future FileSystemWatcher / async resources.
        }

        // True when two colours match within a small epsilon — used to detect a config value
        // still sitting at a known OLD default so it can be migrated without stomping real tuning.
        private static bool SameColor(UnityEngine.Color a, UnityEngine.Color b)
        {
            return UnityEngine.Mathf.Abs(a.r - b.r) < 0.02f
                && UnityEngine.Mathf.Abs(a.g - b.g) < 0.02f
                && UnityEngine.Mathf.Abs(a.b - b.b) < 0.02f;
        }
    }
}
