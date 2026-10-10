using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Rescales vanilla atmosphere geometry so it wraps OUTSIDE our gameplay
    /// volume instead of cutting through it, AND (when the FiresAtmosphereDome
    /// shader is bundled) swaps the vanilla Custom/SkyObject + Custom/Clouds
    /// shaders for our refraction-free replacement. Targets:
    ///
    ///   1. _GameMain/_Environment/CloudCylinder/cylinder
    ///      Vanilla: radius 2000m, height 2000m. Custom/SkyObject swirl with
    ///      screen-space GrabPass refraction — root cause of the "looking
    ///      through the backside of a shader" band on distant terrain.
    ///   2. _GameMain/_Environment/Clouds/offset/cloud_plane_upper_downside
    ///      Vanilla: radius 1200m, EnvMan.m_clouds. Custom/Clouds dome cap
    ///      visible from above when teleported to moon altitude.
    ///   3/4. cloud_plane_rain + cloud_plane_rain_downside variants — rescaled
    ///      + shader-swapped together so storms look consistent.
    ///
    /// Math (from va_sky_dump 2026-06-01):
    ///   Cylinder bounds.x = 4 * scale.x → scale.x = TargetRadius / 2
    ///   Cylinder bounds.y = 4 * scale.y → scale.y = TargetHeight / 4
    ///   Plane    bounds.x = 2 * scale.x → scale.x = TargetRadius
    ///   (Plane scale.y left alone — vanilla 1500, mesh is thin.)
    /// </summary>
    public static class FiresVanillaSkyTuner
    {
        public static bool Enabled = true;
        // When the dome is in vanilla-clouds mode (FiresPlanet.UseVanillaCloudPlane),
        // the user wants Valheim's own cloud layer untouched — so we must NOT blow
        // the cloud cylinder up to atmosphere-bubble size, or you end up looking at
        // the inside wall of a giant scaled cylinder. True → restore vanilla scales.
        public static bool DeferToVanillaClouds = false;
        // Set by FiresPlanet when the camera climbs above the dome top (space view).
        // The cloud cylinder follows the camera, so up there you'd be standing
        // inside it looking at its inner wall — a giant cyan blob among the stars.
        // True → deactivate the cylinder entirely until you drop back under the dome.
        public static bool HideForSpace = false;
        public static float CloudCylinderRadiusMeters = 3000f;
        public static float CloudCylinderHeightMeters = 3000f;
        public static float CloudPlaneRadiusMeters = 3000f;
        // FiresClouds renders as the pink error shader in-build (variant stripped
        // from the bundle), so keep the cylinder on the vanilla shader for now —
        // it looked fine and the planet dome (FiresPlanet) is what matters here.
        // Flip back on once FiresClouds' bundle variant is sorted.
        public static bool UseCustomShader = false;

        private const string CustomShaderName = "Custom/FiresClouds";
        private const string CustomShaderAsset = "FiresClouds";   // bundle asset name (Assets/Shaders/Fires/FiresClouds.shader)

        // Our cloud art (greyscale, tileable, white-on-black). On the shader
        // swap the vanilla `_MainTex` (cloud) carries over, so if these aren't
        // in the bundle the clouds fall back to vanilla art automatically.
        private const string CloudTexName = "CloudTextFire1";
        private const string CloudDetailTexName = "CloudTextFire2";
        public static float AltitudeFadeStart = 1500f;   // clouds start fading above this Y
        public static float AltitudeFadeEnd = 2600f;   // ...fully gone here (kills the moon box)

        // Live-tunable cloud look (config-driven; re-applied to the materials
        // each tick so slider changes show within ~1s, no bundle rebuild).
        public static float CloudCoverage = 0.45f;   // more = more sky covered
        public static float CloudWorldScale = 0.0003f; // bigger = smaller, denser cloud cells
        public static float CloudSoftness = 0.25f;   // edge feather
        public static float CloudOpacity = 1.0f;
        public static float CloudDetailStrength = 0.5f;
        public static float CloudScrollSpeed = 1.0f;
        public static float CloudBrightness = 1.0f;
        private static Texture s_cloudTex, s_cloudDetailTex;
        private static bool s_loggedCloudTex;
        private static readonly int s_propAltFade = Shader.PropertyToID("_FiresCloudAltFade");
        private static readonly int s_propRain = Shader.PropertyToID("_FiresCloudRain");

        private const float CylinderMeshDiameterLocal = 4f;
        private const float CylinderMeshHeightLocal = 4f;
        private const float PlaneMeshDiameterLocal = 2f;

        private static GameObject s_cloudCylinder;
        private static GameObject s_cloudPlane;
        private static GameObject s_cloudPlaneRain;
        private static GameObject s_cloudPlaneRainDownside;

        private static Vector3 s_cylinderVanillaScale = new Vector3(1000f, 500f, 1000f);
        private static Vector3 s_planeVanillaScale = new Vector3(1200f, 1500f, 1200f);
        private static bool s_capturedVanillaScales;
        private static bool s_loggedTune;

        private static Shader s_customShader;
        private static bool s_attemptedShaderLoad;
        private static bool s_loggedShaderState;

        // Per-renderer shader-swap bookkeeping. Vanilla materials are RESHADERED
        // in place — we re-write material.shader on the existing material so
        // the property table (incl. _MainTex / _Color / _Speed) carries over by
        // name. We also stash the original shader so RestoreAll can put it
        // back if the driver is disabled.
        private static readonly System.Collections.Generic.Dictionary<int, Shader> s_originalShaders
            = new System.Collections.Generic.Dictionary<int, Shader>();

        public static void Tick()
        {
            if (!Enabled) return;

            ResolveOnce();
            CaptureVanillaScalesOnce();

            // Space view (camera above the dome): kill the cylinder so it isn't a
            // cyan blob wrapping the camera among the stars. Re-activate on return.
            if (HideForSpace)
            {
                if (s_cloudCylinder != null && s_cloudCylinder.activeSelf) s_cloudCylinder.SetActive(false);
                return;
            }
            if (s_cloudCylinder != null && !s_cloudCylinder.activeSelf) s_cloudCylinder.SetActive(true);

            // Vanilla-clouds mode: keep the cloud cylinder at its native size +
            // shader so it reads as Valheim's normal far clouds, not a giant
            // scaled wall. (Cheap — ApplyScale no-ops once it's already vanilla.)
            if (DeferToVanillaClouds) { RestoreAll(); return; }

            Vector3 cylinderScale = new Vector3(
                CloudCylinderRadiusMeters / (CylinderMeshDiameterLocal * 0.5f),
                CloudCylinderHeightMeters / CylinderMeshHeightLocal,
                CloudCylinderRadiusMeters / (CylinderMeshDiameterLocal * 0.5f));
            ApplyScale(s_cloudCylinder, cylinderScale);

            // The cloud DOME (cloud_plane*) is now owned by FiresPlanet (planet
            // from above + weather clouds from inside; it rescales + hides the
            // rain variants itself). We keep only the far swirl cylinder here.
            if (UseCustomShader)
            {
                TryLoadCustomShader();
                if (s_customShader != null)
                    ApplyCloudShader(s_cloudCylinder);
            }

            if (!s_loggedTune && s_cloudCylinder != null && s_cloudPlane != null)
            {
                FiresLogger.LogVerbose($"[FiresVanillaSkyTuner] Atmosphere bubble: " +
                          $"cylinder R={CloudCylinderRadiusMeters:0}m H={CloudCylinderHeightMeters:0}m, " +
                          $"cloud plane R={CloudPlaneRadiusMeters:0}m, " +
                          $"customShader={(s_customShader != null ? "ON" : "OFF — bundle missing it")}.");
                s_loggedTune = true;
            }
        }

        public static void RestoreAll()
        {
            if (s_capturedVanillaScales)
            {
                ApplyScale(s_cloudCylinder, s_cylinderVanillaScale);
                ApplyScale(s_cloudPlane, s_planeVanillaScale);
                ApplyScale(s_cloudPlaneRain, s_planeVanillaScale);
                ApplyScale(s_cloudPlaneRainDownside, s_planeVanillaScale);
            }
            RestoreOriginalShaders();
        }

        private static void ResolveOnce()
        {
            if (s_cloudCylinder == null)
                s_cloudCylinder = GameObject.Find("_GameMain/_Environment/CloudCylinder/cylinder");
            if (s_cloudPlane == null)
                s_cloudPlane = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_upper_downside");
            if (s_cloudPlaneRain == null)
                s_cloudPlaneRain = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain");
            if (s_cloudPlaneRainDownside == null)
                s_cloudPlaneRainDownside = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain_downside");
        }

        private static void CaptureVanillaScalesOnce()
        {
            if (s_capturedVanillaScales) return;
            if (s_cloudCylinder != null) s_cylinderVanillaScale = s_cloudCylinder.transform.localScale;
            if (s_cloudPlane != null) s_planeVanillaScale = s_cloudPlane.transform.localScale;
            if (s_cloudCylinder != null && s_cloudPlane != null) s_capturedVanillaScales = true;
        }

        private static void ApplyScale(GameObject go, Vector3 scale)
        {
            if (go == null) return;
            if (go.transform.localScale != scale)
                go.transform.localScale = scale;
        }

        // === Shader-swap path ===
        //
        // The bundle is loaded by FiresWaterShaderLoader (shared across the
        // mod). Once that's run, Shader.Find resolves bundled shaders by name.
        // If the user hasn't rebuilt the bundle yet, Find returns null and we
        // silently fall back to the vanilla materials with just the rescale.

        private static void TryLoadCustomShader()
        {
            if (s_customShader != null) return;

            // Load straight from the bundle — Shader.Find can't see a bundled
            // shader until it's LoadAsset'd. Retries each tick until the bundle
            // is mounted (then resolves immediately).
            s_customShader = FiresWaterShaderLoader.LoadBundleShader(CustomShaderAsset, CustomShaderName);
            s_attemptedShaderLoad = true;
            if (s_customShader == null && FiresWaterShaderLoader.Bundle == null) return;

            if (!s_loggedShaderState)
            {
                if (s_customShader == null)
                {
                    Debug.LogWarning($"[FiresVanillaSkyTuner] '{CustomShaderName}' not in bundle yet — " +
                                     $"rebuild the AssetBundle from Unity to enable the shader swap. " +
                                     $"Continuing with rescaled vanilla materials only.");
                }
                else
                {
                    FiresLogger.LogVerbose($"[FiresVanillaSkyTuner] Loaded '{CustomShaderName}' from bundle, " +
                              $"will swap onto cloud cylinder + plane.");
                }
                s_loggedShaderState = true;
            }
        }

        private static void ApplyCloudShader(GameObject go)
        {
            if (go == null || s_customShader == null) return;
            var r = go.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return;
            var mat = r.sharedMaterial;
            if (mat.shader != s_customShader) { StashOriginal(mat); mat.shader = s_customShader; }
            ApplyCloudProps(mat);   // re-apply from config each tick for live tuning
        }

        private static void StashOriginal(Material mat)
        {
            int id = mat.GetInstanceID();
            if (!s_originalShaders.ContainsKey(id))
                s_originalShaders[id] = mat.shader;
        }

        private static void RestoreOriginalShaders()
        {
            foreach (var kv in s_originalShaders)
            {
                // We can't look up Material by instance ID directly without an
                // alloc — skip restore on disable; vanilla shader assets are
                // still in memory and a scene reload re-binds them. This is
                // a best-effort path; the typical lifecycle is mod-runtime only.
                _ = kv;
            }
            s_originalShaders.Clear();
        }

        // === Per-material tuning ===
        //
        // Both materials get the same shader but different property values so
        // the cylinder swirls horizontally (wrap-around motion) and the cloud
        // plane drifts more slowly with thicker opacity. Property NAMES that
        // already exist on the vanilla materials (_MainTex, _Color, _Speed,
        // _UVScale, _Opacity, _Darkening) are preserved by Unity when we swap
        // the shader, so we only WRITE the ones we want to override.

        // Re-applied to every cloud material each tick, so config slider changes
        // show live (no bundle rebuild). Cylinder + planes share one look for v1.
        private static void ApplyCloudProps(Material mat)
        {
            ApplyCloudTextures(mat);
            mat.SetFloat("_WorldScale", CloudWorldScale);
            mat.SetFloat("_DetailScale", 3.7f);
            mat.SetFloat("_DetailStrength", CloudDetailStrength);
            mat.SetFloat("_Coverage", CloudCoverage);
            mat.SetFloat("_Softness", CloudSoftness);
            mat.SetFloat("_Opacity", CloudOpacity);
            mat.SetFloat("_ScrollMul", 0.0001f * CloudScrollSpeed);
            mat.SetColor("_Color", new Color(CloudBrightness, CloudBrightness, CloudBrightness, 1f));
        }

        // Our greyscale cloud art if bundled; otherwise the vanilla _MainTex
        // that carried over on the shader swap stays as the fallback.
        private static void ApplyCloudTextures(Material mat)
        {
            EnsureCloudTextures();
            if (s_cloudTex != null) mat.SetTexture("_MainTex", s_cloudTex);
            mat.SetTexture("_DetailTex", s_cloudDetailTex != null ? s_cloudDetailTex : mat.GetTexture("_MainTex"));
        }

        private static void EnsureCloudTextures()
        {
            if (s_cloudTex != null || s_loggedCloudTex) return;
            var bundle = FiresWaterShaderLoader.Bundle;
            if (bundle == null) return;   // bundle not up yet — retry next call
            s_cloudTex = bundle.LoadAsset<Texture2D>(CloudTexName);
            s_cloudDetailTex = bundle.LoadAsset<Texture2D>(CloudDetailTexName);
            FiresLogger.LogVerbose($"[FiresVanillaSkyTuner] Cloud art: main='{(s_cloudTex != null ? CloudTexName : "VANILLA fallback")}', "
                    + $"detail='{(s_cloudDetailTex != null ? CloudDetailTexName : "reuse main")}'.");
            s_loggedCloudTex = true;
        }

        // Globals consumed by Custom/FiresClouds: altitude fade (so the cloud
        // layer dissolves as you fly up to the moon instead of boxing around it)
        // + the rain blend from the active env.
        public static void DriveGlobals()
        {
            var player = Player.m_localPlayer;
            float y = player != null ? player.transform.position.y : 0f;
            Shader.SetGlobalFloat(s_propAltFade, 1f - Mathf.SmoothStep(AltitudeFadeStart, AltitudeFadeEnd, y));

            float rain = 0f;
            if (EnvMan.instance != null)
            {
                var env = EnvMan.instance.GetCurrentEnvironment();
                if (env != null) rain = env.m_rainCloudAlpha;
            }
            Shader.SetGlobalFloat(s_propRain, rain);
        }
    }

    public class FiresVanillaSkyTunerDriver : MonoBehaviour
    {
        private const float CheckIntervalSec = 1f;
        private float m_nextCheckTime;
        private bool m_restoredWhileDisabled;

        private void Update()
        {
            // When the master toggle is off, REVERT the cloud cylinder + planes to
            // vanilla scales/shaders ONCE (not just stop updating — otherwise the
            // last-applied 3000m scale would linger and "off" wouldn't look off).
            if (!FiresVanillaSkyTuner.Enabled)
            {
                if (!m_restoredWhileDisabled)
                {
                    FiresVanillaSkyTuner.RestoreAll();
                    m_restoredWhileDisabled = true;
                }
                return;
            }
            m_restoredWhileDisabled = false;

            FiresVanillaSkyTuner.DriveGlobals();   // every frame — smooth altitude fade + rain
            if (Time.unscaledTime < m_nextCheckTime) return;
            m_nextCheckTime = Time.unscaledTime + CheckIntervalSec;
            FiresVanillaSkyTuner.Tick();
        }

        private void OnDisable() { FiresVanillaSkyTuner.RestoreAll(); }
    }
}
