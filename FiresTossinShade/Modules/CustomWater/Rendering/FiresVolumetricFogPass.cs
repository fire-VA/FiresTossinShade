using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Three-zone volumetric fog — LAND / SEA / UNDERWATER — as one fullscreen
    /// post pass (Custom/FiresVolFog) at AfterForwardAlpha. The shader picks the
    /// zone per pixel: camera-submerged → underwater (distance + current swirl);
    /// else ray-over-water → sea (height slab on the live water level, bobs with
    /// the tide, wind flow); else → land (world-Y slab, wind flow). Every look
    /// knob is a global pushed from the static fields below, so all three zones
    /// tune in REAL TIME via config; only the shader needs the bundle.
    /// </summary>
    public class FiresVolumetricFogPass : MonoBehaviour
    {
        // Master kill-switch — defaults ON so it's not a trap; the per-zone
        // toggles are the normal way to pick which fog draws.
        public static bool Enabled = true;
        public static bool AnyZoneOn => LandOn || SeaOn || UwOn || CloudOn;

        // Confine land fog to land and sea fog to water (soft coastline crossfade via
        // the shore field's water-ness channel). Off = the two bands overlap everywhere.
        public static bool DomainConfine = true;

        // Ground-fog SKY BLEND — angle above the horizon (ray-Y ~ sin(elevation)) over which the
        // LAND + SEA fog fades to clear, so it dissolves into the sky instead of a hard slab-top
        // line / bright terrain silhouette. 0 = disabled (old hard-topped floating-layer look).
        public static float SkyBlend = 0.35f;

        // Player push — applied to the land + sea fog. Within PushRadius of the player the
        // fog's coverage is warped OUTWARD so the mist parts / flows around you as you move,
        // like you are the wind. Density-preserving (a displacement, not a clear).
        public static bool  PushOn = true;
        public static float PushRadius = 12f;     // radius around the player the push reaches (m)
        public static float PushStrength = 0.6f;  // how hard the mist is shoved aside (0..1)

        // Cloud layer — a high-altitude volumetric band (world-Y), independent of the
        // ground fog, for overhead cloud cover. Weather-reactive via FiresFogEnvReactive.
        public static bool CloudOn = false;   // opt-in
        public static Color CloudColor = new Color(0.70f, 0.74f, 0.80f, 1f);
        public static float CloudMin = 600f;    // band bottom (world Y)
        public static float CloudMax = 1000f;   // band top (world Y)
        public static float CloudBotFade = 80f;     // soft bottom edge (m)
        public static float CloudTopFade = 150f;    // soft top edge (m)
        public static float CloudDensity = 0.5f;
        public static float CloudMaxOp = 0.85f;
        public static float CloudCoverage = 0.5f;
        public static float CloudPatch = 0.7f;
        public static float CloudPatchScale = 0.0015f; // bigger cells than ground fog
        public static float CloudUndulate = 30f;
        public static float CloudDrift = 0.02f;   // fixed deck drift speed, decoupled from storm wind so it won't churn/heave in rain

        // ── LAND (above water, ray over terrain) ──
        public static bool LandOn = true;
        public static Color LandColor = new Color(0.72f, 0.78f, 0.84f, 1f);   // DENSE-fog hue
        public static Color LandColorThin = new Color(0.72f, 0.78f, 0.84f, 1f);   // THIN-fog hue (blends to dense)
        public static float LandMin = 0f;
        public static float LandMax = 60f;
        public static float LandBotFade = 6f;     // soft bottom edge (m)
        public static float LandTopFade = 15f;    // soft top edge (m)
        public static float LandDensity = 0.6f;
        public static float LandMaxOp = 0.8f;
        public static float LandPatchScale = 0.004f;
        public static float LandPatch = 0.7f;     // overall patch strength (0 = flat uniform fog)
        public static float LandCoverage = 0.5f;     // dense-vs-thin balance
        public static float LandClearOp = 0.35f;    // density floor of the thin sections (0 = nearly clear)
        public static float LandPatchFade = 0.3f;     // softness of the thin<->dense transition
        public static float LandWind = 0.04f;
        public static float LandFarCut = 4000f;  // fog dies out past here so the dome/orbitals/moon stay crisp
        public static float LandUndulate = 5f;      // rolling edge amplitude (m) — top/bottom undulate ±this

        // ── SEA (above water, ray over water — slab anchored to water level) ──
        public static bool SeaOn = true;
        public static Color SeaColor = new Color(0.74f, 0.82f, 0.88f, 1f);   // DENSE-fog hue
        public static Color SeaColorThin = new Color(0.74f, 0.82f, 0.88f, 1f);   // THIN-fog hue (blends to dense)
        public static float SeaBelow = 4f;     // slab extends this far below the surface
        public static float SeaThick = 18f;    // …and this far above it
        public static float SeaBotFade = 4f;     // soft bottom edge (m)
        public static float SeaTopFade = 8f;     // soft top edge (m)
        public static float SeaDensity = 0.6f;
        public static float SeaMaxOp = 0.8f;
        public static float SeaPatchScale = 0.004f;
        public static float SeaPatch = 0.7f;     // overall patch strength (0 = flat uniform fog)
        public static float SeaCoverage = 0.5f;     // dense-vs-thin balance
        public static float SeaClearOp = 0.35f;    // density floor of the thin sections (0 = nearly clear)
        public static float SeaPatchFade = 0.3f;     // softness of the thin<->dense transition
        public static float SeaWind = 0.05f;
        public static float SeaFarCut = 4000f;  // fog dies out past here so the sky shell stays crisp
        public static float SeaUndulate = 5f;      // rolling edge amplitude (m) — top/bottom undulate ±this

        // ── UNDERWATER (camera below the surface — distance + current swirl) ──
        public static bool UwOn = true;
        public static Color UwColor = new Color(0.10f, 0.30f, 0.38f, 1f);
        public static float UwDensity = 0.7f;
        public static float UwMaxOp = 0.92f;
        public static float UwPatchScale = 0.02f;
        public static float UwSwirl = 0.6f;
        public static float UwCurrent = 0.05f;
        public static float UwDistStart = 2f;
        public static float UwDistRange = 40f;
        public static float UwFarCut = 400f;    // murk fades out past here so it never paints the dome
        public static Color UwColorThin   = new Color(0.10f, 0.30f, 0.38f, 1f);   // near/shallow murk hue (far/deep = UwColor)
        public static float UwCoverage    = 0.5f;    // patch coverage (same model as land/sea)
        public static float UwPatch       = 0.3f;    // patchiness strength (0 = uniform murk)
        public static float UwClearOp     = 0.6f;    // murk opacity in the clear pockets (1 = no pockets, stays uniform)
        public static float UwPatchFade   = 0.3f;    // soft edge width of the clear pockets
        public static float UwDepthDensity = 1f;     // murk multiplier at depth (1 = uniform; >1 = denser deep)
        public static float UwDepthSpan   = 30f;     // metres below the surface to reach full depth density

        private const string ShaderName = "Custom/FiresVolFog";
        private const CameraEvent Evt = CameraEvent.AfterForwardAlpha;

        private Camera m_cam; private CommandBuffer m_cb; private Material m_mat; private Shader m_shader; private bool m_warned;
        private static readonly Rect s_fullView = new Rect(0f, 0f, 1f, 1f);
        private readonly Vector3[] m_corners = new Vector3[4];

        private static readonly int P_RayBL = Shader.PropertyToID("_FiresFogRayBL");
        private static readonly int P_RayTL = Shader.PropertyToID("_FiresFogRayTL");
        private static readonly int P_RayTR = Shader.PropertyToID("_FiresFogRayTR");
        private static readonly int P_RayBR = Shader.PropertyToID("_FiresFogRayBR");
        private static readonly int P_Water = Shader.PropertyToID("_FiresFogWaterLevel");
        private static readonly int P_Submerged = Shader.PropertyToID("_FiresFogCamSubmerged");
        private static readonly int P_TideOffset = Shader.PropertyToID("_FiresTideOffset");

        private static int I(string n) => Shader.PropertyToID(n);

        // 0.3.154: the cave fade. CaveAware (cfg 09 / 02c) on: 0 outside, eases to 1 over CaveFadeSeconds while FAT says the camera is
        // in a cave. FAT's PUBLIC contract VerdantsAscent.Terrain.Voxel.FatCaveSignals.CameraInCave (0.2.825+, hysteresis'd: in 0.5 s,
        // out 1.5 s, switches >= 3 s apart), read by reflection. Missing (an older FAT) = 0: no change, fail soft.
        public static bool CaveAware = true;
        public static float CaveFadeSeconds = 4f;
        // 0.3.157 (Fire 10-07: fog "fades in and out" at cave mouths): CameraInCave flaps near mouths, so a flip only counts
        // once it has held CaveFlipHoldSeconds; FAT's continuous CameraCaveFactor (0..1), when present, replaces all of it.
        public static float CaveFlipHoldSeconds = 3f;
        private static System.Reflection.PropertyInfo s_inCave;
        private static System.Func<float> s_caveFactor;
        private static bool s_inCaveBound;
        private static float s_caveK;
        private static bool s_caveLogged;
        private static bool s_caveRaw, s_caveHeld;
        private static float s_caveRawSince;

        // 0.3.155: SHAFT fog bands (Fire 10-07: "a soft, translucent fog band in the shaft's air at river height, seen from the grotto
        // below"). Once a second the nearest 4 shafts within ShaftRange of the camera are read from FAT's PUBLIC contract
        // VerdantsAscent.Terrain.Voxel.FatCaveSignals.ShaftCount / TryGetShaft(i, out x, z, radius, bedY, surfaceY) (0.2.825+) and
        // pushed as _FogShaft[4] (x, z, radius, reach) and _FogShaftY[4] (bed, surface - ShaftTopGap). Missing: no bands.
        public static bool ShaftOn = true;
        public static Color ShaftColor = new Color(0.70f, 0.76f, 0.82f, 1f);
        public static float ShaftDensity = 0.02f, ShaftMaxOp = 0.18f, ShaftReach = 16f, ShaftTopGap = 0.5f, ShaftVFade = 1.5f, ShaftRange = 300f;
        private static System.Reflection.MethodInfo s_tryShaft;
        private static System.Reflection.PropertyInfo s_shaftCount;
        private static bool s_shaftBound;
        private static float s_nextShaftScan;
        private static readonly Vector4[] s_shaft = new Vector4[4], s_shaftY = new Vector4[4];
        private static int s_shafts, s_shaftsLogged = -1;

        private void PushShafts(Camera cam)
        {
            if (!s_shaftBound)
            {
                s_shaftBound = true;
                var t = HarmonyLib.AccessTools.TypeByName("VerdantsAscent.Terrain.Voxel.FatCaveSignals");
                s_shaftCount = t?.GetProperty("ShaftCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                s_tryShaft = t?.GetMethod("TryGetShaft", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] shaft bands: FAT's FatCaveSignals shaft API {(s_shaftCount != null && s_tryShaft != null ? "found" : "not found (FAT older than 0.2.825): no shaft bands")}");
            }
            if (Time.time >= s_nextShaftScan)
            {
                s_nextShaftScan = Time.time + 1f;
                s_shafts = 0;
                if (ShaftOn && s_shaftCount != null && s_tryShaft != null)
                {
                    try
                    {
                        int n = (int)s_shaftCount.GetValue(null, null);
                        Vector3 c = cam.transform.position;
                        var near = new System.Collections.Generic.List<(float d, Vector4 s, Vector4 y)>();
                        var args = new object[6];
                        for (int i = 0; i < n; i++)
                        {
                            args[0] = i;
                            if (!(bool)s_tryShaft.Invoke(null, args)) break;
                            float x = (float)args[1], z = (float)args[2], r = (float)args[3], bed = (float)args[4], surf = (float)args[5];
                            float d = new Vector2(x - c.x, z - c.z).magnitude;
                            if (d > ShaftRange + r) continue;
                            near.Add((d, new Vector4(x, z, r, Mathf.Max(0f, ShaftReach)), new Vector4(bed, Mathf.Max(bed + 0.2f, surf - ShaftTopGap), 0f, 0f)));
                        }
                        near.Sort((p, q) => p.d.CompareTo(q.d));
                        for (; s_shafts < 4 && s_shafts < near.Count; s_shafts++) { s_shaft[s_shafts] = near[s_shafts].s; s_shaftY[s_shafts] = near[s_shafts].y; }
                    }
                    catch (System.Exception ex) { VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] shaft read failed: {(ex.InnerException ?? ex).Message}; no shaft bands"); s_tryShaft = null; }
                }
                if (s_shafts != s_shaftsLogged)
                {
                    s_shaftsLogged = s_shafts;
                    VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] shaft bands: {s_shafts} within {ShaftRange:0} m" + (s_shafts > 0 ? $" (nearest at ({s_shaft[0].x:0},{s_shaft[0].y:0}) r {s_shaft[0].z:0.0}, y {s_shaftY[0].x:0.0}-{s_shaftY[0].y:0.0})" : ""));
                }
            }
            Shader.SetGlobalFloat(I("_FogShaftCount"), ShaftOn ? s_shafts : 0);
            Shader.SetGlobalVectorArray(I("_FogShaft"), s_shaft);
            Shader.SetGlobalVectorArray(I("_FogShaftY"), s_shaftY);
            Shader.SetGlobalColor(I("_FogShaftColor"), ShaftColor);
            Shader.SetGlobalFloat(I("_FogShaftDensity"), Mathf.Max(0f, ShaftDensity));
            Shader.SetGlobalFloat(I("_FogShaftMaxOp"), Mathf.Clamp(ShaftMaxOp, 0f, 0.6f));   // never opaque (Fire)
            Shader.SetGlobalFloat(I("_FogShaftVFade"), Mathf.Max(0.01f, ShaftVFade));
        }

        // The water surface over a point: sea + tide, vanilla/FAT-patched Floating.GetWaterLevel (inland), and
        // FAT 0.2.835 FatCaveSignals.TryWaterSurfaceAt (layered: a cave river answers its own level). Highest wins.
        public static float PerObjectUnderwaterScale = 0.15f;
        public static float CaveRenderFog = 0.25f;
        private static bool s_wsBound;
        private static System.Reflection.MethodInfo s_waterAt;
        private static WaterVolume s_camWv;
        private static readonly object[] s_waterArgs = new object[4];
        private static string s_waterLogged;
        public static float TrueWaterY(Vector3 p, out string kind)
        {
            float y = ZoneSystem.c_WaterLevel + Shader.GetGlobalFloat(P_TideOffset);
            kind = "sea";
            if (!s_wsBound)
            {
                s_wsBound = true;
                var t = HarmonyLib.AccessTools.TypeByName("VerdantsAscent.Terrain.Voxel.FatCaveSignals");
                s_waterAt = t?.GetMethod("TryWaterSurfaceAt", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float).MakeByRefType() }, null);
                VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] FAT TryWaterSurfaceAt {(s_waterAt != null ? "found" : "not found (FAT older than 0.2.835): sea + GetWaterLevel only")}");
            }
            if (s_waterAt != null)
            {
                try
                {
                    s_waterArgs[0] = p.x; s_waterArgs[1] = p.y; s_waterArgs[2] = p.z; s_waterArgs[3] = 0f;
                    if ((bool)s_waterAt.Invoke(null, s_waterArgs))
                    { float w = (float)s_waterArgs[3]; if (w > y && p.y < w) { y = w; kind = "inland (FAT)"; } }
                }
                catch (System.Exception) { s_waterAt = null; }
            }
            // Without FAT's layered answer, fall back to Floating (topmost surface: a river over a dry grotto's
            // roof reads as "under", 114.webp). With it, FAT is authoritative for inland water.
            if (s_waterAt == null)
            {
                float v = Floating.GetWaterLevel(p, ref s_camWv);
                if (v > -1000f && v > y && p.y < v) { y = v; kind = "inland"; }
            }
            return y;
        }

        private static float CaveFactor()
        {
            if (!s_inCaveBound)
            {
                s_inCaveBound = true;
                var t = HarmonyLib.AccessTools.TypeByName("VerdantsAscent.Terrain.Voxel.FatCaveSignals");
                s_inCave = t != null ? t.GetProperty("CameraInCave", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] cave fade: FAT's FatCaveSignals.CameraInCave {(s_inCave != null ? "found" : "not found (FAT older than 0.2.825): land/sea fog unchanged in caves")}");
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                var cfProp = t?.GetProperty("CameraCaveFactor", bf);
                var cfField = cfProp == null ? t?.GetField("CameraCaveFactor", bf) : null;
                if (cfProp != null && cfProp.PropertyType == typeof(float)) { var g = cfProp.GetGetMethod(); s_caveFactor = () => (float)g.Invoke(null, null); }
                else if (cfField != null && cfField.FieldType == typeof(float)) s_caveFactor = () => (float)cfField.GetValue(null);
                VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] cave factor: FAT's continuous CameraCaveFactor {(s_caveFactor != null ? "found: fog follows it directly" : "not found: debounced CameraInCave")}");
            }
            if (!CaveAware) { s_caveK = 0f; return 0f; }
            // Continuous factor from FAT: already smoothed over position, so follow it directly (no event fades).
            if (s_caveFactor != null)
            {
                try { s_caveK = Mathf.Clamp01(s_caveFactor()); return s_caveK; }
                catch (System.Exception) { s_caveFactor = null; }
            }
            bool raw = false;
            if (s_inCave != null)
            {
                try { raw = (bool)s_inCave.GetValue(null, null); } catch (System.Exception) { s_inCave = null; }
            }
            // Debounce: a flip only counts once it has held CaveFlipHoldSeconds (mouth flapping reverses sooner).
            if (raw != s_caveRaw) { s_caveRaw = raw; s_caveRawSince = Time.time; }
            if (s_caveHeld != s_caveRaw && Time.time - s_caveRawSince >= Mathf.Max(0f, CaveFlipHoldSeconds)) s_caveHeld = s_caveRaw;
            bool inCave = s_caveHeld;
            float target = inCave ? 1f : 0f;
            float step = CaveFadeSeconds > 0.01f ? Time.deltaTime / CaveFadeSeconds : 1f;
            s_caveK = Mathf.MoveTowards(s_caveK, target, step);
            if (inCave != s_caveLogged) { s_caveLogged = inCave; VerdantsAscent.FiresTossinShade.Log?.LogDebug($"[VolFog] camera {(inCave ? "entered a cave: land/sea fog fading out" : "left the caves: land/sea fog fading back")}"); }
            return s_caveK;
        }

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (!Enabled || !AnyZoneOn || cam == null || !FiresWaterShaderLoader.Enabled || !EnsureMaterial()) { Teardown(); return; }
            if (m_cam != cam) { Teardown(); Hook(cam); }
            if (m_cam == null) return;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;

            float waterY = TrueWaterY(cam.transform.position, out string waterKind);
            Shader.SetGlobalFloat(P_Water, waterY);
            string wl = cam.transform.position.y < waterY ? waterKind : null;
            if (wl != s_waterLogged) { s_waterLogged = wl; VerdantsAscent.FiresTossinShade.Log?.LogDebug(wl != null ? $"[VolFog] camera submerged in {wl} water at y {waterY:F1}" : "[VolFog] camera above water"); }
            // Smooth 0..1 over a shallow camera-depth band so the underwater murk EASES in as the
            // camera crosses the surface instead of snapping (the shader blends the zones by this).
            float camDepthBelow = waterY - cam.transform.position.y;
            Shader.SetGlobalFloat(P_Submerged, Mathf.Clamp01(camDepthBelow / 0.8f));
            Shader.SetGlobalFloat(I("_FogDomainConfine"), DomainConfine ? 1f : 0f);
            Shader.SetGlobalFloat(I("_FogSkyBlend"), Mathf.Max(0f, SkyBlend));

            // Player push — push the player XZ (the push centre) + params. Off when disabled
            // or no local player, so the land/sea fog is undisturbed.
            var pushPlayer = Player.m_localPlayer;
            if (PushOn && pushPlayer != null)
            {
                Vector3 pp = pushPlayer.transform.position;
                Shader.SetGlobalVector(I("_FiresFogPlayerPos"), new Vector4(pp.x, pp.y, pp.z, 0f));
                Shader.SetGlobalFloat(I("_FogPushOn"), 1f);
            }
            else Shader.SetGlobalFloat(I("_FogPushOn"), 0f);
            Shader.SetGlobalFloat(I("_FogPushRadius"), Mathf.Max(0.1f, PushRadius));
            Shader.SetGlobalFloat(I("_FogPushStrength"), Mathf.Clamp01(PushStrength));

            Shader.SetGlobalFloat(I("_FogCloudOn"), CloudOn ? 1f : 0f);
            Shader.SetGlobalColor(I("_FogCloudColor"), CloudColor);
            Shader.SetGlobalFloat(I("_FogCloudMin"), CloudMin);
            Shader.SetGlobalFloat(I("_FogCloudMax"), Mathf.Max(CloudMin + 1f, CloudMax));
            Shader.SetGlobalFloat(I("_FogCloudBotFade"), Mathf.Max(0.01f, CloudBotFade));
            Shader.SetGlobalFloat(I("_FogCloudTopFade"), Mathf.Max(0.01f, CloudTopFade));
            Shader.SetGlobalFloat(I("_FogCloudDensity"), Mathf.Max(0f, CloudDensity));
            Shader.SetGlobalFloat(I("_FogCloudMaxOp"), Mathf.Clamp01(CloudMaxOp));
            Shader.SetGlobalFloat(I("_FogCloudCoverage"), Mathf.Clamp01(CloudCoverage));
            Shader.SetGlobalFloat(I("_FogCloudPatch"), Mathf.Clamp01(CloudPatch));
            Shader.SetGlobalFloat(I("_FogCloudPatchScale"), CloudPatchScale);
            Shader.SetGlobalFloat(I("_FogCloudUndulate"), Mathf.Max(0f, CloudUndulate));
            Shader.SetGlobalFloat(I("_FogCloudDrift"), Mathf.Max(0f, CloudDrift));

            // 0.3.154 (Fire 10-07, the grotto: a blue wash toward the shaft, cream-washed stone, rim trees as silhouettes): the land and
            // sea slabs are world-Y bands that know nothing of caves, and a grotto floor at y 43 sits inside the land slab (0-60). While
            // the CAMERA is in a cave (FAT's hysteresis'd ClutterGroundProbe.CameraInCave, the same decision its clutter and ocean cull
            // use) both slabs ease out over CaveFadeSeconds; outside nothing changes. Underwater and cloud fog are untouched.
            PushShafts(cam);   // 0.3.155
            float caveK = CaveFactor();
            float slabK = 1f - caveK;
            // 0.3.156: in caves the env's own per-object fog (EnvMan sets it fresh every Update) flattens far grotto
            // walls to a white/blue surface (114.webp). Scale it by CaveRenderFog while dry; underwater fog is left alone.
            if (caveK > 0.001f && FiresUnderwaterEnv.UwLightBlend <= 0.001f && RenderSettings.fog)
                RenderSettings.fogDensity *= Mathf.Lerp(1f, Mathf.Clamp01(CaveRenderFog), caveK);

            // LAND
            Shader.SetGlobalFloat(I("_FogLandOn"), LandOn && slabK > 0.001f ? 1f : 0f);
            Shader.SetGlobalColor(I("_FogLandColor"), LandColor);
            Shader.SetGlobalColor(I("_FogLandColorThin"), LandColorThin);
            Shader.SetGlobalFloat(I("_FogLandMin"), LandMin);
            Shader.SetGlobalFloat(I("_FogLandMax"), Mathf.Max(LandMin + 0.1f, LandMax));
            Shader.SetGlobalFloat(I("_FogLandBotFade"), Mathf.Max(0.01f, LandBotFade));
            Shader.SetGlobalFloat(I("_FogLandTopFade"), Mathf.Max(0.01f, LandTopFade));
            Shader.SetGlobalFloat(I("_FogLandDensity"), LandDensity * slabK);
            Shader.SetGlobalFloat(I("_FogLandMaxOp"), Mathf.Clamp01(LandMaxOp) * slabK);
            Shader.SetGlobalFloat(I("_FogLandPatchScale"), LandPatchScale);
            Shader.SetGlobalFloat(I("_FogLandPatch"), Mathf.Clamp01(LandPatch));
            Shader.SetGlobalFloat(I("_FogLandCoverage"), Mathf.Clamp01(LandCoverage));
            Shader.SetGlobalFloat(I("_FogLandClearOp"), Mathf.Clamp01(LandClearOp));
            Shader.SetGlobalFloat(I("_FogLandPatchFade"), Mathf.Clamp(LandPatchFade, 0.02f, 0.5f));
            Shader.SetGlobalFloat(I("_FogLandWind"), LandWind);
            Shader.SetGlobalFloat(I("_FogLandFarCut"), Mathf.Max(50f, LandFarCut));
            Shader.SetGlobalFloat(I("_FogLandUndulate"), Mathf.Max(0f, LandUndulate));

            // SEA
            Shader.SetGlobalFloat(I("_FogSeaOn"), SeaOn && slabK > 0.001f ? 1f : 0f);
            Shader.SetGlobalColor(I("_FogSeaColor"), SeaColor);
            Shader.SetGlobalColor(I("_FogSeaColorThin"), SeaColorThin);
            Shader.SetGlobalFloat(I("_FogSeaBelow"), Mathf.Max(0f, SeaBelow));
            Shader.SetGlobalFloat(I("_FogSeaThick"), Mathf.Max(0.1f, SeaThick));
            Shader.SetGlobalFloat(I("_FogSeaBotFade"), Mathf.Max(0.01f, SeaBotFade));
            Shader.SetGlobalFloat(I("_FogSeaTopFade"), Mathf.Max(0.01f, SeaTopFade));
            Shader.SetGlobalFloat(I("_FogSeaDensity"), SeaDensity * slabK);
            Shader.SetGlobalFloat(I("_FogSeaMaxOp"), Mathf.Clamp01(SeaMaxOp) * slabK);
            Shader.SetGlobalFloat(I("_FogSeaPatchScale"), SeaPatchScale);
            Shader.SetGlobalFloat(I("_FogSeaPatch"), Mathf.Clamp01(SeaPatch));
            Shader.SetGlobalFloat(I("_FogSeaCoverage"), Mathf.Clamp01(SeaCoverage));
            Shader.SetGlobalFloat(I("_FogSeaClearOp"), Mathf.Clamp01(SeaClearOp));
            Shader.SetGlobalFloat(I("_FogSeaPatchFade"), Mathf.Clamp(SeaPatchFade, 0.02f, 0.5f));
            Shader.SetGlobalFloat(I("_FogSeaWind"), SeaWind);
            Shader.SetGlobalFloat(I("_FogSeaFarCut"), Mathf.Max(50f, SeaFarCut));
            Shader.SetGlobalFloat(I("_FogSeaUndulate"), Mathf.Max(0f, SeaUndulate));

            // UNDERWATER
            // Ease the murk opacity by the temporal above<->below blend so it fades in/out with the
            // lighting transition (vanilla-style timed crossfade) instead of a hard cut on submersion.
            // When the underwater env driver isn't driving it (disabled), fall back to 1 (full murk).
            float uwEase = FiresUnderwaterEnv.Enabled ? Mathf.Clamp01(FiresUnderwaterEnv.UwLightBlend) : 1f;
            Shader.SetGlobalFloat(I("_FogUwOn"), UwOn ? 1f : 0f);
            Shader.SetGlobalColor(I("_FogUwColor"), UwColor);
            Shader.SetGlobalFloat(I("_FogUwDensity"), UwDensity);
            Shader.SetGlobalFloat(I("_FogUwMaxOp"), Mathf.Clamp01(UwMaxOp) * uwEase);
            Shader.SetGlobalFloat(I("_FogUwPatchScale"), UwPatchScale);
            Shader.SetGlobalFloat(I("_FogUwSwirl"), UwSwirl);
            Shader.SetGlobalFloat(I("_FogUwCurrent"), UwCurrent);
            Shader.SetGlobalFloat(I("_FogUwDistStart"), UwDistStart);
            Shader.SetGlobalFloat(I("_FogUwDistRange"), Mathf.Max(1f, UwDistRange));
            Shader.SetGlobalFloat(I("_FogUwFarCut"), Mathf.Max(20f, UwFarCut));
            Shader.SetGlobalColor(I("_FogUwColorThin"), UwColorThin);
            Shader.SetGlobalFloat(I("_FogUwCoverage"), Mathf.Clamp01(UwCoverage));
            Shader.SetGlobalFloat(I("_FogUwPatch"), Mathf.Clamp01(UwPatch));
            Shader.SetGlobalFloat(I("_FogUwClearOp"), Mathf.Clamp01(UwClearOp));
            Shader.SetGlobalFloat(I("_FogUwPatchFade"), Mathf.Max(0.02f, UwPatchFade));
            Shader.SetGlobalFloat(I("_FogUwDepthDensity"), Mathf.Max(0f, UwDepthDensity));
            Shader.SetGlobalFloat(I("_FogUwDepthSpan"), Mathf.Max(1f, UwDepthSpan));
        }

        private bool EnsureMaterial()
        {
            if (m_mat != null) return true;
            if (m_shader == null)
            {
                var bundle = FiresWaterShaderLoader.Bundle;
                if (bundle == null) return false;
                m_shader = bundle.LoadAsset<Shader>(ShaderName);
                if (m_shader == null)
                    foreach (var s in bundle.LoadAllAssets<Shader>())
                        if (s != null && s.name == ShaderName) { m_shader = s; break; }
                if (m_shader == null)
                {
                    if (!m_warned)
                    {
                        Debug.LogWarning("[FiresVolFog] Custom/FiresVolFog not in the FiresWater bundle — "
                            + "volumetric fog off. Add the shader to the 'fireswater' bundle and rebuild.");
                        m_warned = true;
                    }
                    return false;
                }
            }
            m_mat = new Material(m_shader);
            FiresLogger.LogVerbose("[FiresVolFog] Custom/FiresVolFog loaded — 3-zone volumetric fog active.");
            return true;
        }

        private void Hook(Camera cam)
        {
            m_cam = cam;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;
            m_cb = new CommandBuffer { name = "FiresVolFog" };
            int tmp = Shader.PropertyToID("_FiresFogScene");
            m_cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear);
            m_cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp);
            m_cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, m_mat);
            m_cb.ReleaseTemporaryRT(tmp);
            m_cam.AddCommandBuffer(Evt, m_cb);
        }

        private void Teardown()
        {
            if (m_cam != null && m_cb != null) m_cam.RemoveCommandBuffer(Evt, m_cb);
            m_cb = null; m_cam = null;
        }

        private void OnEnable() { Camera.onPreRender += OnCamPreRender; }
        private void OnDisable() { Camera.onPreRender -= OnCamPreRender; Teardown(); }
        private void OnDestroy() { Camera.onPreRender -= OnCamPreRender; Teardown(); }

        private void OnCamPreRender(Camera cam)
        {
            if (m_cam == null || cam != m_cam) return;
            cam.CalculateFrustumCorners(s_fullView, cam.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_corners);
            Transform t = cam.transform;
            Shader.SetGlobalVector(P_RayBL, t.TransformVector(m_corners[0]));
            Shader.SetGlobalVector(P_RayTL, t.TransformVector(m_corners[1]));
            Shader.SetGlobalVector(P_RayTR, t.TransformVector(m_corners[2]));
            Shader.SetGlobalVector(P_RayBR, t.TransformVector(m_corners[3]));
        }
    }
}
