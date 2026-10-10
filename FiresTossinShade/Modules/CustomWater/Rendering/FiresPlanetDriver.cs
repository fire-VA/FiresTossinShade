using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Renders the planet/sky dome as OUR OWN generated dome mesh (a spherical
    /// cap) carrying Custom/FiresPlanet — a planet (world map over ocean, sun-lit,
    /// atmosphere rim) seen from space, and the weather cloud layer seen from the
    /// ground. We build the mesh ourselves (instead of riding the vanilla
    /// cloud_plane, a thin flat cap that can't scale tall) so we can:
    ///   * scale radius + height INDEPENDENTLY (the sides rise as height grows),
    ///   * give it HUGE bounds so it never frustum-culls (renders from far up),
    ///   * pin it above the mountains and follow the player.
    /// The vanilla cloud planes are hidden; the cylinder swirl stays vanilla
    /// (FiresVanillaSkyTuner). Above the dome the world is culled to the skybox.
    /// </summary>
    public static class FiresPlanet
    {
        public static bool Enabled = true;
        public static Color OceanColor = new Color(0.04f, 0.18f, 0.42f, 1f);
        public static float CloudCoverage = 0.5f;    // OUTSIDE (planet veil)
        public static float CloudSoftness = 0.25f;   // OUTSIDE
        public static float SurfaceCloudOpacity = 0.55f;  // OUTSIDE
        // INSIDE / overhead clouds (what you see from the ground) — driven by the
        // 09 - FiresAtmosphere sliders. Weather ramps coverage up toward full.
        public static float InsideCoverage = 0.5f;
        public static float InsideSoftness = 0.25f;
        public static float InsideOpacity = 0.8f;
        // Keep OUR dome + cylinder as the cloud layer (default). The fix for cloud
        // artifacts is to handle the texture/shaders the way vanilla's Custom/Clouds
        // does, NOT to strip our systems out. (true was a misread — it disabled the
        // dome clouds + un-hid vanilla's; left here only as a debug escape hatch.)
        public static bool UseVanillaCloudPlane = false;
        public static float ClearClouds = 0.15f;   // clear-weather floor (auto from InsideCoverage)
        public static float CloudDensityBoost = 3.0f;    // vanilla _Opacity-style boost (faint PNG → solid)
        public static float InsideScale = 0.0004f; // dome cloud world tiling
        public static float InsideScroll = 0.0002f; // dome cloud drift (× wind _CloudOffset)
        public static float InsideHue = 0.0f;    // dome cloud hue shift
        public static float HorizonFadeLow = 0.15f;   // clouds fully faded below this view-up
        public static float HorizonFadeHigh = 0.40f;   // clouds full above this view-up (cylinder owns below)
        public static float CloudPlaneHeight = 1500f;   // virtual cloud-ceiling height above the camera
        public static float CloudMaxDist = 6000f;   // cap on cloud sample distance (kills horizon sunburst smear)
        public static float CloudScale = 0.0004f;
        public static float ScrollSpeed = 1.0f;
        public static float DomeRadiusMeters = 5000f;   // horizontal radius (rim reaches this far out)
        public static float DomeHeight = 2500f;   // height of the dome TOP above the rim
        public static float DomeBaseY = 0f;      // world Y the rim sits at (wraps down to the ground)
        public static float DomeRise = 0.5f;    // (legacy, unused by the hemisphere)
        public static float MapOpacity = 1.0f;
        public static float Vibrance = 1.0f;
        public static float Hue = 0.0f;
        public static bool HideWorldInSpaceView = true;   // above the dome, cull terrain/water → skybox shows

        private const string ShaderName = "Custom/FiresPlanet";
        private const string ShaderAsset = "FiresPlanet";   // bundle asset name
        private const string CloudTexName = "CloudTextFire1";

        private static GameObject s_domeGO;
        private static MeshRenderer s_domeMR;
        private static Material s_mat;
        private static GameObject s_vanillaCloud, s_rain, s_rainDown;
        private static Shader s_shader;
        private static Texture s_cloudTex;
        private static Texture s_shownMap;
        private static float s_shownMapOpacity = -1f;
        private static bool s_loggedShader, s_loggedTex, s_loggedBuilt;
        private static bool s_worldHidden, s_hideMaskInit, s_loggedSpace;
        private static int s_hideMask;
        private static float s_savedFarClip;
        public static float SpaceViewFarClip = 40000f;   // render the dome + orbitals from far up

        // True once Custom/FiresPlanet resolved from the bundle — read by
        // the 🌊 SHADERS load summary.
        public static bool ShaderLoaded => s_shader != null;

        private static readonly int P_Ocean = Shader.PropertyToID("_OceanColor");
        private static readonly int P_Coverage = Shader.PropertyToID("_CloudCoverage");
        private static readonly int P_Soft = Shader.PropertyToID("_CloudSoftness");
        private static readonly int P_SurfCloud = Shader.PropertyToID("_SurfaceCloudOpacity");
        private static readonly int P_Clear = Shader.PropertyToID("_ClearClouds");
        private static readonly int P_InsideCov = Shader.PropertyToID("_InsideCoverage");
        private static readonly int P_InsideSoft = Shader.PropertyToID("_InsideSoftness");
        private static readonly int P_InsideOpac = Shader.PropertyToID("_InsideOpacity");
        private static readonly int P_DensBoost = Shader.PropertyToID("_CloudDensityBoost");
        private static readonly int P_InScale = Shader.PropertyToID("_InsideScale");
        private static readonly int P_InScroll = Shader.PropertyToID("_InsideScroll");
        private static readonly int P_InHue = Shader.PropertyToID("_InsideHue");
        private static readonly int P_CloudHt = Shader.PropertyToID("_CloudPlaneHeight");
        private static readonly int P_CloudMaxD = Shader.PropertyToID("_CloudMaxDist");
        private static readonly int P_FadeLow = Shader.PropertyToID("_HorizonFadeLow");
        private static readonly int P_FadeHigh = Shader.PropertyToID("_HorizonFadeHigh");
        private static readonly int P_CloudScale = Shader.PropertyToID("_CloudScale");
        private static readonly int P_SurfScale = Shader.PropertyToID("_SurfaceScale");
        private static readonly int P_Scroll = Shader.PropertyToID("_ScrollMul");
        private static readonly int P_SurfTex = Shader.PropertyToID("_SurfaceTex");
        private static readonly int P_CloudTex = Shader.PropertyToID("_CloudTex");
        private static readonly int P_Center = Shader.PropertyToID("_FiresPlanetCenter");
        private static readonly int P_Rain = Shader.PropertyToID("_FiresCloudRain");
        private static readonly int P_StormDarken = Shader.PropertyToID("_FiresStormDarken");
        private static readonly int P_StormCover = Shader.PropertyToID("_FiresStormCover");
        private static readonly int P_StormOpacity = Shader.PropertyToID("_FiresStormOpacity");
        public static float StormDarken = 0.35f;   // cloud brightness at full storm (lower = darker/more ominous)
        public static float StormCover = 0.15f;   // storm coverage factor (lower = heavier overcast)
        public static float StormOpacity = 1.6f;    // cloud opacity boost at full storm (more solid sky)
        private static readonly int P_Inside = Shader.PropertyToID("_FiresPlanetInside");
        private static readonly int P_MapOpacity = Shader.PropertyToID("_MapOpacity");
        private static readonly int P_Vibrance = Shader.PropertyToID("_Vibrance");
        private static readonly int P_Hue = Shader.PropertyToID("_Hue");

        // ---- per-frame: follow the player, scale to radius/height, drive globals.
        public static void PerFrame()
        {
            if (!Enabled) { if (s_domeGO != null && s_domeGO.activeSelf) s_domeGO.SetActive(false); return; }
            if (s_domeGO == null) return;                 // Tick() builds it once the shader is ready
            if (!s_domeGO.activeSelf) s_domeGO.SetActive(true);

            var player = Player.m_localPlayer;
            Vector3 p = player != null ? player.transform.position : Vector3.zero;
            // Hemisphere: rim pinned at DomeBaseY (wraps to the ground), top at
            // DomeBaseY + DomeHeight. Follows the player in XZ.
            s_domeGO.transform.position = new Vector3(p.x, DomeBaseY, p.z);
            s_domeGO.transform.localScale = new Vector3(DomeRadiusMeters, DomeHeight, DomeRadiusMeters);

            // Dome renders from INSIDE (cloud layer on the dome) and from ABOVE
            // (the planet); the global flag picks the look. Renderer stays on.
            var cam = Utils.GetMainCamera();
            float camY = cam != null ? cam.transform.position.y : p.y;
            if (s_domeMR != null && !s_domeMR.enabled) s_domeMR.enabled = true;
            bool planetInView = camY >= DomeBaseY + DomeHeight;
            Shader.SetGlobalFloat(P_Inside, planetInView ? 0f : 1f);
            ApplySurfaceMap(planetInView);

            // Map projects top-down from the dome centre (= player XZ).
            Shader.SetGlobalVector(P_Center, new Vector4(p.x, DomeBaseY, p.z, 0f));

            float rain = 0f;
            if (EnvMan.instance != null)
            {
                var env = EnvMan.instance.GetCurrentEnvironment();
                if (env != null) rain = env.m_rainCloudAlpha;
            }
            Shader.SetGlobalFloat(P_Rain, rain);
            Shader.SetGlobalFloat(P_StormDarken, StormDarken);
            Shader.SetGlobalFloat(P_StormCover, StormCover);
            Shader.SetGlobalFloat(P_StormOpacity, StormOpacity);
        }

        private static void ApplySurfaceMap(bool planetInView)
        {
            FiresPlanetMap.Tick(planetInView && MapOpacity > 0f);
            var map = FiresPlanetMap.Current;
            if (map != null && s_shownMap != map)
            {
                s_mat.SetTexture(P_SurfTex, map);
                s_shownMap = map;
            }
            float opacity = map != null ? MapOpacity : 0f;
            if (opacity != s_shownMapOpacity)
            {
                s_mat.SetFloat(P_MapOpacity, opacity);
                s_shownMapOpacity = opacity;
            }
        }

        // ---- ~1 Hz: ensure shader/mesh/material exist, push props, hide vanilla.
        public static void Tick()
        {
            if (!Enabled) return;
            if (!EnsureShader()) return;
            EnsureDome();
            if (s_mat == null) return;
            ApplyProps(s_mat);
            // Vanilla clouds (default): un-hide the flat cloud_plane and let the
            // dome render no clouds. Otherwise the dome is the cloud layer and we
            // hide vanilla's so they don't double. Also tell the sky tuner to leave
            // the cloud CYLINDER vanilla-sized — otherwise its scaled-up wall is the
            // giant inside-out cloud you see once the dome clouds stop covering it.
            FiresVanillaSkyTuner.DeferToVanillaClouds = UseVanillaCloudPlane;
            if (UseVanillaCloudPlane) RestoreVanillaClouds();
            else HideVanillaClouds();
        }

        // World above the dome top => space view: cull terrain/water off the
        // camera (→ star skybox around the planet) and raise the far clip so the
        // dome + orbitals render from far up. Restores below the dome / when off.
        public static void UpdateSpaceView()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (!s_hideMaskInit)
            {
                s_hideMask = LayerMask.GetMask("terrain", "Water", "WaterVolume");
                s_hideMaskInit = true;
            }

            float domeTop = DomeBaseY + DomeHeight;                   // hemisphere top
            // The cloud cylinder follows the camera, so above the dome top it wraps
            // the view as a cyan blob over the stars. Hide it up there regardless of
            // the terrain-cull toggle below.
            FiresVanillaSkyTuner.HideForSpace = cam.transform.position.y > domeTop;
            bool wantHide = Enabled && HideWorldInSpaceView && s_domeGO != null
                         && cam.transform.position.y > domeTop;

            // NEVER cull the dome's own layer, whatever it ended up on.
            int domeMask = s_domeGO != null ? (s_hideMask & ~(1 << s_domeGO.layer)) : s_hideMask;

            if (wantHide)
            {
                if (!s_worldHidden)
                {
                    s_savedFarClip = cam.farClipPlane;
                    cam.cullingMask &= ~domeMask;
                    s_worldHidden = true;
                    if (!s_loggedSpace)
                    {
                        var mr = s_domeGO.GetComponent<Renderer>();
                        FiresLogger.LogVerbose("[FiresPlanet] space-view ENTER — "
                            + $"camY={cam.transform.position.y:0} domeTop={domeTop:0} domeY={s_domeGO.transform.position.y:0} "
                            + $"domeLayer={s_domeGO.layer} active={s_domeGO.activeInHierarchy} "
                            + $"rEnabled={(mr != null && mr.enabled)} isVisible={(mr != null && mr.isVisible)} "
                            + $"farClip={cam.farClipPlane:0}->{SpaceViewFarClip:0}");
                        s_loggedSpace = true;
                    }
                }
                if (cam.farClipPlane < SpaceViewFarClip) cam.farClipPlane = SpaceViewFarClip;
            }
            else if (s_worldHidden)
            {
                cam.cullingMask |= s_hideMask;
                cam.farClipPlane = s_savedFarClip;
                s_worldHidden = false;
            }
        }

        private static void EnsureDome()
        {
            if (s_domeGO != null) return;                 // self-heals if destroyed on scene reload

            s_mat = new Material(s_shader);
            s_shownMap = null;
            s_shownMapOpacity = -1f;
            s_domeGO = new GameObject("FiresPlanetDome");
            Object.DontDestroyOnLoad(s_domeGO);
            var mf = s_domeGO.AddComponent<MeshFilter>();
            mf.sharedMesh = BuildDomeMesh(24, 48);
            s_domeMR = s_domeGO.AddComponent<MeshRenderer>();
            s_domeMR.sharedMaterial = s_mat;
            s_domeMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_domeMR.receiveShadows = false;
            s_domeMR.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            s_domeMR.allowOcclusionWhenDynamic = false;
            var mr = s_domeMR;
            if (!s_loggedBuilt) { FiresLogger.LogVerbose($"[FiresPlanet] Built own dome mesh — layer={s_domeGO.layer}, scalable, far-clip-proof."); s_loggedBuilt = true; }
        }

        // FULL HEMISPHERE: rim (equator) radius 1 at y=0, top at y=1. The rim
        // sits at the ground and the dome curves up overhead, wrapping the player.
        // Bounds forced huge so it never frustum-culls regardless of camera range.
        private static Mesh BuildDomeMesh(int rings, int segs)
        {
            int vcount = 1 + rings * segs;
            var verts = new Vector3[vcount];
            var norms = new Vector3[vcount];
            verts[0] = new Vector3(0f, 1f, 0f);
            norms[0] = Vector3.up;
            int vi = 1;
            for (int ring = 1; ring <= rings; ring++)
            {
                float r = (float)ring / rings;
                float y = Mathf.Sqrt(Mathf.Max(0f, 1f - r * r));   // sphere arc → rim at y=0
                for (int s = 0; s < segs; s++)
                {
                    float th = (float)s / segs * 2f * Mathf.PI;
                    verts[vi] = new Vector3(r * Mathf.Cos(th), y, r * Mathf.Sin(th));
                    norms[vi] = Vector3.up;
                    vi++;
                }
            }

            var tris = new System.Collections.Generic.List<int>(rings * segs * 6);
            for (int s = 0; s < segs; s++)                 // centre fan
            {
                int a = 1 + s, b = 1 + (s + 1) % segs;
                tris.Add(0); tris.Add(a); tris.Add(b);
            }
            for (int ring = 1; ring < rings; ring++)       // ring quads
            {
                int baseA = 1 + (ring - 1) * segs;
                int baseB = 1 + ring * segs;
                for (int s = 0; s < segs; s++)
                {
                    int a0 = baseA + s, a1 = baseA + (s + 1) % segs;
                    int b0 = baseB + s, b1 = baseB + (s + 1) % segs;
                    tris.Add(a0); tris.Add(b0); tris.Add(b1);
                    tris.Add(a0); tris.Add(b1); tris.Add(a1);
                }
            }

            var m = new Mesh { name = "FiresPlanetDomeMesh" };
            m.vertices = verts;
            m.normals = norms;
            m.SetTriangles(tris, 0, calculateBounds: false);          // don't let it shrink the bounds
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e7f);   // huge → never frustum-culls
            return m;
        }

        private static void ApplyProps(Material mat)
        {
            EnsureTextures();
            if (s_cloudTex != null) mat.SetTexture(P_CloudTex, s_cloudTex);
            mat.SetColor(P_Ocean, OceanColor);
            mat.SetFloat(P_Coverage, CloudCoverage);
            mat.SetFloat(P_Soft, CloudSoftness);
            mat.SetFloat(P_SurfCloud, SurfaceCloudOpacity);
            mat.SetFloat(P_InsideCov, InsideCoverage);
            mat.SetFloat(P_InsideSoft, InsideSoftness);
            mat.SetFloat(P_InsideOpac, UseVanillaCloudPlane ? 0f : InsideOpacity);   // 0 = dome draws no clouds
            mat.SetFloat(P_Clear, ClearClouds);
            mat.SetFloat(P_DensBoost, CloudDensityBoost);
            mat.SetFloat(P_InScale, InsideScale);
            mat.SetFloat(P_InScroll, InsideScroll);
            mat.SetFloat(P_InHue, InsideHue);
            mat.SetFloat(P_CloudHt, CloudPlaneHeight);
            mat.SetFloat(P_CloudMaxD, CloudMaxDist);
            mat.SetFloat(P_FadeLow, HorizonFadeLow);
            mat.SetFloat(P_FadeHigh, HorizonFadeHigh);
            mat.SetFloat(P_CloudScale, CloudScale);
            mat.SetFloat(P_SurfScale, 0.5f / Mathf.Max(1f, DomeRadiusMeters));
            mat.SetFloat(P_Scroll, 0.0001f * ScrollSpeed);
            mat.SetFloat(P_Vibrance, Vibrance);
            mat.SetFloat(P_Hue, Hue);
        }

        private static bool EnsureShader()
        {
            if (s_shader != null) return true;
            s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderAsset, ShaderName);
            if (s_loggedShader) return s_shader != null;
            if (s_shader != null)
            {
                FiresLogger.LogVerbose("[FiresPlanet] Loaded 'Custom/FiresPlanet'.");
                s_loggedShader = true;
            }
            else if (FiresWaterShaderLoader.Bundle != null)
            {
                Debug.LogWarning("[FiresPlanet] bundle is mounted but 'Custom/FiresPlanet' "
                    + "didn't resolve — check the shader is assigned to the fireswater bundle.");
                s_loggedShader = true;
            }
            return s_shader != null;
        }

        private static void EnsureTextures()
        {
            if (s_loggedTex) return;
            var bundle = FiresWaterShaderLoader.Bundle;
            if (bundle == null) return;
            s_cloudTex = bundle.LoadAsset<Texture2D>(CloudTexName);   // our cloud PNG (greyscale: white=cloud)
            FiresLogger.LogVerbose($"[FiresPlanet] clouds='{(s_cloudTex != null ? CloudTexName : "MISSING")}'.");
            s_loggedTex = true;
        }

        // Our dome IS the cloud layer now — hide the vanilla cloud planes so they
        // don't double up. (We still read the vanilla cloud TEXTURE off it.)
        private static void HideVanillaClouds()
        {
            if (s_vanillaCloud == null) s_vanillaCloud = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_upper_downside");
            if (s_rain == null) s_rain = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain");
            if (s_rainDown == null) s_rainDown = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain_downside");
            DisableRenderer(s_vanillaCloud);
            DisableRenderer(s_rain);
            DisableRenderer(s_rainDown);
        }

        // Re-enable the vanilla cloud planes (used when UseVanillaCloudPlane is on,
        // so EnvMan's flat, animated, weather-reactive clouds are the cloud layer).
        private static void RestoreVanillaClouds()
        {
            if (s_vanillaCloud == null) s_vanillaCloud = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_upper_downside");
            if (s_rain == null) s_rain = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain");
            if (s_rainDown == null) s_rainDown = GameObject.Find("_GameMain/_Environment/Clouds/offset/cloud_plane_rain_downside");
            EnableRenderer(s_vanillaCloud);
            EnableRenderer(s_rain);
            EnableRenderer(s_rainDown);
        }

        private static void DisableRenderer(GameObject go)
        {
            if (go == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null && r.enabled) r.enabled = false;
        }

        private static void EnableRenderer(GameObject go)
        {
            if (go == null) return;
            var r = go.GetComponent<Renderer>();
            if (r != null && !r.enabled) r.enabled = true;
        }
    }

    public class FiresPlanetDriver : MonoBehaviour
    {
        private const float CheckIntervalSec = 1f;
        private float m_next;

        private void Update()
        {
            FiresPlanet.PerFrame();                 // follow + scale + globals (every frame)
            if (Time.unscaledTime < m_next) return;
            m_next = Time.unscaledTime + CheckIntervalSec;
            FiresPlanet.Tick();                     // ensure mesh/material/props, hide vanilla
        }

        private void LateUpdate()
        {
            FiresPlanet.UpdateSpaceView();
        }
    }
}
