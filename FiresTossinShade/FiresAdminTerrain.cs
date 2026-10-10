using BepInEx;
using FiresCore.Sync;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Managers;

namespace VerdantsAscent
{
    // Standalone Fires-* mod hosting CustomTerrain + CustomWater. Clutter is
    // delegated to FAP (source-linked) - see csproj for the linked files.
    //
    // Companion / NPC / Quest / Guild / Discord / Wayshrine / Territory init
    // intentionally absent: this mod is admin terrain + water tools only.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Soft dependencies - these mods are not required for FAT to load, but
    // we integrate with them when present. Declaring them gives r2modman /
    // Thunderstore proper "depends on" metadata AND lets BepInEx order our
    // Awake AFTER theirs, so reflective lookups (AdminCheck, IH ruler
    // patches, WEC undo bindings) hit fully-initialized targets.
    [BepInDependency("Azumatt.InfinityHammer", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serversync", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.worldeditcommands", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("org.bepinex.plugins.serverdevcommands", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(FiresCore.FiresUnifiedCore.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    public class FiresTossinShade : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresTossinShade";
        public const string PluginName = "FiresTossinShade";
        public const string PluginVersion = "0.3.161";

        public static readonly Harmony harmony = new Harmony(PluginGUID);
        public static FiresTossinShade Instance { get; private set; }

        public static GameObject RootObject;
        public static GameObject PrefabContainer { get; private set; }

        public ConfigSync configSync;
        private static ILogHandler _originalLogHandler;

        // BepInEx log source - banner emitters route through this (not Debug.Log)
        // so banner lines don't stdout-echo a raw white console duplicate.
        public static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // BIG sun-over-waves banner - fires FIRST so it sits at
            // the top of the console output as the load announcement.
            // The compact banner fires once ZNetScene is up to bookend
            // the load with a "ready" confirmation.
            try { FiresTossinShadeBanner.PrintBig(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] FiresTossinShadeBanner.PrintBig failed: {ex.Message}");
            }

            Debug.Log($"[{PluginName}] Awake() entered - version {PluginVersion}");
            Instance = this;

            InstallLogFilter();

            RootObject = new GameObject("_FiresAdminTerrainRoot");
            UnityEngine.Object.DontDestroyOnLoad(RootObject);

            if (!Application.isBatchMode)
                AttachRenderDrivers();

            PrefabContainer = new GameObject("FiresAdminTerrain_Prefabs");
            PrefabContainer.transform.parent = RootObject.transform;
            PrefabContainer.SetActive(false);

            ConfigManager.Instance.Initialize(Config);

            // Register our config with the Fires config window (Core; F1). Shows our grouped layout
            // first, with a button to fall back to the default config manager.
            FiresCore.UI.FiresConfigUI.Register(PluginName, Config);

            configSync = new ConfigSync(PluginGUID)
            {
                DisplayName = PluginName,
                CurrentVersion = PluginVersion,
                MinimumRequiredVersion = PluginVersion
            };
            configSync.lockedConfigChanged += ConfigManager.Instance.OnConfigLockChanged;
            configSync.AddLockingConfigEntry(ConfigManager.Instance.configServerAuthority);
            configSync.AddConfigEntry(ConfigManager.Instance.configVerboseLogging);
            configSync.AddConfigEntry(ConfigManager.Instance.configServerPreset);
            configSync.AddConfigEntry(ConfigManager.Instance.configAdminOnlyTuning);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterEnabled);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterSwapVanilla);
            configSync.AddConfigEntry(ConfigManager.Instance.configFiresWaterUnderwaterEnv);
            // SkyboxOverride is intentionally NOT added to configSync - it's a
            // client-only diagnostic toggle. Each player should be able to
            // A/B-test the horizon haze on their own machine without the
            // server forcing a setting.

            // Push initial config values into the FiresWater static flags so when
            // WindGlobalDriver / FiresUnderwaterEnvDriver tick on the first frame
            // after Awake they see the right state.
            // Live updates from the BepInEx config UI route through the
            // SettingChanged hooks below.
            ApplyFiresWaterConfig();

            ConfigManager.Instance.configPresetToLoad.SettingChanged += (_, __) => ApplySelectedPreset();
            ConfigManager.Instance.configPresetSaveNow.SettingChanged += (_, __) => SaveNamedPreset();
            ConfigManager.Instance.configServerPreset.SettingChanged += (_, __) => ApplyServerPreset();

            // Everything but the master switch, the presets and the per-shader on/off toggles is deep tuning:
            // hidden behind the Advanced filter in both config windows, and lockable to admins by the server.
            FiresCore.Config.FiresConfigVisibility.MarkAdvanced(Config, ConfigManager.IsAdvancedTuning);
            FiresCore.Config.FiresAdminConfigGate.Register(Config,
                () => ConfigManager.Instance.configAdminOnlyTuning.Value, ConfigManager.IsAdvancedTuning);

            // 00 - Master: ONE switch -> vanilla graphics. Every gated system supports live Restore(),
            // so flipping it re-applies them all immediately; the water stack is exempt (not gated).
            ConfigManager.Instance.configMasterFxEnabled.SettingChanged += (_, __) =>
            {
                ApplyFiresWaterConfig();       // sky / atmosphere / planet / auroras / fog gates live inside
                ApplyFiresPlayerConfig();
                ApplyFiresLavaConfig();
                ApplyMistConfig();
                ApplyGlassConfig();
                ApplyPieceConfig();
                ApplyFiresHeightmapConfig();
            };

            ConfigManager.Instance.configFiresWaterEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSwapVanilla.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterEnv.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterBubbles.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterFogDensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterDepthFog.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterEnvReactive.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresLightningFxBlend.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterTransition.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterRefractShoreFade.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterRefractNearStart.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterRefractNearRange.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterBubbleDensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterBubbleSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            // FiresLava - initial apply + live re-apply on any lava config change (no rebuild needed).
            ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaEnabled.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaEmissionBoost.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaGlowFromTexture.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaFlowSpeed.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaMoltenColor.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaMetallic.SettingChanged += (_, __) => ApplyFiresLavaConfig();
            ConfigManager.Instance.configFiresLavaSmoothness.SettingChanged += (_, __) => ApplyFiresLavaConfig();

            // FiresHeightmap - config is the source of truth, POLLED by the driver every ~1s. Wire the
            // poll (enable + tune apply) and the console->config enable writer. The SettingChanged handlers
            // below are kept for instant response if the editor DOES fire them, but the poll is the net
            // that makes the toggles/sliders work regardless (observed: changes only applied on restart).
            VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ConfigPoll = HeightmapConfigPoll;
            VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.SetEnabledConfig = SetHeightmapEnabledConfig;
            VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ResetConfig = ResetHeightmapConfig;
            ApplyFiresHeightmapConfig();
            // Only the Enabled toggle swaps/restores terrain; the tune sliders re-push to the swapped
            // materials WITHOUT touching enable, so they work even when enabled via the `va_terrain`
            // console command (where the config Enabled is still false). Prior bug: every slider ran
            // the full config apply, which restored vanilla terrain when config-Enabled was false.
            ConfigManager.Instance.configFiresHeightmapEnabled.SettingChanged += (_, __) => ApplyFiresHeightmapConfig();
            ConfigManager.Instance.configFiresHeightmapLavaBrightness.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();
            ConfigManager.Instance.configFiresHeightmapLavaTint.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();
            ConfigManager.Instance.configFiresHeightmapFlowSpeed.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();
            ConfigManager.Instance.configFiresHeightmapArrayLayer.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();
            ConfigManager.Instance.configFiresHeightmapLavaCoverage.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();
            ConfigManager.Instance.configFiresHeightmapLavaThreshold.SettingChanged += (_, __) => ApplyFiresHeightmapTuning();

            // FiresMist - same model: config polled every ~1s; Enabled toggle swaps/restores the mist, the
            // tune sliders re-push to swapped mist materials live (work even when enabled via `va_mist`).
            VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.ConfigPoll = MistConfigPoll;
            VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.SetEnabledConfig = SetMistEnabledConfig;
            ApplyMistConfig();
            ConfigManager.Instance.configFiresMistEnabled.SettingChanged += (_, __) => ApplyMistConfig();
            ConfigManager.Instance.configFiresMistTint.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistBrightness.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistDensity.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistFadeScale.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistEdgePower.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistLightResponse.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistAltitude.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistPatchiness.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistPatchScale.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistWindDrift.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistPushStrength.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistPushRadius.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistCoverage.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistCoverScale.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistDenseTint.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistTwoTone.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistWeatherColor.SettingChanged += (_, __) => ApplyMistTuning();
            ConfigManager.Instance.configFiresMistWeatherDensity.SettingChanged += (_, __) => ApplyMistTuning();

            VerdantsAscent.Water.Core.FiresPieceShaderLoader.ConfigPoll = PieceConfigPoll;
            VerdantsAscent.Water.Core.FiresPieceShaderLoader.SetEnabledConfig = SetPieceEnabledConfig;
            ApplyPieceConfig();
            ConfigManager.Instance.configFiresPieceEnabled.SettingChanged += (_, __) => ApplyPieceConfig();
            ConfigManager.Instance.configFiresPieceWetStrength.SettingChanged += (_, __) => ApplyPieceTuning();
            ConfigManager.Instance.configFiresPieceWetSmoothness.SettingChanged += (_, __) => ApplyPieceTuning();
            ConfigManager.Instance.configFiresPieceSmoothnessScale.SettingChanged += (_, __) => ApplyPieceTuning();
            ConfigManager.Instance.configFiresPieceMetallicScale.SettingChanged += (_, __) => ApplyPieceTuning();

            // FiresGlass - push the projection/godray knobs into the pass + live re-apply.
            ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassEnabled.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassIntensity.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassReach.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassSoftness.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassUseVanillaShadow.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassShadowStrength.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassShadowBias.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassShadowThickness.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassGodrayDensity.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassNeonEmission.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassNeonCastIntensity.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassNeonCastAngle.SettingChanged += (_, __) => ApplyGlassConfig();
            ConfigManager.Instance.configFiresGlassNeonBeamFront.SettingChanged += (_, __) => ApplyGlassConfig();

            // FiresPlayer - initial apply + live re-apply on eye-config change.
            ApplyFiresPlayerConfig();
            ConfigManager.Instance.configFiresPlayerEnabled.SettingChanged += (_, __) => ApplyFiresPlayerConfig();
            ConfigManager.Instance.configFiresPlayerEyeColor.SettingChanged += (_, __) => ApplyFiresPlayerConfig();
            ConfigManager.Instance.configFiresPlayerEyeThreshold.SettingChanged += (_, __) => ApplyFiresPlayerConfig();

            // Elf ears - initial apply + live re-tune on any ear config change.
            ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsEnabled.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsScaleX.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsScaleY.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsScaleZ.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsOffsetX.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsOffsetY.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsOffsetZ.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsRoll.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsYaw.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsPitch.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsColor.SettingChanged += (_, __) => ApplyFiresEarsConfig();
            ConfigManager.Instance.configElfEarsSet.SettingChanged += (_, __) => ApplyFiresEarsConfig();

            // Hair & beard scale - initial apply + live update (the per-frame applier reads these values).
            ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configHairScaleEnabled.SettingChanged  += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configHairScaleX.SettingChanged        += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configHairScaleY.SettingChanged        += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configHairScaleZ.SettingChanged        += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configBeardScaleEnabled.SettingChanged += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configBeardScaleX.SettingChanged       += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configBeardScaleY.SettingChanged       += (_, __) => ApplyFiresHairBeardConfig();
            ConfigManager.Instance.configBeardScaleZ.SettingChanged       += (_, __) => ApplyFiresHairBeardConfig();

            // Player model (prototype) - REMOVED FOR NOW (10e configs commented out in ConfigManager). Uncomment with the configs to restore.
            // ApplyFiresPlayerModelConfig();
            // ConfigManager.Instance.configPlayerModelEnabled.SettingChanged += (_, __) => ApplyFiresPlayerModelConfig();
            // ConfigManager.Instance.configPlayerModelInflate.SettingChanged += (_, __) => ApplyFiresPlayerModelConfig();
            // ConfigManager.Instance.configPlayerModelIndex.SettingChanged += (_, __) => ApplyFiresPlayerModelConfig();

            // Hair physics - initial apply + live re-tune.
            ApplyFiresHairConfig();
            ConfigManager.Instance.configHairPhysEnabled.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairStiffness.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairDamping.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairGravity.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairWind.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairMaxSway.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairCollision.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairBodyRadius.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairBodyTop.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairBodyBottom.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairShoulderRadius.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairShoulderWidth.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairAnchorSide.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairAnchorUp.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairAnchorForward.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configBeardAnchorSide.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configBeardAnchorUp.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configBeardAnchorForward.SettingChanged += (_, __) => ApplyFiresHairConfig();
            ConfigManager.Instance.configHairSwayFalloff.SettingChanged += (_, __) => ApplyFiresHairConfig();

            // Hair/beard style + color - sync static fields from config; the driver applies once the
            // local player exists. SettingChanged pushes new values + applies immediately when in-world.
            ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configHairStyle.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configBeardStyle.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configHairColorEnabled.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configHairColor.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configBeardColorEnabled.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();
            ConfigManager.Instance.configBeardColor.SettingChanged += (_, __) => ApplyFiresCustomizationConfig();

            ConfigManager.Instance.configFiresSkyboxOverride.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxAtmosphereThicknessMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxFogHeightMinOffset.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxFogHeightMaxOffset.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxGroundColorBlendToSky.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configOceanRingExtendEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configOceanRingExtendedActiveArea.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterExtendedRingEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFollowRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configRingWaveDetailDistance.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configRingWaveDetailFloor.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configRingSeamOverlap.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configUseClipmapOcean.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configClipmapOceanRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configClipmapOceanLods.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configClipmapOceanResolution.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configClipmapOceanInnerHole.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistantReflection.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistantTint.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistantBrightness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamLayerA.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamLayerB.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamSlope.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamWaveHeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterReflectionFloor.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterReflectionMax.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFresnelSoftness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterWaveOpacity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamScrollSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSurfaceCausticsEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticStrength.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticWarp.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticSharp.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterNormalSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamSunBoost.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticShadow.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticDepthFade.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterBelowWaterCaustics.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticPassStrength.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterCausticMoonScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogDensityScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogColorOverride.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogColor.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogMode.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogLinearStart.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDistanceFogLinearEnd.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            System.EventHandler vfApply = (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configVolFogEnabled.SettingChanged += vfApply;
            ConfigManager.Instance.configVolFogDomainConfine.SettingChanged += vfApply;
            ConfigManager.Instance.configVolFogSkyBlend.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandOn.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandColor.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandColorThin.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandMin.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandMax.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandBotFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandTopFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandDensity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandMaxOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandPatchScale.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandPatch.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandCoverage.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandClearOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandPatchFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandWind.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandFarCut.SettingChanged += vfApply;
            ConfigManager.Instance.configVfLandUndulate.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaOn.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaColor.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaColorThin.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaBelow.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaThick.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaBotFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaTopFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaDensity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaMaxOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaPatchScale.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaPatch.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaCoverage.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaClearOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaPatchFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaWind.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaFarCut.SettingChanged += vfApply;
            ConfigManager.Instance.configVfSeaUndulate.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwOn.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwColor.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwDensity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwMaxOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwPatchScale.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwSwirl.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwCurrent.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwDistStart.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwDistRange.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwFarCut.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwColorThin.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwCoverage.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwPatch.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwClearOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwPatchFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwDepthDensity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfUwDepthSpan.SettingChanged += vfApply;
            ConfigManager.Instance.configVfPushOn.SettingChanged += vfApply;
            ConfigManager.Instance.configVfPushRadius.SettingChanged += vfApply;
            ConfigManager.Instance.configVfPushStrength.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudOn.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudColor.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudMin.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudMax.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudBotFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudTopFade.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudDensity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudMaxOp.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudCoverage.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudPatch.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudPatchScale.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudUndulate.SettingChanged += vfApply;
            ConfigManager.Instance.configVfCloudDrift.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvReactive.SettingChanged += vfApply;
            ConfigManager.Instance.configVfClearDayFog.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvColorBlend.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvDensityBlend.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvRainBoost.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvStormOpacity.SettingChanged += vfApply;
            ConfigManager.Instance.configVfEnvAffectUw.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreFieldEnabled.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreWaveFloor.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreWaveFullDepth.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreDepthDarkness.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreDepthForDark.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreFieldExtent.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreFieldResolution.SettingChanged += vfApply;
            ConfigManager.Instance.configShoreFogDomainBlend.SettingChanged += vfApply;
            ConfigManager.Instance.configFiresWaterCausticDebug.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUWCausticScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUWCausticSharpness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUWCausticStrength.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUWCausticSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUWCausticWarp.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterUnderwaterDapple.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSurfaceFog.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterMeniscusEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterMeniscusStrength.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterMeniscusWidth.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterShipMaskFit.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterShipMaskMargin.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarRefl.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarReflResolution.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarReflBankClip.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarReflDistort.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarReflMaxHeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPlanarReflTerrain.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSSSColor.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSSSBase.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSSSSun.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSSSSunFalloff.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSSSWaveBoost.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterAbsorbBlend.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterAbsorbExtinction.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterFoamBubbleIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterDistNormalFade.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterDistSpecRough.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSchlickFresnel.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterNadirReflCap.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterPixelDepth.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterSunSpec.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresWaterWaveBandLimit.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configHideVanillaLodWater.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowAtmosphereMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraGlowFogHeightMin.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraOverlayEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraOverlayIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraNorthBiasPower.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraContribCap.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraAltMin.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraAltMax.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraColumnFreq.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraVertFreq.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraSharpness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraScrollSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraPulseSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            // Vanilla skybox multipliers + lights
            ConfigManager.Instance.configFiresSkyboxExposureMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxStormDarken.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxStarExposureMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxSunSizeMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxSunDiskBrightness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxMoonSizeMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxSunLightMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresSkyboxMoonLightMul.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeClearDiskBoost.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeHazyDiskBoost.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeClearSunSize.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeHazySunSize.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeLowSunElevation.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeLowSunWeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configSunHazeWeatherWeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCylinderRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCylinderHeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudPlaneRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudCoverage.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudWorldScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudSoftness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudOpacity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudDetailStrength.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudScrollSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudBrightness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudDensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudHue.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudAltFadeStart.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresAtmosphereCloudAltFadeEnd.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetOceanColor.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetSurfaceCloudOpacity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetCloudCoverage.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configStormDarken.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configStormCoverage.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configStormOpacity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetCloudSoftness.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetCloudScale.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetCloudScrollSpeed.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetDomeRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetMapOpacity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetVibrance.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetHue.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetHideWorld.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetDomeHeight.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configFiresPlanetTieWaterToDome.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            // Aurora light + shafts
            ConfigManager.Instance.configAuroraLightEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraLightIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraLightElevation.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraLightAzimuth.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraShaftsEnabled.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraShaftsIntensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraShaftsBlurRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraShaftsMaxRadius.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            // Day-sky knobs
            ConfigManager.Instance.configDaySkyHue.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDaySkyDensity.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configStarFadeBySky.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configDaySkyColor.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            ConfigManager.Instance.configAuroraNightOnly.SettingChanged += (_, __) => ApplyFiresWaterConfig();
            ConfigManager.Instance.configAuroraDuringStorms.SettingChanged += (_, __) => ApplyFiresWaterConfig();

            // FUC contract: hand FUC our water-wrap so gameplay mods (FAT, etc.)
            // can skin their custom water meshes with the FiresWater look via
            // WaterShaderBridge.Wrap() - without referencing this mod, its bundle,
            // or its shader. MaybeWrap self-gates on the FiresWater Enabled toggle,
            // so a single registration covers the live on/off.
            FiresCore.Services.WaterShaderBridge.Register(
                VerdantsAscent.Water.Core.FiresWaterShaderLoader.MaybeWrap);

            // Once the world is live, retroactively swap any WaterVolumes that
            // came online before our Harmony OnEnable hook started catching
            // them. Cheap idempotent scan - no-op when SwapVanillaOcean is
            // false. Started here so it runs in lockstep with the rest of the
            // mod's startup; the coroutine handles its own readiness wait.
            StartCoroutine(FiresWaterApplyWhenReady());

            // Tile-restore batch tracker - starts an infinite polling
            // coroutine that emits the TILES banner whenever a wave
            // of tile restores (TileAnchor.Load calls) has settled.
            // Per-tile success logs are verbose-only; the batch banner
            // is the default-visible summary. See VATileRestoreBatch
            // for the debounce model.
            if (ZNet.instance && ZNet.instance.IsDedicated())
                StartCoroutine(DelayedDedicatedServerInit());
            else
                FinishInit();
        }

        private void AttachRenderDrivers()
        {
            // Pushes EnvMan wind direction + wrapped day-time into Shader
            // globals each frame so Custom/FiresWater wave animation stays
            // welded to whatever vanilla ocean is doing. Component's Update
            // self-gates on FiresWaterShaderLoader.Enabled - a no-op if the
            // shader bundle isn't loaded, so attaching unconditionally is
            // free.
            RootObject.AddComponent<VerdantsAscent.Water.Core.WindGlobalDriver>();
            // Per-frame env-force driver: registers a "FiresUnderwater"
            // EnvSetup with EnvMan and forces it active whenever the local
            // player's eye is below a water surface. Replaces the simpler
            // UnderwaterFogDriver path - instead of mutating
            // RenderSettings.fog directly, this drives EnvMan so fog +
            // sun + ambient + wet status all switch together with vanilla's
            // 2-second blend. The old UnderwaterFogDriver file is retained
            // (unused) in case we want the raw fog-only path back later.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresUnderwaterEnvDriver>();
            // Our own underwater bubble particles (replaces the old red-Yagluth
            // borrow); self-gates on FiresUnderwaterEnv.LocalSubmerged.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresUnderwaterBubbles>();
            // Waterline meniscus band (only hooks within ~1m of the surface).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresMeniscusPass>();
            // Stretches a submerged boat's water-mask up to the surface so the ocean
            // stops rendering over the deck/player; restores to vanilla while floating.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresShipWaterMaskFit>();
            // True planar mirror reflection - a reflected camera renders the scene into the
            // water surface. Self-gates near-water-only; no-op until its config flips on.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresPlanarReflection>();
            // Per-frame procedural-skybox property overrider. No-op until
            // ConfigManager.configFiresSkyboxOverride flips on. Used to
            // diagnose which skybox property creates the horizon-haze band
            // visible past distant water - see FiresSkyboxDriver class docs.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresSkyboxDriverComponent>();
            // ===================================================================
            // OCEAN COVERAGE WATER. FiresWaterExtendedRing is the ONE ocean
            // surface that fills out to the horizon - a single animated
            // FiresWater grid mesh that recenters on the player (zero churn,
            // seam-free with zone water because the shader is world-space) and
            // hides vanilla's water_lod plane. It replaces BOTH the old
            // standalone tile grid AND the vanilla-LOD shader-swap, which made a
            // camera-following flat sheet that fought the zone water.
            //
            // The other two augmentation drivers stay DISABLED (classes intact
            // on disk if needed):
            //   * OceanZoneRingExtenderDriver - bumped m_activeArea over deep
            //     water, waking zone content (mobs/veg/builds). Unneeded now:
            //     the ring covers the visual, the Floating fallback covers
            //     physics.
            //   * FiresWaterFlatWorldPlaneDriver - static flat plane, superseded
            //     by the single animated ring mesh.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresWaterExtendedRingDriver>();
            // Clipmap-ocean ALTERNATIVE far coverage (Crest-style cascade rings),
            // config-gated OFF by default ("05 - Far Water Disk / 09 UseClipmapOcean").
            // Self-gates on FiresClipmapOcean.Enabled - pushed from
            // ApplyFiresWaterConfig, which also stands the disk down while the
            // clipmap owns the horizon - so mounting unconditionally is free,
            // exactly like the disk driver above.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresClipmapOceanDriver>();
            // REAL-water extension: keep MORE real per-zone water loaded around the
            // player by bumping ZoneSystem.m_activeArea over deep ocean. This is the
            // ACTUAL vanilla zone-water mesh extended - seamless by nature (no clone
            // tiles, no separate layer to tear). Gated to deep ocean so it doesn't
            // wake land content. The FiresWaterZoneTiles clone-tile approach is RETIRED
            // (it added a separate seaming layer - exactly what we don't want).
            RootObject.AddComponent<VerdantsAscent.Water.Core.OceanZoneRingExtenderDriver>();
            // Stage 2 underwater caustics - self-gates on camera-underwater +
            // the Custom/FiresCaustics shader being in the bundle.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresCausticsPass>();
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresVolumetricFogPass>();
            // FiresGlass (P1) - stained-glass light projection. No-op until a test
            // window is planted (`va_glass here`) and its shader is in the bundle,
            // so registering it here is free, exactly like the caustics pass.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresGlassProjector>();
            // Shore field - bakes a world-XZ texture from terrain height so the water
            // shader ramps waves down near shore (full in deep ocean) and darkens
            // water by depth, seam-free. Replaces the wave/depth hardcodes.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresShoreField>();
            //   RootObject.AddComponent<VerdantsAscent.Water.Core.FiresWaterFlatWorldPlaneDriver>();
            // ===================================================================
            // Rescale vanilla CloudCylinder + cloud_plane_upper_downside so they
            // wrap our gameplay volume as an "atmosphere bubble" at ~3000m radius
            // instead of intersecting the foreground.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresVanillaSkyTunerDriver>();
            // Cloud dome rendered as a planet from space + weather clouds from the ground
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresPlanetDriver>();
            // Aurora skybox shader replacement
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresSkyboxAuroraDriver>();
            // Aurora directional light (water reflections + ambient rim)
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresAuroraLightDriver>();
            // Aurora god-ray shafts (second SunShafts pass pointing at aurora light)
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresAuroraShaftsPassDriver>();
            // Ashlands lava: swap vanilla Custom/FlowOpaque -> our Custom/FiresFlowOpaque
            // + FireLavaCrust/FireLavaFlow textures. Registers `va_lava on|off|status` and
            // re-scans for lava materials as they stream in. On by default.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresLavaDriver>();
            // Terrain: swap Custom/Heightmap -> Custom/FiresHeightmap (Ashlands lava biome paint).
            // va_terrain on|off|status; lava tuning via config. OFF by default (opt-in, terrain-wide).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresHeightmapDriver>();
            // Mistlands mist: swap Custom/LitParticles (heavymist) -> Custom/FiresLitParticles.
            // va_mist on|off|status. OFF by default (opt-in); scoped to "mist"-named materials.
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresLitParticlesDriver>();
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresPieceDriver>();
            // Character bodies: swap Custom/Player -> Custom/FiresPlayer (adds eye color).
            // va_player on|off|status; eye color via config. On by default (white eyes = vanilla).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresPlayerDriver>();
            // Procedural elf ears on the local player (off by default; va_ears + config placement).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresElfEarsDriver>();
            // Physics hair on the local player (off by default; va_hair + config tuning).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresHairDriver>();
            // Hair/beard style + color on the local player (config-driven; va_styles lists indices).
            RootObject.AddComponent<VerdantsAscent.Water.Core.FiresPlayerCustomizationDriver>();
        }

        // Push current config values into the static flags the FiresWater
        // shader plumbing reads. Called once at startup with the bound
        // defaults, and again any time a SettingChanged event fires.
        //
        // When SwapVanillaOcean transitions OFF -> we restore originals so
        // vanilla water comes back without needing a restart. When it
        // transitions ON we don't apply here directly - the coroutine
        // FiresWaterApplyWhenReady handles the initial scan, and the
        // Harmony hook on WaterVolume.OnEnable handles any volume that
        // streams in later.
        private void ApplyFiresHairConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresHairPhysics.Stiffness       = cm.configHairStiffness.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.Damping         = cm.configHairDamping.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.Gravity         = cm.configHairGravity.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.WindInfluence   = cm.configHairWind.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.MaxSway         = cm.configHairMaxSway.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.CollisionRadius = cm.configHairCollision.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.BodyRadius       = cm.configHairBodyRadius.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.BodyTopHeight    = cm.configHairBodyTop.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.BodyBottomHeight = cm.configHairBodyBottom.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.ShoulderRadius   = cm.configHairShoulderRadius.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.ShoulderWidth    = cm.configHairShoulderWidth.Value;
                VerdantsAscent.Water.Core.FiresHairPhysics.HairAnchorOffset  = new UnityEngine.Vector3(
                    cm.configHairAnchorSide.Value, cm.configHairAnchorUp.Value, cm.configHairAnchorForward.Value);
                VerdantsAscent.Water.Core.FiresHairPhysics.BeardAnchorOffset = new UnityEngine.Vector3(
                    cm.configBeardAnchorSide.Value, cm.configBeardAnchorUp.Value, cm.configBeardAnchorForward.Value);
                VerdantsAscent.Water.Core.FiresHairPhysics.SwayFalloff = cm.configHairSwayFalloff.Value;

                if (cm.configHairPhysEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresHairPhysics.Enabled = true;
                    VerdantsAscent.Water.Core.FiresHairPhysics.ApplyToLocalPlayer();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresHairPhysics.Enabled = false;
                    VerdantsAscent.Water.Core.FiresHairPhysics.Remove();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresHairConfig failed: {ex.Message}");
            }
        }

        private void ApplyFiresCustomizationConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.HairStyle         = cm.configHairStyle.Value;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.BeardStyle        = cm.configBeardStyle.Value;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.HairColorEnabled  = cm.configHairColorEnabled.Value;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.HairColor         = cm.configHairColor.Value;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.BeardColorEnabled = cm.configBeardColorEnabled.Value;
                VerdantsAscent.Water.Core.FiresPlayerCustomization.BeardColor        = cm.configBeardColor.Value;

                // Apply now if we're already in-world; otherwise the driver picks it up once the
                // local player spawns (ObjectDB/Player aren't ready during Awake).
                VerdantsAscent.Water.Core.FiresPlayerCustomization.ApplyStyles();
                VerdantsAscent.Water.Core.FiresPlayerCustomization.ApplyHairColor();
                VerdantsAscent.Water.Core.FiresPlayerCustomization.ApplyBeardColorTick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresCustomizationConfig failed: {ex.Message}");
            }
        }

        private void ApplyFiresEarsConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresElfEars.OffsetX  = cm.configElfEarsOffsetX.Value;
                VerdantsAscent.Water.Core.FiresElfEars.OffsetY  = cm.configElfEarsOffsetY.Value;
                VerdantsAscent.Water.Core.FiresElfEars.OffsetZ  = cm.configElfEarsOffsetZ.Value;
                VerdantsAscent.Water.Core.FiresElfEars.ScaleX = cm.configElfEarsScaleX.Value;
                VerdantsAscent.Water.Core.FiresElfEars.ScaleY = cm.configElfEarsScaleY.Value;
                VerdantsAscent.Water.Core.FiresElfEars.ScaleZ = cm.configElfEarsScaleZ.Value;
                VerdantsAscent.Water.Core.FiresElfEars.Pitch    = cm.configElfEarsPitch.Value;
                VerdantsAscent.Water.Core.FiresElfEars.Yaw      = cm.configElfEarsYaw.Value;
                VerdantsAscent.Water.Core.FiresElfEars.Roll     = cm.configElfEarsRoll.Value;
                VerdantsAscent.Water.Core.FiresElfEars.EarColor = cm.configElfEarsColor.Value;
                VerdantsAscent.Water.Core.FiresElfEars.EarSet   = cm.configElfEarsSet.Value;

                if (cm.configElfEarsEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresElfEars.Enabled = true;
                    VerdantsAscent.Water.Core.FiresElfEars.ApplyToLocalPlayer();
                    VerdantsAscent.Water.Core.FiresElfEars.ReTuneAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresElfEars.Enabled = false;
                    VerdantsAscent.Water.Core.FiresElfEars.Remove();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresEarsConfig failed: {ex.Message}");
            }
        }

        private void ApplyFiresHairBeardConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresHairBeardScale.HairEnabled  = cm.configHairScaleEnabled.Value;
                VerdantsAscent.Water.Core.FiresHairBeardScale.BeardEnabled = cm.configBeardScaleEnabled.Value;
                VerdantsAscent.Water.Core.FiresHairBeardScale.HairScale  = new UnityEngine.Vector3(cm.configHairScaleX.Value,  cm.configHairScaleY.Value,  cm.configHairScaleZ.Value);
                VerdantsAscent.Water.Core.FiresHairBeardScale.BeardScale = new UnityEngine.Vector3(cm.configBeardScaleX.Value, cm.configBeardScaleY.Value, cm.configBeardScaleZ.Value);

                // A skinned hair/beard ignores transform scale, so the size is baked into the physics
                // reskin instead. Re-attach the vanilla strands so the physics re-reskins at the new size.
                if (cm.configHairPhysEnabled.Value)
                    VerdantsAscent.Water.Core.FiresHairPhysics.ForceRebuild();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresHairBeardConfig failed: {ex.Message}");
            }
        }

        // Player model (prototype) - REMOVED FOR NOW. Drove FiresPlayerModel from the 10e configs (commented out).
        // Uncomment this method + the 10e configs + the init wiring above to restore.
        /*
        private void ApplyFiresPlayerModelConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                var _models = VerdantsAscent.Water.Core.FiresPlayerModel.AvailableModels();   // sorted; stable for the index slider
                int _idx = Mathf.Clamp(cm.configPlayerModelIndex.Value, 0, Mathf.Max(0, _models.Count - 1));
                string _picked = _models.Count > 0 ? _models[_idx] : null;
                VerdantsAscent.Water.Core.FiresPlayerModel.ActiveModelName = _picked;
                Debug.Log($"[{PluginName}] player model [{_idx}] -> {(_picked ?? "(none)")}");
                bool on = cm.configPlayerModelEnabled.Value;
                VerdantsAscent.Water.Core.FiresPlayerModel.Enabled = on;
                VerdantsAscent.Water.Core.FiresPlayerModel.Restore();   // clear any existing instance so a model-name change takes effect
                if (on) VerdantsAscent.Water.Core.FiresPlayerModel.ApplyToLocalPlayer();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresPlayerModelConfig failed: {ex.Message}");
            }
        }
        */

        private void ApplyFiresPlayerConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresPlayerShaderLoader.TuneEyeColor     = cm.configFiresPlayerEyeColor.Value;
                VerdantsAscent.Water.Core.FiresPlayerShaderLoader.TuneEyeThreshold = cm.configFiresPlayerEyeThreshold.Value;

                if (ConfigManager.MasterFx && cm.configFiresPlayerEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresPlayerShaderLoader.Enabled = true;
                    VerdantsAscent.Water.Core.FiresPlayerShaderLoader.ApplyToAll();
                    VerdantsAscent.Water.Core.FiresPlayerShaderLoader.ApplyTuningToAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresPlayerShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresPlayerShaderLoader.Restore();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresPlayerConfig failed: {ex.Message}");
            }
        }

        private void ApplyFiresLavaConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneEmissionBoost   = cm.configFiresLavaEmissionBoost.Value;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneGlowFromTexture = cm.configFiresLavaGlowFromTexture.Value;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneFlowSpeed       = cm.configFiresLavaFlowSpeed.Value;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneMoltenColor     = cm.configFiresLavaMoltenColor.Value;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneMetallic        = cm.configFiresLavaMetallic.Value;
                VerdantsAscent.Water.Core.FiresLavaShaderLoader.TuneSmoothness      = cm.configFiresLavaSmoothness.Value;

                if (ConfigManager.MasterFx && cm.configFiresLavaEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresLavaShaderLoader.Enabled = true;
                    VerdantsAscent.Water.Core.FiresLavaShaderLoader.ApplyToAllLava();
                    VerdantsAscent.Water.Core.FiresLavaShaderLoader.ApplyTuningToAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresLavaShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresLavaShaderLoader.Restore();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresLavaConfig failed: {ex.Message}");
            }
        }

        // Push the lava tuning to whatever terrain is currently swapped. Does NOT swap or restore, so a
        // tune-slider change is safe regardless of how the swap was enabled (config OR va_terrain cmd).
        private void ApplyFiresHeightmapTuning()
        {
            try
            {
                var cm = ConfigManager.Instance;
                Color tint   = cm.configFiresHeightmapLavaTint.Value;
                float bright = cm.configFiresHeightmapLavaBrightness.Value;
                // Glow is the HDR product tint*brightness (default 1,0.133,0 * 6 = the baked 6,0.8,0).
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.TuneGlowColor =
                    new Color(tint.r * bright, tint.g * bright, tint.b * bright, 1f);
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.TuneFlowSpeed  = cm.configFiresHeightmapFlowSpeed.Value;
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.TuneArrayLayer = cm.configFiresHeightmapArrayLayer.Value;
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.TuneThreshold  = cm.configFiresHeightmapLavaThreshold.Value;
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.TuneCoverage   = cm.configFiresHeightmapLavaCoverage.Value;
                VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ApplyTuningToAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresHeightmapTuning failed: {ex.Message}");
            }
        }

        // ---- FiresMist: tune push + enable apply + poll + console->config writer (mirrors the heightmap) ----
        private void ApplyMistTuning()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneTint       = cm.configFiresMistTint.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneBrightness = cm.configFiresMistBrightness.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneDensity    = cm.configFiresMistDensity.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneFadeScale  = cm.configFiresMistFadeScale.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneEdgePower  = cm.configFiresMistEdgePower.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneLightScale = cm.configFiresMistLightResponse.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneAltitude   = cm.configFiresMistAltitude.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TunePatchiness = cm.configFiresMistPatchiness.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TunePatchScale = cm.configFiresMistPatchScale.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneWindDrift    = cm.configFiresMistWindDrift.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TunePushStrength = cm.configFiresMistPushStrength.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TunePushRadius   = cm.configFiresMistPushRadius.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneCoverage     = cm.configFiresMistCoverage.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneCoverScale   = cm.configFiresMistCoverScale.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneDenseTint    = cm.configFiresMistDenseTint.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.TuneTwoTone      = cm.configFiresMistTwoTone.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.MistColorBlend   = cm.configFiresMistWeatherColor.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.MistDensityBlend = cm.configFiresMistWeatherDensity.Value;
                VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.ApplyTuningToAll();
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ApplyMistTuning failed: {ex.Message}"); }
        }

        private void ApplyMistConfig()
        {
            try
            {
                ApplyMistTuning();
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresMistEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Enabled = true;
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.ApplyToAll();
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.ApplyTuningToAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Restore();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ApplyMistConfig failed: {ex.Message}"); }
        }

        // Polled ~1s by FiresLitParticlesDriver (which also does the throttled ApplyToAll scan when Enabled).
        private void MistConfigPoll()
        {
            try
            {
                ApplyMistTuning();   // push tune to swapped mist mats (cheap)
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresMistEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Enabled = true;
                }
                else if (VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Enabled
                         || VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.SwappedCount > 0)
                {
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresLitParticlesShaderLoader.Restore();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] MistConfigPoll failed: {ex.Message}"); }
        }

        private void SetMistEnabledConfig(bool v)
        {
            try { ConfigManager.Instance.configFiresMistEnabled.Value = v; }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] SetMistEnabledConfig failed: {ex.Message}"); }
        }

        // ---- FiresGlass: push the projection/godray knobs straight into the pass statics ----
        private void ApplyGlassConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresGlassProjector.Active          = ConfigManager.MasterFx && cm.configFiresGlassEnabled.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.Intensity       = cm.configFiresGlassIntensity.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.Reach           = cm.configFiresGlassReach.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.Softness        = cm.configFiresGlassSoftness.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.UseVanillaShadow = cm.configFiresGlassUseVanillaShadow.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.ShadowStrength = cm.configFiresGlassShadowStrength.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.ShadowLow      = cm.configFiresGlassShadowBias.Value;       // repurposed -> luminance low
                VerdantsAscent.Water.Core.FiresGlassProjector.ShadowHigh     = cm.configFiresGlassShadowThickness.Value;  // repurposed -> luminance high
                VerdantsAscent.Water.Core.FiresGlassProjector.GodrayDensity   = cm.configFiresGlassGodrayDensity.Value;
                UnityEngine.Shader.SetGlobalFloat(UnityEngine.Shader.PropertyToID("_FiresNeonEmission"),
                    cm.configFiresGlassNeonEmission.Value);
                VerdantsAscent.Water.Core.FiresGlassProjector.NeonCastIntensity = cm.configFiresGlassNeonCastIntensity.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.NeonCastAngleDeg  = cm.configFiresGlassNeonCastAngle.Value;
                VerdantsAscent.Water.Core.FiresGlassProjector.NeonBeamSign      = cm.configFiresGlassNeonBeamFront.Value ? -1f : 1f;
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ApplyGlassConfig failed: {ex.Message}"); }
        }

        // ---- FiresPiece: tune wet/reflection + enable apply + poll + console->config writer (mirrors the mist) ----
        private void ApplyPieceTuning()
        {
            try
            {
                var cm = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresPieceShaderLoader.TuneWetStrength     = cm.configFiresPieceWetStrength.Value;
                VerdantsAscent.Water.Core.FiresPieceShaderLoader.TuneWetSmoothness   = cm.configFiresPieceWetSmoothness.Value;
                VerdantsAscent.Water.Core.FiresPieceShaderLoader.TuneSmoothnessScale = cm.configFiresPieceSmoothnessScale.Value;
                VerdantsAscent.Water.Core.FiresPieceShaderLoader.TuneMetallicScale   = cm.configFiresPieceMetallicScale.Value;
                VerdantsAscent.Water.Core.FiresPieceShaderLoader.ApplyTuningToAll();
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ApplyPieceTuning failed: {ex.Message}"); }
        }

        private void ApplyPieceConfig()
        {
            try
            {
                ApplyPieceTuning();
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresPieceEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Enabled = true;
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.ApplyToAll();
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.ApplyTuningToAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Restore();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ApplyPieceConfig failed: {ex.Message}"); }
        }

        // Polled ~1s by FiresPieceDriver (which also does the throttled ApplyToAll scan when Enabled).
        private void PieceConfigPoll()
        {
            try
            {
                ApplyPieceTuning();
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresPieceEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Enabled = true;
                }
                else if (VerdantsAscent.Water.Core.FiresPieceShaderLoader.Enabled
                         || VerdantsAscent.Water.Core.FiresPieceShaderLoader.SwappedCount > 0)
                {
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresPieceShaderLoader.Restore();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] PieceConfigPoll failed: {ex.Message}"); }
        }

        private void SetPieceEnabledConfig(bool v)
        {
            try { ConfigManager.Instance.configFiresPieceEnabled.Value = v; }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] SetPieceEnabledConfig failed: {ex.Message}"); }
        }

        private void ApplyFiresHeightmapConfig()
        {
            try
            {
                ApplyFiresHeightmapTuning();   // keep Tune* current before (re)applying enable state
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresHeightmapEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Enabled = true;
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ApplyToAllTerrain();
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ApplyTuningToAll();
                }
                else
                {
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Restore();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresHeightmapConfig failed: {ex.Message}");
            }
        }

        // Throttle for the expensive material scan inside the per-second config poll.
        private float _hmNextScan;

        // Called ~1s by FiresHeightmapDriver. Config is the source of truth: read enable + tune from it
        // and apply swap/restore/tune, so the toggles and sliders work live without SettingChanged.
        private void HeightmapConfigPoll()
        {
            try
            {
                ApplyFiresHeightmapTuning();   // push tune values to whatever is swapped (cheap)
                if (ConfigManager.MasterFx && ConfigManager.Instance.configFiresHeightmapEnabled.Value)
                {
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Enabled = true;
                    // The material scan is the only costly part - throttle it to ~4s; new zones still get
                    // caught and the tune push above keeps the swapped ones live every second.
                    if (Time.unscaledTime >= _hmNextScan)
                    {
                        _hmNextScan = Time.unscaledTime + 4f;
                        VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.ApplyToAllTerrain();
                    }
                }
                else if (VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Enabled
                         || VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.SwappedCount > 0)
                {
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Enabled = false;
                    VerdantsAscent.Water.Core.FiresHeightmapShaderLoader.Restore();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] HeightmapConfigPoll failed: {ex.Message}");
            }
        }

        // `va_terrain on|off` routes through here so the console toggle WRITES the config (the polled
        // source of truth) instead of being instantly undone by the next poll.
        private void SetHeightmapEnabledConfig(bool v)
        {
            try { ConfigManager.Instance.configFiresHeightmapEnabled.Value = v; }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] SetHeightmapEnabledConfig failed: {ex.Message}"); }
        }

        // `va_terrain reset` -> restore the section-17 lava tuning to its bound defaults (layer 15,
        // threshold 0.2, coverage 1, brightness 6, ...) and re-push. For recovering after dialing the
        // sliders too far (e.g. raising LavaThreshold / changing LavaArrayLayer gates the lava off).
        private void ResetHeightmapConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                cm.configFiresHeightmapLavaBrightness.Value = (float)cm.configFiresHeightmapLavaBrightness.DefaultValue;
                cm.configFiresHeightmapLavaTint.Value       = (UnityEngine.Color)cm.configFiresHeightmapLavaTint.DefaultValue;
                cm.configFiresHeightmapFlowSpeed.Value      = (float)cm.configFiresHeightmapFlowSpeed.DefaultValue;
                cm.configFiresHeightmapArrayLayer.Value     = (int)cm.configFiresHeightmapArrayLayer.DefaultValue;
                cm.configFiresHeightmapLavaCoverage.Value   = (float)cm.configFiresHeightmapLavaCoverage.DefaultValue;
                cm.configFiresHeightmapLavaThreshold.Value  = (float)cm.configFiresHeightmapLavaThreshold.DefaultValue;
                ApplyFiresHeightmapTuning();
            }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] ResetHeightmapConfig failed: {ex.Message}"); }
        }

        private bool m_applyingPreset;

        private void ApplySelectedPreset()
        {
            if (m_applyingPreset) return;
            string presetName = ConfigManager.Instance.configPresetToLoad.Value;
            if (string.IsNullOrEmpty(presetName) || presetName == ConfigManager.NoPresetOption) return;

            m_applyingPreset = true;
            try
            {
                int changed = FiresCore.Config.FiresConfigPresets.ApplyPreset(
                    ConfigManager.PresetModName, Config, presetName);
                FiresLogger.LogInfo($"Preset '{presetName}' applied ({changed} setting(s) changed).");
            }
            finally
            {
                m_applyingPreset = false;
            }
        }

        /// <summary>Applies the preset the SERVER names, so a whole server can share one look. Skipped on the
        /// server itself, which is where the value is authored.</summary>
        private void ApplyServerPreset()
        {
            string presetName = ConfigManager.Instance.configServerPreset.Value;
            if (string.IsNullOrWhiteSpace(presetName)) return;
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;

            if (m_applyingPreset) return;
            m_applyingPreset = true;
            try
            {
                int changed = FiresCore.Config.FiresConfigPresets.ApplyPreset(
                    ConfigManager.PresetModName, Config, presetName);
                FiresLogger.LogInfo($"Server preset '{presetName}' applied ({changed} setting(s) changed).");
            }
            finally
            {
                m_applyingPreset = false;
            }
        }

        private void SaveNamedPreset()
        {
            if (!ConfigManager.Instance.configPresetSaveNow.Value) return;

            FiresCore.Config.FiresConfigPresets.SaveCurrentValuesAsPreset(
                ConfigManager.PresetModName, Config, ConfigManager.Instance.configPresetSaveName.Value);
            ConfigManager.Instance.configPresetSaveNow.Value = false;
        }

        private void ApplyFiresWaterConfig()
        {
            try
            {
                bool prevSwap = VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean;

                // Master gate: the water stack was historically EXEMPT from "00 Master", so
                // flipping it off never returned the ocean to vanilla. Master now ANDs into
                // every water-stack ENABLE (values stay live) - the prevSwap/nowSwap logic
                // below restores the vanilla water materials the moment master goes off.
                // The cross-mod global tells the other Fires mods (Galaxies moon/orbitals)
                // to stand down too, with no assembly coupling.
                bool fx = ConfigManager.MasterFx;
                UnityEngine.Shader.SetGlobalFloat(
                    UnityEngine.Shader.PropertyToID("_FiresMasterFxOff"), fx ? 0f : 1f);

                VerdantsAscent.Water.Core.FiresWaterShaderLoader.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterEnabled.Value;
                VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean
                    = fx && ConfigManager.Instance.configFiresWaterSwapVanilla.Value;
                VerdantsAscent.Water.Core.FiresUnderwaterEnv.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterUnderwaterEnv.Value;
                VerdantsAscent.Water.Core.FiresUnderwaterBubbles.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterUnderwaterBubbles.Value;
                VerdantsAscent.Water.Core.FiresUnderwaterEnv.SetFogDensityScale(
                    ConfigManager.Instance.configFiresWaterUnderwaterFogDensity.Value);
                VerdantsAscent.Water.Core.FiresUnderwaterEnv.DepthFogEnabled
                    = fx && ConfigManager.Instance.configFiresWaterUnderwaterDepthFog.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.AffectUnderwater
                    = ConfigManager.Instance.configFiresWaterUnderwaterEnvReactive.Value;

                // Lightning FX blend - gated by the master FX toggle (the patch previously ran
                // unconditionally: Enabled was wired to nothing). On the OFF edge, restore every
                // live swapped material (the persistent storm flash card keeps our shader otherwise).
                bool lightningBlend = ConfigManager.MasterFx
                    && ConfigManager.Instance.configFiresLightningFxBlend.Value;
                if (!lightningBlend && VerdantsAscent.Water.Core.FiresLightningBlend.Enabled)
                    VerdantsAscent.Water.Core.FiresLightningBlend.RestoreAllSwapped();
                VerdantsAscent.Water.Core.FiresLightningBlend.Enabled = lightningBlend;
                VerdantsAscent.Water.Core.FiresUnderwaterEnv.UwTransitionSeconds
                    = ConfigManager.Instance.configFiresWaterUnderwaterTransition.Value;
                UnityEngine.Shader.SetGlobalFloat(UnityEngine.Shader.PropertyToID("_FiresRefractShoreFade"),
                    ConfigManager.Instance.configFiresWaterRefractShoreFade.Value);
                UnityEngine.Shader.SetGlobalFloat(UnityEngine.Shader.PropertyToID("_FiresRefractNearStart"),
                    ConfigManager.Instance.configFiresWaterRefractNearStart.Value);
                UnityEngine.Shader.SetGlobalFloat(UnityEngine.Shader.PropertyToID("_FiresRefractNearRange"),
                    ConfigManager.Instance.configFiresWaterRefractNearRange.Value);
                VerdantsAscent.Water.Core.FiresUnderwaterBubbles.Density
                    = ConfigManager.Instance.configFiresWaterBubbleDensity.Value;
                VerdantsAscent.Water.Core.FiresUnderwaterBubbles.Speed
                    = ConfigManager.Instance.configFiresWaterBubbleSpeed.Value;
                // Retired: UnderwaterFogDriver was the v1 fog-only path;
                // FiresUnderwaterEnv replaces it. Force the legacy driver
                // off in case anything else flipped it.
                VerdantsAscent.Water.Core.UnderwaterFogDriver.Enabled = false;

                // Push skybox-override values. Driver re-applies every
                // LateUpdate so a change to any slider takes visible effect
                // on the very next frame.
                VerdantsAscent.Water.Core.FiresSkyboxDriver.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configFiresSkyboxOverride.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AtmosphereThicknessMultiplier
                    = ConfigManager.Instance.configFiresSkyboxAtmosphereThicknessMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.FogHeightMinOffset
                    = ConfigManager.Instance.configFiresSkyboxFogHeightMinOffset.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.FogHeightMaxOffset
                    = ConfigManager.Instance.configFiresSkyboxFogHeightMaxOffset.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.GroundColorBlendToSky
                    = ConfigManager.Instance.configFiresSkyboxGroundColorBlendToSky.Value;

                // Real-zone water extension: keep more vanilla zone water loaded
                // around the player (the actual mesh, seamless). Gate-free; content
                // in the extra zones is suppressed by ZoneContentSuppressor.
                VerdantsAscent.Water.Core.OceanZoneRingExtender.Enabled
                    = ConfigManager.Instance.configOceanRingExtendEnabled.Value;
                VerdantsAscent.Water.Core.OceanZoneRingExtender.ExtendedActiveArea
                    = ConfigManager.Instance.configOceanRingExtendedActiveArea.Value;
                // Clipmap ocean (Crest-style cascade rings) - the ALTERNATIVE far
                // coverage. Single writer: the stand-down of the disk happens HERE,
                // not inside the clipmap, so toggling 09 UseClipmapOcean live just
                // reruns this method and the loser despawns on its next Tick. NOT
                // gated on MasterFx - the water stack is exempt, like the rest of
                // this block.
                bool useClipmapOcean = fx && ConfigManager.Instance.configUseClipmapOcean.Value;
                VerdantsAscent.Water.Core.FiresClipmapOcean.Enabled = useClipmapOcean;
                VerdantsAscent.Water.Core.FiresClipmapOcean.TotalRadiusMeters
                    = ConfigManager.Instance.configClipmapOceanRadius.Value;
                VerdantsAscent.Water.Core.FiresClipmapOcean.LodCount
                    = ConfigManager.Instance.configClipmapOceanLods.Value;
                VerdantsAscent.Water.Core.FiresClipmapOcean.PatchResolution
                    = ConfigManager.Instance.configClipmapOceanResolution.Value;
                VerdantsAscent.Water.Core.FiresClipmapOcean.InnerHoleRadius
                    = ConfigManager.Instance.configClipmapOceanInnerHole.Value;
                // Far-horizon FiresWater disk (fills past the real water) - forced
                // off while the clipmap owns the horizon (never both: same shader,
                // same queue band, they'd fight for the same pixels), restored the
                // moment the clipmap toggle goes back off.
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterExtendedRingEnabled.Value
                      && !useClipmapOcean;
                // Tie the disk radius to the dome radius (one slider) so they clamp
                // together and no water pokes past the planet in space view.
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.RadiusMeters
                    = ConfigManager.Instance.configFiresPlanetTieWaterToDome.Value
                        ? ConfigManager.Instance.configFiresPlanetDomeRadius.Value
                        : ConfigManager.Instance.configFiresWaterFollowRadius.Value;
                // Far-water wave detail + seam overlap (live retune, no rebuild needed).
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.WaveDetailDistance
                    = ConfigManager.Instance.configRingWaveDetailDistance.Value;
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.WaveDetailFloor
                    = ConfigManager.Instance.configRingWaveDetailFloor.Value;
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.SeamOverlapCells
                    = ConfigManager.Instance.configRingSeamOverlap.Value;
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.Retune();
                // AFTER the WaveDetail pushes above - the clipmap's material tuning
                // reads the same two statics, so sliders 03/04 drive both systems.
                VerdantsAscent.Water.Core.FiresClipmapOcean.Retune();

                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configFiresAtmosphereEnabled.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudCylinderRadiusMeters
                    = ConfigManager.Instance.configFiresAtmosphereCylinderRadius.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudCylinderHeightMeters
                    = ConfigManager.Instance.configFiresAtmosphereCylinderHeight.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudPlaneRadiusMeters
                    = ConfigManager.Instance.configFiresAtmosphereCloudPlaneRadius.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudCoverage
                    = ConfigManager.Instance.configFiresAtmosphereCloudCoverage.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudWorldScale
                    = ConfigManager.Instance.configFiresAtmosphereCloudWorldScale.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudSoftness
                    = ConfigManager.Instance.configFiresAtmosphereCloudSoftness.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudOpacity
                    = ConfigManager.Instance.configFiresAtmosphereCloudOpacity.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudDetailStrength
                    = ConfigManager.Instance.configFiresAtmosphereCloudDetailStrength.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudScrollSpeed
                    = ConfigManager.Instance.configFiresAtmosphereCloudScrollSpeed.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.CloudBrightness
                    = ConfigManager.Instance.configFiresAtmosphereCloudBrightness.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.AltitudeFadeStart
                    = ConfigManager.Instance.configFiresAtmosphereCloudAltFadeStart.Value;
                VerdantsAscent.Water.Core.FiresVanillaSkyTuner.AltitudeFadeEnd
                    = ConfigManager.Instance.configFiresAtmosphereCloudAltFadeEnd.Value;

                VerdantsAscent.Water.Core.FiresPlanet.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configFiresPlanetEnabled.Value;
                VerdantsAscent.Water.Core.FiresPlanet.OceanColor
                    = ConfigManager.Instance.configFiresPlanetOceanColor.Value;
                VerdantsAscent.Water.Core.FiresPlanet.SurfaceCloudOpacity
                    = ConfigManager.Instance.configFiresPlanetSurfaceCloudOpacity.Value;
                VerdantsAscent.Water.Core.FiresPlanet.CloudCoverage
                    = ConfigManager.Instance.configFiresPlanetCloudCoverage.Value;
                VerdantsAscent.Water.Core.FiresPlanet.CloudSoftness
                    = ConfigManager.Instance.configFiresPlanetCloudSoftness.Value;
                // 09 - FiresAtmosphere now drives the dome's OVERHEAD (ground-view)
                // clouds. Coverage sets the storm endpoint; clear weather floors at
                // ~30% of it; weather blends between them.
                VerdantsAscent.Water.Core.FiresPlanet.InsideCoverage
                    = ConfigManager.Instance.configFiresAtmosphereCloudCoverage.Value;
                VerdantsAscent.Water.Core.FiresPlanet.StormDarken
                    = ConfigManager.Instance.configStormDarken.Value;
                VerdantsAscent.Water.Core.FiresPlanet.StormCover
                    = ConfigManager.Instance.configStormCoverage.Value;
                VerdantsAscent.Water.Core.FiresPlanet.StormOpacity
                    = ConfigManager.Instance.configStormOpacity.Value;
                VerdantsAscent.Water.Core.FiresPlanet.ClearClouds
                    = ConfigManager.Instance.configFiresAtmosphereCloudCoverage.Value * 0.3f;
                VerdantsAscent.Water.Core.FiresPlanet.InsideSoftness
                    = ConfigManager.Instance.configFiresAtmosphereCloudSoftness.Value;
                VerdantsAscent.Water.Core.FiresPlanet.InsideOpacity
                    = ConfigManager.Instance.configFiresAtmosphereCloudOpacity.Value;
                VerdantsAscent.Water.Core.FiresPlanet.InsideScale
                    = ConfigManager.Instance.configFiresAtmosphereCloudWorldScale.Value;
                VerdantsAscent.Water.Core.FiresPlanet.InsideScroll
                    = 0.0002f * ConfigManager.Instance.configFiresAtmosphereCloudScrollSpeed.Value;
                VerdantsAscent.Water.Core.FiresPlanet.CloudDensityBoost
                    = ConfigManager.Instance.configFiresAtmosphereCloudDensity.Value;
                VerdantsAscent.Water.Core.FiresPlanet.InsideHue
                    = ConfigManager.Instance.configFiresAtmosphereCloudHue.Value;
                VerdantsAscent.Water.Core.FiresPlanet.CloudScale
                    = ConfigManager.Instance.configFiresPlanetCloudScale.Value;
                VerdantsAscent.Water.Core.FiresPlanet.ScrollSpeed
                    = ConfigManager.Instance.configFiresPlanetCloudScrollSpeed.Value;
                VerdantsAscent.Water.Core.FiresPlanet.DomeRadiusMeters
                    = ConfigManager.Instance.configFiresPlanetDomeRadius.Value;
                VerdantsAscent.Water.Core.FiresPlanet.MapOpacity
                    = ConfigManager.Instance.configFiresPlanetMapOpacity.Value;
                VerdantsAscent.Water.Core.FiresPlanet.Vibrance
                    = ConfigManager.Instance.configFiresPlanetVibrance.Value;
                VerdantsAscent.Water.Core.FiresPlanet.Hue
                    = ConfigManager.Instance.configFiresPlanetHue.Value;
                VerdantsAscent.Water.Core.FiresPlanet.HideWorldInSpaceView
                    = ConfigManager.Instance.configFiresPlanetHideWorld.Value;
                VerdantsAscent.Water.Core.FiresPlanet.DomeHeight
                    = ConfigManager.Instance.configFiresPlanetDomeHeight.Value;

                // Live owner of vanilla-LOD-water hiding (FiresWaterExtendedRing
                // SetActive(false)s the whole vanilla WaterPlane GameObject).
                VerdantsAscent.Water.Core.FiresWaterExtendedRing.HideLodWater
                    = fx && ConfigManager.Instance.configHideVanillaLodWater.Value;

                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowEnabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configAuroraGlowEnabled.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowIntensity
                    = ConfigManager.Instance.configAuroraGlowIntensity.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowSpeed
                    = ConfigManager.Instance.configAuroraGlowSpeed.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowAtmosphereMul
                    = ConfigManager.Instance.configAuroraGlowAtmosphereMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowFogHeightMin
                    = ConfigManager.Instance.configAuroraGlowFogHeightMin.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.AuroraGlowNightOnly
                    = ConfigManager.Instance.configAuroraNightOnly.Value;

                // FiresSkyboxAurora - the sky aurora (procedural skybox shader).
                // INDEPENDENT master toggle: section "11 - Aurora Skybox" owns
                // this and nothing else, so you can turn the sky aurora off by
                // itself. It is NO LONGER gated by Aurora Glow (12) - that toggle
                // now controls ONLY the horizon-glow path (FiresSkyboxDriver), so
                // the two can be isolated one at a time. Likewise Intensity is the
                // Overlay slider alone, not multiplied by the glow slider.
                VerdantsAscent.Water.Core.FiresSkyboxAurora.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configAuroraOverlayEnabled.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.Intensity
                    = ConfigManager.Instance.configAuroraOverlayIntensity.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.CycleSpeed
                    = ConfigManager.Instance.configAuroraGlowSpeed.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.NightOnly
                    = ConfigManager.Instance.configAuroraNightOnly.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.DuringStorms
                    = ConfigManager.Instance.configAuroraDuringStorms.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.NorthBiasPower
                    = ConfigManager.Instance.configAuroraNorthBiasPower.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.ContribCap
                    = ConfigManager.Instance.configAuroraContribCap.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.AltMin
                    = ConfigManager.Instance.configAuroraAltMin.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.AltMax
                    = ConfigManager.Instance.configAuroraAltMax.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.ColumnFreq
                    = ConfigManager.Instance.configAuroraColumnFreq.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.VertFreq
                    = ConfigManager.Instance.configAuroraVertFreq.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.Sharpness
                    = ConfigManager.Instance.configAuroraSharpness.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.ScrollSpeed
                    = ConfigManager.Instance.configAuroraScrollSpeed.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.PulseSpeed
                    = ConfigManager.Instance.configAuroraPulseSpeed.Value;

                // Vanilla skybox property multipliers
                VerdantsAscent.Water.Core.FiresSkyboxDriver.ExposureMultiplier
                    = ConfigManager.Instance.configFiresSkyboxExposureMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.StormSkyDarken
                    = ConfigManager.Instance.configFiresSkyboxStormDarken.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.StarExposureMultiplier
                    = ConfigManager.Instance.configFiresSkyboxStarExposureMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.SunSizeMultiplier
                    = ConfigManager.Instance.configFiresSkyboxSunSizeMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.MoonSizeMultiplier
                    = ConfigManager.Instance.configFiresSkyboxMoonSizeMul.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configSunHazeEnabled.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.ClearDiskBoost
                    = ConfigManager.Instance.configSunHazeClearDiskBoost.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.HazyDiskBoost
                    = ConfigManager.Instance.configSunHazeHazyDiskBoost.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.ClearSunSize
                    = ConfigManager.Instance.configSunHazeClearSunSize.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.HazySunSize
                    = ConfigManager.Instance.configSunHazeHazySunSize.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.LowSunElevation
                    = ConfigManager.Instance.configSunHazeLowSunElevation.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.LowSunWeight
                    = ConfigManager.Instance.configSunHazeLowSunWeight.Value;
                VerdantsAscent.Water.Core.FiresSunHaze.WeatherWeight
                    = ConfigManager.Instance.configSunHazeWeatherWeight.Value;

                VerdantsAscent.Water.Core.FiresSkyboxDriver.SunLightMultiplier
                    = ConfigManager.Instance.configFiresSkyboxSunLightMul.Value;
                VerdantsAscent.Water.Core.FiresSkyboxDriver.MoonLightMultiplier
                    = ConfigManager.Instance.configFiresSkyboxMoonLightMul.Value;

                // Aurora light + shaft pass
                VerdantsAscent.Water.Core.FiresAuroraLight.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configAuroraLightEnabled.Value;
                VerdantsAscent.Water.Core.FiresAuroraLight.Intensity
                    = ConfigManager.Instance.configAuroraLightIntensity.Value;
                VerdantsAscent.Water.Core.FiresAuroraLight.ElevationDeg
                    = ConfigManager.Instance.configAuroraLightElevation.Value;
                VerdantsAscent.Water.Core.FiresAuroraLight.AzimuthDeg
                    = ConfigManager.Instance.configAuroraLightAzimuth.Value;
                VerdantsAscent.Water.Core.FiresAuroraLight.NightOnly
                    = ConfigManager.Instance.configAuroraNightOnly.Value;
                VerdantsAscent.Water.Core.FiresAuroraLight.DuringStorms
                    = ConfigManager.Instance.configAuroraDuringStorms.Value;

                VerdantsAscent.Water.Core.FiresAuroraShaftsPass.Enabled
                    = ConfigManager.MasterFx && ConfigManager.Instance.configAuroraShaftsEnabled.Value;
                VerdantsAscent.Water.Core.FiresAuroraShaftsPass.Intensity
                    = ConfigManager.Instance.configAuroraShaftsIntensity.Value;
                VerdantsAscent.Water.Core.FiresAuroraShaftsPass.BlurRadius
                    = ConfigManager.Instance.configAuroraShaftsBlurRadius.Value;
                VerdantsAscent.Water.Core.FiresAuroraShaftsPass.MaxRadius
                    = ConfigManager.Instance.configAuroraShaftsMaxRadius.Value;

                // Day-sky tweaks
                VerdantsAscent.Water.Core.FiresSkyboxAurora.DaySkyTint
                    = ConfigManager.Instance.configDaySkyColor.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.DaySkyHue
                    = ConfigManager.Instance.configDaySkyHue.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.DaySkyDensity
                    = ConfigManager.Instance.configDaySkyDensity.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.StarFadeBySky
                    = ConfigManager.Instance.configStarFadeBySky.Value;
                VerdantsAscent.Water.Core.FiresSkyboxAurora.SunDiskBoost
                    = ConfigManager.Instance.configFiresSkyboxSunDiskBrightness.Value;

                // Apply turning ON or OFF immediately so the user sees
                // feedback when they flip the config. The retroactive
                // scan is cheap (a few materials at most).
                bool nowSwap = VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwapVanillaOcean;
                if (prevSwap && !nowSwap)
                {
                    VerdantsAscent.Water.Core.FiresWaterShaderLoader.RestoreVanillaWater();
                }
                else if (!prevSwap && nowSwap)
                {
                    VerdantsAscent.Water.Core.FiresWaterShaderLoader.ApplyToAllVanillaWater();
                }

                // FiresWater live surface tuning - foam layer balance, slope
                // foam, and the fresnel/reflection compression. All pushed as
                // shader globals below so they take effect in real time.
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.FoamIntensityA
                    = ConfigManager.Instance.configFiresWaterFoamLayerA.Value;
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.FoamIntensityB
                    = ConfigManager.Instance.configFiresWaterFoamLayerB.Value;
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.FoamWaveHeightDriven
                    = ConfigManager.Instance.configFiresWaterFoamWaveHeight.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.FoamSlopeDriven
                    = ConfigManager.Instance.configFiresWaterFoamSlope.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.ReflectionFloor
                    = ConfigManager.Instance.configFiresWaterReflectionFloor.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.ReflectionMax
                    = ConfigManager.Instance.configFiresWaterReflectionMax.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.FresnelPower
                    = ConfigManager.Instance.configFiresWaterFresnelSoftness.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.WaveOpacity
                    = ConfigManager.Instance.configFiresWaterWaveOpacity.Value;
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.FoamSpeed
                    = ConfigManager.Instance.configFiresWaterFoamScrollSpeed.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SurfaceCausticsEnabled
                    = ConfigManager.Instance.configFiresWaterSurfaceCausticsEnabled.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.CausticStrength
                    = ConfigManager.Instance.configFiresWaterCausticStrength.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.CausticScale
                    = ConfigManager.Instance.configFiresWaterCausticScale.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.CausticSpeed
                    = ConfigManager.Instance.configFiresWaterCausticSpeed.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.CausticWarp
                    = ConfigManager.Instance.configFiresWaterCausticWarp.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.CausticSharp
                    = ConfigManager.Instance.configFiresWaterCausticSharp.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.NormalSpeed
                    = ConfigManager.Instance.configFiresWaterNormalSpeed.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.FoamSunBoost
                    = ConfigManager.Instance.configFiresWaterFoamSunBoost.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UWCausticScale
                    = ConfigManager.Instance.configFiresWaterUWCausticScale.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UWCausticSharp
                    = ConfigManager.Instance.configFiresWaterUWCausticSharpness.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UWCausticStrength
                    = ConfigManager.Instance.configFiresWaterUWCausticStrength.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UWCausticSpeed
                    = ConfigManager.Instance.configFiresWaterUWCausticSpeed.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UWCausticWarp
                    = ConfigManager.Instance.configFiresWaterUWCausticWarp.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.UnderwaterDappleEnabled
                    = ConfigManager.Instance.configFiresWaterUnderwaterDapple.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SurfaceFogAmount
                    = ConfigManager.Instance.configFiresWaterSurfaceFog.Value;
                VerdantsAscent.Water.Core.FiresMeniscusPass.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterMeniscusEnabled.Value;
                VerdantsAscent.Water.Core.FiresMeniscusPass.Strength
                    = ConfigManager.Instance.configFiresWaterMeniscusStrength.Value;
                VerdantsAscent.Water.Core.FiresMeniscusPass.Width
                    = ConfigManager.Instance.configFiresWaterMeniscusWidth.Value;
                VerdantsAscent.Water.Core.FiresShipWaterMaskFit.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterShipMaskFit.Value;
                VerdantsAscent.Water.Core.FiresShipWaterMaskFit.ExtendMargin
                    = ConfigManager.Instance.configFiresWaterShipMaskMargin.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterPlanarRefl.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.Resolution
                    = ConfigManager.Instance.configFiresWaterPlanarReflResolution.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.BankClip
                    = ConfigManager.Instance.configFiresWaterPlanarReflBankClip.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.Distort
                    = ConfigManager.Instance.configFiresWaterPlanarReflDistort.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.MaxHeightAboveWater
                    = ConfigManager.Instance.configFiresWaterPlanarReflMaxHeight.Value;
                VerdantsAscent.Water.Core.FiresPlanarReflection.ReflectTerrain
                    = ConfigManager.Instance.configFiresWaterPlanarReflTerrain.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SSSColor
                    = ConfigManager.Instance.configFiresWaterSSSColor.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SSSBase
                    = ConfigManager.Instance.configFiresWaterSSSBase.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SSSSun
                    = ConfigManager.Instance.configFiresWaterSSSSun.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SSSSunFalloff
                    = ConfigManager.Instance.configFiresWaterSSSSunFalloff.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SSSWaveBoost
                    = ConfigManager.Instance.configFiresWaterSSSWaveBoost.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.AbsorbBlend
                    = ConfigManager.Instance.configFiresWaterAbsorbBlend.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.AbsorbExtinction
                    = ConfigManager.Instance.configFiresWaterAbsorbExtinction.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.FoamBubbleIntensity
                    = ConfigManager.Instance.configFiresWaterFoamBubbleIntensity.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.DistNormalFade
                    = ConfigManager.Instance.configFiresWaterDistNormalFade.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.DistSpecRough
                    = ConfigManager.Instance.configFiresWaterDistSpecRough.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SchlickBlend
                    = ConfigManager.Instance.configFiresWaterSchlickFresnel.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.NadirReflCap
                    = ConfigManager.Instance.configFiresWaterNadirReflCap.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.PixelDepth
                    = ConfigManager.Instance.configFiresWaterPixelDepth.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.SunSpecScale
                    = ConfigManager.Instance.configFiresWaterSunSpec.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.WaveBandLimit
                    = ConfigManager.Instance.configFiresWaterWaveBandLimit.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.DistantReflect
                    = ConfigManager.Instance.configDistantReflection.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.DistantTint
                    = ConfigManager.Instance.configDistantTint.Value;
                VerdantsAscent.Water.Core.FiresWaterTuning.DistantBright
                    = ConfigManager.Instance.configDistantBrightness.Value;

                // Stage 2 below-water caustics (read directly by FiresCausticsPass).
                VerdantsAscent.Water.Core.FiresCausticsPass.Enabled
                    = fx && ConfigManager.Instance.configFiresWaterBelowWaterCaustics.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.Shadow
                    = ConfigManager.Instance.configFiresWaterCausticShadow.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.DepthFade
                    = ConfigManager.Instance.configFiresWaterCausticDepthFade.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.DebugView
                    = ConfigManager.Instance.configFiresWaterCausticDebug.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.Strength
                    = ConfigManager.Instance.configFiresWaterCausticPassStrength.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.MoonScale
                    = ConfigManager.Instance.configFiresWaterCausticMoonScale.Value;
                VerdantsAscent.Water.Core.FiresCausticsPass.Radius
                    = ConfigManager.Instance.configFiresWaterCausticRadius.Value;
                VerdantsAscent.Water.Core.FiresFogControl.Active = ConfigManager.MasterFx;
                VerdantsAscent.Water.Core.FiresFogControl.VanillaFogEnabled
                    = ConfigManager.Instance.configDistanceFogEnabled.Value;
                VerdantsAscent.Water.Core.FiresFogControl.DensityScale
                    = ConfigManager.Instance.configDistanceFogDensityScale.Value;
                VerdantsAscent.Water.Core.FiresFogControl.ColorOverride
                    = ConfigManager.Instance.configDistanceFogColorOverride.Value;
                VerdantsAscent.Water.Core.FiresFogControl.Color
                    = ConfigManager.Instance.configDistanceFogColor.Value;
                VerdantsAscent.Water.Core.FiresFogControl.Mode
                    = ConfigManager.Instance.configDistanceFogMode.Value;
                VerdantsAscent.Water.Core.FiresFogControl.LinearStart
                    = ConfigManager.Instance.configDistanceFogLinearStart.Value;
                VerdantsAscent.Water.Core.FiresFogControl.LinearEnd
                    = ConfigManager.Instance.configDistanceFogLinearEnd.Value;

                // Volumetric fog - master + 3 zones (land / sea / underwater), all live.
                var vf = ConfigManager.Instance;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.Enabled = ConfigManager.MasterFx && vf.configVolFogEnabled.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.DomainConfine = vf.configVolFogDomainConfine.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SkyBlend = vf.configVolFogSkyBlend.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CaveAware = vf.configVolFogCaveAware.Value;   // 0.3.154
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CaveFadeSeconds = vf.configVolFogCaveFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.PerObjectUnderwaterScale = vf.configVolFogUwPerObject.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CaveRenderFog = vf.configVolFogCaveRender.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CaveFlipHoldSeconds = vf.configVolFogCaveHold.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftOn = vf.configVfShaftOn.Value;   // 0.3.155
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftColor = vf.configVfShaftColor.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftDensity = vf.configVfShaftDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftMaxOp = vf.configVfShaftMaxOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftReach = vf.configVfShaftReach.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftTopGap = vf.configVfShaftTopGap.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.ShaftVFade = vf.configVfShaftVFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandOn = vf.configVfLandOn.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandColor = vf.configVfLandColor.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandColorThin = vf.configVfLandColorThin.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandMin = vf.configVfLandMin.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandMax = vf.configVfLandMax.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandBotFade = vf.configVfLandBotFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandTopFade = vf.configVfLandTopFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandDensity = vf.configVfLandDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandMaxOp = vf.configVfLandMaxOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandPatchScale = vf.configVfLandPatchScale.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandPatch = vf.configVfLandPatch.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandCoverage = vf.configVfLandCoverage.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandClearOp = vf.configVfLandClearOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandPatchFade = vf.configVfLandPatchFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandWind = vf.configVfLandWind.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandFarCut = vf.configVfLandFarCut.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.LandUndulate = vf.configVfLandUndulate.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaOn = vf.configVfSeaOn.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaColor = vf.configVfSeaColor.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaColorThin = vf.configVfSeaColorThin.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaBelow = vf.configVfSeaBelow.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaThick = vf.configVfSeaThick.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaBotFade = vf.configVfSeaBotFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaTopFade = vf.configVfSeaTopFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaDensity = vf.configVfSeaDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaMaxOp = vf.configVfSeaMaxOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaPatchScale = vf.configVfSeaPatchScale.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaPatch = vf.configVfSeaPatch.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaCoverage = vf.configVfSeaCoverage.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaClearOp = vf.configVfSeaClearOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaPatchFade = vf.configVfSeaPatchFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaWind = vf.configVfSeaWind.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaFarCut = vf.configVfSeaFarCut.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.SeaUndulate = vf.configVfSeaUndulate.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwOn = vf.configVfUwOn.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwColor = vf.configVfUwColor.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwDensity = vf.configVfUwDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwMaxOp = vf.configVfUwMaxOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwPatchScale = vf.configVfUwPatchScale.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwSwirl = vf.configVfUwSwirl.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwCurrent = vf.configVfUwCurrent.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwDistStart = vf.configVfUwDistStart.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwDistRange = vf.configVfUwDistRange.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwFarCut = vf.configVfUwFarCut.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwColorThin = vf.configVfUwColorThin.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwCoverage = vf.configVfUwCoverage.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwPatch = vf.configVfUwPatch.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwClearOp = vf.configVfUwClearOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwPatchFade = vf.configVfUwPatchFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwDepthDensity = vf.configVfUwDepthDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.UwDepthSpan = vf.configVfUwDepthSpan.Value;

                VerdantsAscent.Water.Core.FiresVolumetricFogPass.PushOn = vf.configVfPushOn.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.PushRadius = vf.configVfPushRadius.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.PushStrength = vf.configVfPushStrength.Value;

                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudOn = vf.configVfCloudOn.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudColor = vf.configVfCloudColor.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudMin = vf.configVfCloudMin.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudMax = vf.configVfCloudMax.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudBotFade = vf.configVfCloudBotFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudTopFade = vf.configVfCloudTopFade.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudDensity = vf.configVfCloudDensity.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudMaxOp = vf.configVfCloudMaxOp.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudCoverage = vf.configVfCloudCoverage.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudPatch = vf.configVfCloudPatch.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudPatchScale = vf.configVfCloudPatchScale.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudUndulate = vf.configVfCloudUndulate.Value;
                VerdantsAscent.Water.Core.FiresVolumetricFogPass.CloudDrift = vf.configVfCloudDrift.Value;

                VerdantsAscent.Water.Core.FiresFogEnvReactive.Enabled = vf.configVfEnvReactive.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.ColorBlend = vf.configVfEnvColorBlend.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.DensityBlend = vf.configVfEnvDensityBlend.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.RainBoost = vf.configVfEnvRainBoost.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.StormOpacity = vf.configVfEnvStormOpacity.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.AffectUnderwater = vf.configVfEnvAffectUw.Value;
                VerdantsAscent.Water.Core.FiresFogEnvReactive.ClearDayFog = vf.configVfClearDayFog.Value;

                // Shore field - shore-ramped waves + depth-darkened water.
                VerdantsAscent.Water.Core.FiresShoreField.Enabled = vf.configShoreFieldEnabled.Value;
                VerdantsAscent.Water.Core.FiresShoreField.WaveShoreFloor = vf.configShoreWaveFloor.Value;
                VerdantsAscent.Water.Core.FiresShoreField.WaveFullDepth = vf.configShoreWaveFullDepth.Value;
                VerdantsAscent.Water.Core.FiresShoreField.DepthDarkness = vf.configShoreDepthDarkness.Value;
                VerdantsAscent.Water.Core.FiresShoreField.DepthForDark = vf.configShoreDepthForDark.Value;
                VerdantsAscent.Water.Core.FiresShoreField.Extent = vf.configShoreFieldExtent.Value;
                VerdantsAscent.Water.Core.FiresShoreField.Resolution = vf.configShoreFieldResolution.Value;
                VerdantsAscent.Water.Core.FiresShoreField.FogDomainBlend = vf.configShoreFogDomainBlend.Value;
                VerdantsAscent.Water.Core.FiresShoreField.RequestRebake();   // re-bake so ramp/depth changes show now

                // Push shared foam globals - every water material reads these
                // instead of per-material foam props, so foam pattern + mask
                // are identical at every shared world position. Eliminates
                // the zone-boundary foam seams.
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.Push();
                VerdantsAscent.Water.Core.FiresWaterTuning.Push();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] ApplyFiresWaterConfig failed: {ex.Message}");
            }
        }

        // Wait for ZNetScene to be live before the retroactive water swap -
        // before that point WaterVolume.Instances and ZNetScene.m_prefabs
        // are both empty, so the scan would find nothing.
        private System.Collections.IEnumerator FiresWaterApplyWhenReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try
            {
                int n = VerdantsAscent.Water.Core.FiresWaterShaderLoader.ApplyToAllVanillaWater();
                if (n > 0 && FiresLogger.VerboseEnabled)
                    Debug.Log($"[{PluginName}] FiresWater initial vanilla swap touched {n} material(s).");

                // Foam globals must be in place before any water material renders
                // so the first frame already reads shared values, not per-material.
                VerdantsAscent.Water.Core.FiresWaterFoamGlobals.Push();
                VerdantsAscent.Water.Core.FiresWaterTuning.Push();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] FiresWaterApplyWhenReady scan failed: {ex.Message}");
            }
        }

        private void InstallLogFilter()
        {
            // FiresUnifiedCore installs the one shared rate-limit/suppression log handler now
            // (FiresCore.Logging.RateLimitedLogHandler). This per-mod copy was collapsed into Core so the handler
            // chain stays a single wrapper instead of one-per-mod - intentionally a no-op.
        }

        private IEnumerator DelayedDedicatedServerInit()
        {
            while (ZNetScene.instance == null || ObjectDB.instance == null)
                yield return null;
            yield return new WaitForEndOfFrame();
            FinishInit();
        }

        // Patch classes whose method bodies reference client-only Unity types
        // (Hud, Minimap, InventoryGui, FejdStartup, etc.). On a dedicated
        // server the Mono runtime crashes natively when Harmony's IL rewriter
        // tries to resolve those references. Same pattern as Ascend.cs in the
        // original FiresRPGmaker mod.
        private static readonly HashSet<string> _clientOnlyPatchTypes = new HashSet<string>
        {
            // Phase F: legacy DirtFloorClientPatches entry removed (the
            // DirtFloorPatches type is deleted). Add new client-only patch
            // type names here as we identify them on the dedi path.
        };

        private static bool IsClientOnlyPatch(Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
            {
                if (_clientOnlyPatchTypes.Contains(t.FullName))
                    return true;
            }
            return false;
        }

        private void FinishInit()
        {
            bool isDedicatedServer = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

            try
            {
                if (!isDedicatedServer)
                {
                    harmony.PatchAll();
                }
                else
                {
                    int patched = 0;
                    int skipped = 0;
                    var assembly = Assembly.GetExecutingAssembly();

                    foreach (var type in assembly.GetTypes())
                    {
                        if (IsClientOnlyPatch(type))
                        {
                            skipped++;
                            continue;
                        }

                        try
                        {
                            harmony.CreateClassProcessor(type).Patch();
                            patched++;
                        }
                        catch (Exception ex)
                        {
                            if (type.GetCustomAttribute<HarmonyPatch>() != null ||
                                type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0)
                            {
                                Debug.LogWarning($"[{PluginName}] Failed to apply Harmony patch {type.FullName}: {ex.Message}");
                            }
                        }
                    }

                    Debug.Log($"[{PluginName}] Dedicated server: patched {patched} types, skipped {skipped} client-only types");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{PluginName}] harmony.PatchAll threw an exception: {ex}");
            }

            // Initialize the legacy biome / paint / terrain RPC systems that
            // were ported verbatim from RPG-maker. The [HarmonyPatch] classes
            // inside these files auto-register via harmony.PatchAll above, but
            // their RPC handlers + console commands + file-cache loaders need
            // explicit Initialize calls.
            //
            // Without these:
            //   * TerrainPaintRPC's biome/clutter/paint RPCs aren't registered,
            //     so MP biome sync silently no-ops on the receiver side.
            //   * BiomeFilePersistence's on-disk biome-paint cache is never
            //     loaded, so biome paints on vanilla terrain don't survive
            //     world reload.
            //   * BiomeOverlayPainter's paint_lava / paint_snow / paint_biome
            //     console commands aren't registered.
            //
            // Phase F: legacy TerrainPaintRPC removed. RPC sync now flows
            // through TileBrushRPC (tile ops) + RegionScrubberRPC (clear).

            try { VerdantsAscent.Utilities.FtsHelpContent.Register(); }
            catch (Exception ex) { Debug.LogWarning($"[{PluginName}] Help registration failed: {ex.Message}"); }

            // Compact "ready" banner + SHADERS box - deferred to WORLD
            // LOAD time (when ZNetScene is up) so the shader loaders have
            // real counts to report. The BIG "loading" banner already fired
            // at the top of Awake; the compact one bookends the load once
            // the world is actually live.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Waits for ZNetScene + prefab table to be live, then emits the
        // SHADERS load summary followed by the compact brand banner.
        // Failure is non-fatal - falls back to the plain "mod loaded" log
        // so the load event is still recorded.
        private System.Collections.IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try
            {
                FiresCore.Logging.LoadSummary.For(PluginName).EmitMiniBox("\uD83C\uDF0A SHADERS", new[]
                {
                    $"water mats swapped: {VerdantsAscent.Water.Core.FiresWaterShaderLoader.SwappedMaterialCount}",
                    $"sky/aurora: {(VerdantsAscent.Water.Core.FiresSkyboxAurora.AuroraShader != null ? "ok" : "off")}",
                    $"planet dome: {(VerdantsAscent.Water.Core.FiresPlanet.ShaderLoaded ? "ok" : "off")}",
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{PluginName}] SHADERS summary failed: {ex.Message}");
            }

            try { FiresTossinShadeBanner.Print(); }
            catch (Exception ex)
            {
                Debug.Log($"[{PluginName}] mod loaded. (banner failed: {ex.Message})");
            }
        }

        private void OnDestroy()
        {
            if (RootObject != null)
            {
                UnityEngine.Object.Destroy(RootObject);
                RootObject = null;
            }

            try { harmony?.UnpatchSelf(); } catch { }

            try { ConfigManager.Instance?.Dispose(); } catch { }

            configSync = null;
            Instance = null;
        }

        // Bundle loading removed: the FAT-clone left a vestigial `firesnpcs` asset-bundle
        // loader here, but FTS ships no `firesnpcs` (only the `tossinshade` SHADER bundle,
        // mounted by FiresWaterShaderLoader) and nothing ever LoadAsset'd it. Attempting to
        // load `firesnpcs` would have collided with FiresRPGMaker's real `firesnpcs` bundle
        // ("same files already loaded"), so it's gone entirely.
    }
}
