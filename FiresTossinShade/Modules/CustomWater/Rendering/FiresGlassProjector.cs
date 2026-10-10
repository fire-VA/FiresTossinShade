using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// FiresGlass stained-glass light projection — the fullscreen pass that reads
    /// EVERY registered window (FiresGlassWindow.Registry) each frame and casts each
    /// one's coloured light onto the world. Reconstructs world position from depth
    /// (the caustics trick), so it is camera-independent; the engine's own sun shadow
    /// suppresses the cast where the world is already dark (so real shadows show
    /// through). Windows share a clean leaded projection texture, tinted per window.
    ///
    /// Console (test): va_glass on|off|status|debug|here|spawn|clear|img &lt;name&gt;|list
    ///   |reach|intensity|shadow|shadowlow|shadowhigh|godray.
    /// </summary>
    public class FiresGlassProjector : MonoBehaviour
    {
        public static bool Active = true;
        public static float Intensity = 1.8f;
        public static float GodrayDensity = 0.6f;
        public static float GodrayMaxDistance = 45f;
        public static float Reach = 40f;   // metres the beam throws before it fades out
        public static float Softness = 0.12f;
        public static bool DebugView = false;
        public static bool ShadowDebug = false;   // paint occluded pixels red to verify the sun-shadow map

        // Real-shadow gate (engine sun shadow via scene luminance).
        public static float ShadowStrength = 1f;
        public static float ShadowLow = 0.20f;
        public static float ShadowHigh = 0.55f;

        // Cull the coloured cast with VANILLA's own sun shadow (screen-space) instead of our
        // custom sun-shadow-map render. Lines the cast up pixel-perfect with the shadow you
        // already see, and skips the extra shadow-camera render. Default on.
        public static bool UseVanillaShadow = true;

        // NEON signs — self-lit pieces (WinDef neon flag): the beam leaves the sign's front
        // tilted DOWN by NeonCastAngleDeg, at NeonCastIntensity, ignoring every sun gate
        // (day/night, roof raycast, sun shadow) so a bar sign pools its glow out front 24/7.
        public static float NeonCastIntensity = 1f;
        public static float NeonCastAngleDeg = 40f;
        // -1 = beam out the sign's VISIBLE front (default), +1 = out the back. Live flip.
        public static float NeonBeamSign = -1f;

        // The shared, clean leaded pattern all windows project (tinted per window).
        public static readonly string[] ImageNames = { "StainedEmit1", "Glass2", "Glass2Emit" };
        public static string CurrentImage = "StainedEmit1";

        private const int MAXW = 8;
        private const string ShaderName = "Custom/FiresGlassProjection";
        private const CameraEvent Evt = CameraEvent.AfterForwardAlpha;

        private Camera m_cam;
        private CommandBuffer m_cb;
        private Material m_mat;
        private Shader m_shader;
        private Texture m_projTex;
        private bool m_warned;

        // Screen-space shadow-mask capture for the UseVanillaShadow path. Unity's
        // _ShadowMapTexture is a TEMPORARY that is recycled right after opaque lighting —
        // by AfterForwardAlpha (where our pass samples) it's stale/unbound, reading "fully
        // lit" everywhere, which is why the vanilla toggle appeared to do nothing on every
        // setup. Fix: copy the mask at the ONE moment it's valid — a CommandBuffer on the
        // directional light at AfterScreenspaceMask — into our own frame-temporary and
        // publish it as _FiresScreenShadowMask (+ ...On flag; the shader falls back to the
        // legacy sample when 0). The temporary auto-releases at end of frame.
        private Light m_maskLight;
        private CommandBuffer m_maskCB;
        private static readonly int P_MaskTmp = Shader.PropertyToID("_FiresScreenShadowMaskRT");
        private static readonly int P_MaskGlobal = Shader.PropertyToID("_FiresScreenShadowMask");
        private static readonly int P_MaskOn = Shader.PropertyToID("_FiresScreenShadowMaskOn");

        // The per-window projection atlas (each window casts its own face-on image).
        private const int AtlasCols = 6, AtlasRows = 6;
        private Texture m_atlas;

        private readonly List<FiresGlassWindow> m_sorted = new List<FiresGlassWindow>();
        private readonly Vector4[] m_centers = new Vector4[MAXW];
        private readonly Vector4[] m_normals = new Vector4[MAXW];
        private readonly Vector4[] m_rights = new Vector4[MAXW];
        private readonly Vector4[] m_ups = new Vector4[MAXW];
        private readonly Vector4[] m_halfs = new Vector4[MAXW];
        private readonly Vector4[] m_tints = new Vector4[MAXW];
        private readonly Vector4[] m_uvs = new Vector4[MAXW];

        private static Texture2D s_testPattern;
        private static readonly List<GameObject> s_testWindows = new List<GameObject>();
        private static bool s_passHooked;   // diagnostics: is the fullscreen pass attached
        private static bool s_matLoaded;    // diagnostics: did the projection material load

        private static readonly Rect s_fullView = new Rect(0f, 0f, 1f, 1f);
        private readonly Vector3[] m_corners = new Vector3[4];
        private static readonly int P_RayBL = Shader.PropertyToID("_FiresRayBL");
        private static readonly int P_RayTL = Shader.PropertyToID("_FiresRayTL");
        private static readonly int P_RayTR = Shader.PropertyToID("_FiresRayTR");
        private static readonly int P_RayBR = Shader.PropertyToID("_FiresRayBR");

        private static readonly int P_Centers = Shader.PropertyToID("_GlassCenters");
        private static readonly int P_Normals = Shader.PropertyToID("_GlassNormals");
        private static readonly int P_Rights = Shader.PropertyToID("_GlassRights");
        private static readonly int P_Ups = Shader.PropertyToID("_GlassUps");
        private static readonly int P_Halfs = Shader.PropertyToID("_GlassHalfs");
        private static readonly int P_Tints = Shader.PropertyToID("_GlassTints");
        private static readonly int P_UV = Shader.PropertyToID("_GlassUV");
        private static readonly int P_ProjAtlas = Shader.PropertyToID("_GlassProjAtlas");
        private static readonly int P_UseAtlas = Shader.PropertyToID("_GlassUseAtlas");
        private static readonly int P_Count = Shader.PropertyToID("_GlassCount");

        private static readonly int P_Sun = Shader.PropertyToID("_GlassSunDir");
        private static readonly int P_Intensity = Shader.PropertyToID("_GlassIntensity");
        private static readonly int P_Reach = Shader.PropertyToID("_GlassReach");
        private static readonly int P_Softness = Shader.PropertyToID("_GlassSoftness");
        private static readonly int P_DayMask = Shader.PropertyToID("_GlassDayMask");
        private static readonly int P_Debug = Shader.PropertyToID("_GlassDebug");
        private static readonly int P_ShadowDebug = Shader.PropertyToID("_GlassShadowDebug");
        private static readonly int P_NeonTiltCos = Shader.PropertyToID("_GlassNeonTiltCos");
        private static readonly int P_NeonTiltSin = Shader.PropertyToID("_GlassNeonTiltSin");
        private static readonly int P_NeonCast    = Shader.PropertyToID("_GlassNeonCast");
        private static readonly int P_NeonBeamSign = Shader.PropertyToID("_GlassNeonBeamSign");
        private static readonly int P_Tex = Shader.PropertyToID("_GlassTex");
        private static readonly int P_ShadowStrength = Shader.PropertyToID("_GlassShadowStrength");
        private static readonly int P_UseVanillaShadow = Shader.PropertyToID("_GlassUseVanillaShadow");
        private static readonly int P_ShadowLow = Shader.PropertyToID("_GlassShadowLow");
        private static readonly int P_ShadowHigh = Shader.PropertyToID("_GlassShadowHigh");
        private static readonly int P_GodrayDensity = Shader.PropertyToID("_GlassGodrayDensity");
        private static readonly int P_GodrayMaxDist = Shader.PropertyToID("_GlassGodrayMaxDist");

        // Real sun-shadow map — scene depth from the sun direction around the player.
        // Tight coverage (higher effective resolution near you) = sharper block +
        // less self-shadow acne, so ShadowStrength can go to 1 for a hard block.
        // Bias MUST be smaller than the player's front-to-back thickness (~0.3 m) or the
        // sun-column occlusion can't self-shadow the player's back — that residual bias
        // gap was the leftover colour bleeding onto the back after the side test. Tune
        // live with `va_glass shadowbias` (lower = tighter contact / less bleed, higher
        // = less self-shadow speckle).
        public static float ShadowBias = 0.2f;
        private const string DepthShaderName = "Custom/FiresGlassDepth";
        private const int ShadowRes = 2048;
        private const float ShadowFar = 70f;
        private const float ShadowHalf = 15f;
        private Camera m_shadowCam;
        private RenderTexture m_shadowRT;
        private Shader m_depthShader;
        private static readonly List<Renderer> s_hiddenWinRenderers = new List<Renderer>();
        private static readonly int P_ShadowMap = Shader.PropertyToID("_GlassShadowMap");
        private static readonly int P_ShadowVP = Shader.PropertyToID("_GlassShadowVP");
        private static readonly int P_ShadowCamPos = Shader.PropertyToID("_GlassShadowCamPos");
        private static readonly int P_ShadowFwd = Shader.PropertyToID("_GlassShadowFwd");
        private static readonly int P_DepthFar = Shader.PropertyToID("_GlassDepthFar");
        private static readonly int P_ShadowBias = Shader.PropertyToID("_GlassShadowBias");

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            Vector3 sun = CurrentSunTravel();
            float dayMask = Mathf.Clamp01(-sun.y * 4f);

            // Neon signs are self-lit: they keep the pass alive at night (dayMask 0), when
            // sun windows alone would tear it down. Their in-shader gate ignores dayMask.
            bool anyNeon = false;
            foreach (var nw in FiresGlassWindow.Registry)
                if (nw != null && nw.Neon) { anyNeon = true; break; }

            if (!Active || cam == null || (dayMask <= 0.001f && !anyNeon)
                || FiresGlassWindow.Registry.Count == 0 || !EnsureMaterial())
            {
                Teardown();
                return;
            }

            if (m_cam != cam) { Teardown(); Hook(cam); }
            if (m_cam == null) return;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;

            if (m_projTex == null) m_projTex = LoadRealImage(CurrentImage) ?? GetTestPattern();
            if (m_atlas == null) m_atlas = FiresWaterShaderLoader.LoadBundleAsset<Texture2D>("FiresGlassProjAtlas");

            // Nearest N windows to the player.
            var p = Player.m_localPlayer;
            Vector3 anchor = p != null ? p.transform.position : cam.transform.position;
            m_sorted.Clear();
            foreach (var w in FiresGlassWindow.Registry) if (w != null) m_sorted.Add(w);
            m_sorted.Sort((a, b) => (a.Center - anchor).sqrMagnitude.CompareTo((b.Center - anchor).sqrMagnitude));
            int count = Mathf.Min(m_sorted.Count, MAXW);
            for (int i = 0; i < count; i++)
            {
                var w = m_sorted[i];
                m_centers[i] = w.Center;
                m_normals[i] = w.Normal.normalized;
                m_rights[i] = w.Right.normalized;
                m_ups[i] = w.Up.normalized;
                // halfs.z carries the NEON flag into the shader (per-window self-lit beam).
                m_halfs[i] = new Vector4(Mathf.Max(w.Half.x, 0.05f), Mathf.Max(w.Half.y, 0.05f), w.Neon ? 1f : 0f, 0f);
                // A window only projects the SUN's light if the sun actually reaches it — if a roof/wall/rock/
                // tree blocks the sun above the window, it casts nothing (an unlit window shows no reflection).
                // A NEON sign is its own light: always "lit", roof or not, so skip the sun raycast.
                w.SunLit = w.Neon || IsWindowSunlit(w, sun) ? 1f : 0f;
                m_tints[i] = (Vector4)(w.Tint * w.SunLit);
                int idx = Mathf.Clamp(w.ImageIndex, 0, AtlasCols * AtlasRows - 1);
                m_uvs[i] = new Vector4((idx % AtlasCols) / (float)AtlasCols, (idx / AtlasCols) / (float)AtlasRows,
                                       1f / AtlasCols, 1f / AtlasRows);
            }

            m_mat.SetVectorArray(P_Centers, m_centers);
            m_mat.SetVectorArray(P_Normals, m_normals);
            m_mat.SetVectorArray(P_Rights, m_rights);
            m_mat.SetVectorArray(P_Ups, m_ups);
            m_mat.SetVectorArray(P_Halfs, m_halfs);
            m_mat.SetVectorArray(P_Tints, m_tints);
            m_mat.SetVectorArray(P_UV, m_uvs);
            m_mat.SetInt(P_Count, count);

            if (m_atlas != null) { m_mat.SetTexture(P_ProjAtlas, m_atlas); m_mat.SetFloat(P_UseAtlas, 1f); }
            else m_mat.SetFloat(P_UseAtlas, 0f);

            m_mat.SetVector(P_Sun, sun);
            m_mat.SetFloat(P_Intensity, Intensity);
            m_mat.SetFloat(P_Reach, Reach);
            m_mat.SetFloat(P_Softness, Softness);
            m_mat.SetFloat(P_DayMask, dayMask);
            m_mat.SetFloat(P_Debug, DebugView ? 1f : 0f);
            m_mat.SetFloat(P_ShadowDebug, ShadowDebug ? 1f : 0f);
            m_mat.SetFloat(P_ShadowStrength, ShadowStrength);
            m_mat.SetFloat(P_ShadowLow, ShadowLow);
            m_mat.SetFloat(P_ShadowHigh, ShadowHigh);
            m_mat.SetFloat(P_GodrayDensity, GodrayDensity);
            m_mat.SetFloat(P_GodrayMaxDist, GodrayMaxDistance);
            m_mat.SetFloat(P_ShadowBias, ShadowBias);
            m_mat.SetFloat(P_UseVanillaShadow, UseVanillaShadow ? 1f : 0f);
            float neonRad = NeonCastAngleDeg * Mathf.Deg2Rad;
            m_mat.SetFloat(P_NeonTiltCos, Mathf.Cos(neonRad));
            m_mat.SetFloat(P_NeonTiltSin, Mathf.Sin(neonRad));
            m_mat.SetFloat(P_NeonCast, Mathf.Max(0f, NeonCastIntensity));
            m_mat.SetFloat(P_NeonBeamSign, NeonBeamSign >= 0f ? 1f : -1f);
            if (m_projTex != null) m_mat.SetTexture(P_Tex, m_projTex);

            // Occlusion: with UseVanillaShadow the fullscreen pass samples the game's own
            // screen-space sun shadow — via our light-event copy, since Unity's raw
            // _ShadowMapTexture temporary is already recycled by AfterForwardAlpha (sampling
            // it read "fully lit" everywhere: the vanilla toggle silently did nothing).
            // Otherwise render our custom map so the player blocks the beam per window.
            if (!UseVanillaShadow)
            {
                TeardownMaskCopy();
                RenderSunShadow(sun, anchor);
            }
            else
                EnsureMaskCopy();
        }

        // Copy the screen-space directional shadow mask into our own frame-temporary at the
        // one point in the frame it's valid (AfterScreenspaceMask on the dir light), publish
        // as _FiresScreenShadowMask. MaskOn=0 → the shader falls back to the legacy sample.
        private void EnsureMaskCopy()
        {
            Light dir = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            if (dir == null || dir.shadows == LightShadows.None)
            {
                Shader.SetGlobalFloat(P_MaskOn, 0f);
                return;
            }
            if (m_maskCB == null)
            {
                m_maskCB = new CommandBuffer { name = "FiresGlassShadowMaskCopy" };
                // Linear, explicitly — the default read/write is sRGB here, and R8-sRGB is not a
                // valid render target on D3D11 (RenderTexture.Create failed spam + a white mask
                // = the cast showed full colour). The mask is data, not colour, anyway.
                m_maskCB.GetTemporaryRT(P_MaskTmp, -1, -1, 0, FilterMode.Bilinear,
                    RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                m_maskCB.Blit(BuiltinRenderTextureType.CurrentActive, P_MaskTmp);
                m_maskCB.SetGlobalTexture(P_MaskGlobal, P_MaskTmp);
            }
            if (m_maskLight != dir)
            {
                if (m_maskLight != null) m_maskLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, m_maskCB);
                dir.AddCommandBuffer(LightEvent.AfterScreenspaceMask, m_maskCB);
                m_maskLight = dir;
            }
            Shader.SetGlobalFloat(P_MaskOn, 1f);
        }

        private void TeardownMaskCopy()
        {
            if (m_maskLight != null && m_maskCB != null)
                m_maskLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, m_maskCB);
            m_maskLight = null;
            Shader.SetGlobalFloat(P_MaskOn, 0f);
        }

        // Renders scene depth from the SUN direction over a region around the player
        // into an RFloat map (each texel = normalised distance from the shadow camera
        // to the nearest surface in that sun-column). Cleared to FAR so empty columns
        // never read as occluders (the bug that blanked the whole cast last time).
        private void RenderSunShadow(Vector3 sun, Vector3 anchor)
        {
            if (ShadowStrength <= 0.001f) return;
            if (m_depthShader == null)
            {
                m_depthShader = FiresWaterShaderLoader.LoadBundleShader(DepthShaderName, DepthShaderName);
                if (m_depthShader == null) return;
            }
            if (m_shadowRT == null)
            {
                m_shadowRT = new RenderTexture(ShadowRes, ShadowRes, 24, RenderTextureFormat.RFloat)
                { name = "FiresGlassSunShadow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                m_shadowRT.Create();
            }
            if (m_shadowCam == null)
            {
                var go = new GameObject("_FiresGlassSunShadowCam");
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                m_shadowCam = go.AddComponent<Camera>();
                m_shadowCam.enabled = false;
                m_shadowCam.orthographic = true;
                m_shadowCam.allowHDR = false;
                m_shadowCam.allowMSAA = false;
                m_shadowCam.useOcclusionCulling = false;
                m_shadowCam.targetTexture = m_shadowRT;
                // Clear color to FAR (white) so empty sun-columns read as "no
                // occluder" — clearing to 0 is what blanked the whole cast before.
                m_shadowCam.clearFlags = CameraClearFlags.SolidColor;
                m_shadowCam.backgroundColor = Color.white;

                // OCCLUDER-ONLY map via an ALLOWLIST: render ONLY true solid blockers — characters (player +
                // creatures), build pieces (walls/roofs) and static_solid (rocks). NOT terrain and NOT the grass
                // carpet: those SURFACES would shadow themselves (every ground pixel reads "occluded by the blade
                // just above it"), which painted the whole coverage box red. With only real occluders present,
                // the ground behind the player is occluded BY the player — exactly the shadow we want, nothing else.
                m_shadowCam.cullingMask = LayerMask.GetMask("character", "character_net", "character_ghost", "piece", "static_solid");
            }

            Vector3 camPos = anchor - sun * (ShadowFar * 0.5f);   // half the far-plane up the sun ray
            Vector3 up = Mathf.Abs(sun.y) > 0.98f ? Vector3.forward : Vector3.up;
            m_shadowCam.transform.position = camPos;
            m_shadowCam.transform.rotation = Quaternion.LookRotation(sun, up);
            m_shadowCam.orthographicSize = ShadowHalf;
            m_shadowCam.aspect = 1f;
            m_shadowCam.nearClipPlane = 0.5f;
            m_shadowCam.farClipPlane = ShadowFar;

            Shader.SetGlobalFloat(P_DepthFar, ShadowFar);

            // Exclude the glass windows themselves — a window must NOT cast a shadow onto its OWN projection
            // area (that was the clean red parallelogram in the debug view: the pane blocking the very ground it
            // should be lighting). Hide their renderers for just this synchronous shadow render, then restore
            // BEFORE the main camera draws this frame.
            s_hiddenWinRenderers.Clear();
            foreach (var w in FiresGlassWindow.Registry)
            {
                if (w == null) continue;
                foreach (var r in w.GetComponentsInChildren<Renderer>())
                    if (r != null && r.enabled) { r.enabled = false; s_hiddenWinRenderers.Add(r); }
            }
            try { m_shadowCam.RenderWithShader(m_depthShader, ""); }
            finally { for (int k = 0; k < s_hiddenWinRenderers.Count; k++) if (s_hiddenWinRenderers[k] != null) s_hiddenWinRenderers[k].enabled = true; }

            // SAMPLING matrix — renderIntoTexture must be FALSE here. Unity already bakes the
            // D3D11 y-flip into the projection it renders the RT with, which leaves the texture
            // in standard sample orientation (V up). Reprojecting with the FLIPPED projection
            // (true) mirrored the lookup vertically — in this player-centred map the occlusion
            // landed mirrored across the player, so the fake shadow sat on the WRONG SIDE of
            // you, visibly disagreeing with the real sun shadow.
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(m_shadowCam.projectionMatrix, false) * m_shadowCam.worldToCameraMatrix;
            Shader.SetGlobalTexture(P_ShadowMap, m_shadowRT);
            Shader.SetGlobalMatrix(P_ShadowVP, vp);
            Shader.SetGlobalVector(P_ShadowCamPos, camPos);
            Shader.SetGlobalVector(P_ShadowFwd, sun);
        }

        private static Vector3 CurrentSunTravel()
        {
            Vector3 sun;
            if (EnvMan.instance != null) sun = EnvMan.instance.GetSunDirection();
            else { Vector4 g = Shader.GetGlobalVector("_SkyboxSunDir"); sun = -new Vector3(g.x, g.y, g.z); }
            if (sun.sqrMagnitude < 1e-6f) sun = Vector3.down;
            return sun.normalized;
        }

        // True if the SUN reaches this window (nothing solid between the pane and the sky along the sun ray).
        // A roof/wall/rock/tree over the window blocks it -> the window casts no sunlight. Grass never blocks
        // (instanced clutter has no colliders, so a raycast passes through it).
        private static int s_sunBlockMask = -1;
        private static bool IsWindowSunlit(FiresGlassWindow w, Vector3 sun)
        {
            if (s_sunBlockMask == -1) s_sunBlockMask = LayerMask.GetMask("piece", "static_solid", "terrain", "Default");
            Vector3 toSun = -sun;                        // sun travel dir is sun->ground; toward the sun is -sun
            Vector3 origin = w.Center + toSun * 0.75f;   // start clear of the pane's own collider
            return !Physics.Raycast(origin, toSun, 60f, s_sunBlockMask, QueryTriggerInteraction.Ignore);
        }

        private bool EnsureMaterial()
        {
            if (m_mat != null) return true;
            if (m_shader == null)
            {
                m_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                if (m_shader == null)
                {
                    if (!m_warned)
                    {
                        Debug.LogWarning("[FiresGlassProjector] Custom/FiresGlassProjection not in the tossinshade "
                            + "bundle — stained-glass projection off. Add the shader and rebuild.");
                        m_warned = true;
                    }
                    return false;
                }
            }
            m_mat = new Material(m_shader);
            s_matLoaded = true;
            FiresLogger.LogVerbose("[FiresGlassProjector] Custom/FiresGlassProjection loaded — projection active.");
            return true;
        }

        private void Hook(Camera cam)
        {
            m_cam = cam;
            m_cam.depthTextureMode |= DepthTextureMode.Depth;
            m_cb = new CommandBuffer { name = "FiresGlassProjection" };
            int tmp = Shader.PropertyToID("_FiresGlassScene");
            m_cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear);
            m_cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp);
            m_cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, m_mat);
            m_cb.ReleaseTemporaryRT(tmp);
            m_cam.AddCommandBuffer(Evt, m_cb);
            s_passHooked = true;
        }

        private void Teardown()
        {
            if (m_cam != null && m_cb != null) m_cam.RemoveCommandBuffer(Evt, m_cb);
            m_cb = null;
            m_cam = null;
            s_passHooked = false;
            TeardownMaskCopy();
        }

        private void OnEnable() { Camera.onPreRender += OnCamPreRender; }
        private void OnDisable() { Camera.onPreRender -= OnCamPreRender; Teardown(); }
        private void OnDestroy()
        {
            Camera.onPreRender -= OnCamPreRender;
            Teardown();
            if (m_shadowCam != null) Object.Destroy(m_shadowCam.gameObject);
            if (m_shadowRT != null) { m_shadowRT.Release(); Object.Destroy(m_shadowRT); }
        }

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

        // ── Test helpers ─────────────────────────────────────────────────────

        // Plant a mesh-less test window in front of the player, facing the sun.
        public static string PlantAtPlayer()
        {
            var p = Player.m_localPlayer;
            if (p == null) return "No local player.";
            Vector3 sun = CurrentSunTravel();
            Vector3 n = -sun; if (n.sqrMagnitude < 1e-6f) n = Vector3.up; n.Normalize();
            Vector3 up = Vector3.up - n * Vector3.Dot(Vector3.up, n);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.forward - n * Vector3.Dot(Vector3.forward, n);
            up.Normalize();
            Vector3 fwd = p.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();

            var go = new GameObject("FiresGlassTestWindow");
            go.SetActive(false);
            var win = go.AddComponent<FiresGlassWindow>();
            win.Manual = true;
            win.Center = p.transform.position + Vector3.up * 2.0f + fwd * 2.0f;
            win.Normal = n;
            win.Up = up;
            win.Right = Vector3.Normalize(Vector3.Cross(up, n));
            win.Half = new Vector2(1.1f, 1.6f);
            win.Tint = Color.white;
            go.transform.position = win.Center;
            go.SetActive(true);   // OnEnable registers with the manual plane
            s_testWindows.Add(go);
            Active = true;
            return $"Planted test window at {win.Center}. Registry now {FiresGlassWindow.Registry.Count}.";
        }

        // Spawn the flat pane prefab (carries a FiresGlassWindow via the piece setup).
        public static string SpawnWindow()
        {
            var p = Player.m_localPlayer;
            if (p == null) return "No local player.";
            var src = FiresWaterShaderLoader.LoadBundleModel("FiresGlassWindow");
            if (src == null) return "FiresGlassWindow prefab not in bundle.";
            Vector3 fwd = p.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
            // Face the pane at the sun (front normal = toward sun) so the beam
            // casts down onto the ground on the shadow side.
            Vector3 sun = CurrentSunTravel();
            Vector3 n = -sun; if (n.sqrMagnitude < 1e-6f) n = Vector3.forward; n.Normalize();
            Vector3 up = Vector3.up - n * Vector3.Dot(Vector3.up, n);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.forward - n * Vector3.Dot(Vector3.forward, n);
            up.Normalize();
            var go = Object.Instantiate(src);
            go.transform.position = p.transform.position + Vector3.up * 1.6f + fwd * 3.2f;
            go.transform.rotation = Quaternion.LookRotation(n, up);   // +Z (pane normal) toward the sun
            if (go.GetComponent<FiresGlassWindow>() == null) go.AddComponent<FiresGlassWindow>();
            s_testWindows.Add(go);
            Active = true;
            return $"Spawned pane. Registry now {FiresGlassWindow.Registry.Count}.";
        }

        public static string ClearTestWindows()
        {
            int n = s_testWindows.Count;
            foreach (var g in s_testWindows) if (g != null) Object.Destroy(g);
            s_testWindows.Clear();
            return $"cleared {n} test window(s)";
        }

        public static string SetImage(string name)
        {
            CurrentImage = name;
            return $"projection image = '{name}' (applies next frame)";
        }

        private static Texture2D LoadRealImage(string name)
            => FiresWaterShaderLoader.LoadBundleAsset<Texture2D>(name);

        private static Texture2D GetTestPattern()
        {
            if (s_testPattern == null) s_testPattern = BuildTestPattern();
            return s_testPattern;
        }

        private static Texture2D BuildTestPattern()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false, false)
            { name = "FiresGlassTestPattern", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                float fy = y / (float)(S - 1);
                for (int x = 0; x < S; x++)
                {
                    float fx = x / (float)(S - 1);
                    Color c;
                    if (fx < 0.5f && fy >= 0.5f) c = new Color(0.90f, 0.15f, 0.15f);
                    else if (fx >= 0.5f && fy >= 0.5f) c = new Color(0.15f, 0.35f, 0.95f);
                    else if (fx < 0.5f) c = new Color(0.15f, 0.80f, 0.25f);
                    else c = new Color(0.98f, 0.85f, 0.20f);
                    float rx = fx * 4f; rx -= Mathf.Floor(rx);
                    float ry = fy * 4f; ry -= Mathf.Floor(ry);
                    float lead = Mathf.Min(Mathf.Abs(rx - 0.5f), Mathf.Abs(ry - 0.5f));
                    bool border = fx < 0.03f || fx > 0.97f || fy < 0.03f || fy > 0.97f;
                    bool cross = Mathf.Abs(fx - 0.5f) < 0.018f || Mathf.Abs(fy - 0.5f) < 0.018f;
                    if (lead > 0.44f || border || cross) c *= 0.08f;
                    px[y * S + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        // ── Console command ──────────────────────────────────────────────────

        private static bool s_reg;

        public static void Register()
        {
            if (s_reg) return;
            s_reg = true;
            new Terminal.ConsoleCommand("va_glass",
                "FiresGlass projection. Usage: va_glass on|off|status|debug|here|spawn|clear|img <name>|list|reach <m>|intensity <x>|shadow <0..1>|shadowlow <0..1>|shadowhigh <0..1>|godray <density>",
                args =>
                {
                    // 0.3.158 (Fire 10-08): world-changing, admin only
                    if (ZNet.instance != null && !ZNet.instance.LocalPlayerIsAdminOrHost()) { args.Context?.AddString("[va_glass] admin only"); return; }
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    switch (a)
                    {
                        case "on": Active = true; args.Context.AddString("[va_glass] projection ON"); break;
                        case "off": Active = false; args.Context.AddString("[va_glass] projection OFF"); break;
                        case "debug": DebugView = !DebugView; args.Context.AddString($"[va_glass] debug view = {DebugView}"); break;
                        case "shadowdebug": ShadowDebug = !ShadowDebug; args.Context.AddString($"[va_glass] shadow debug = {ShadowDebug} (RED = sun-shadow map says occluded)"); break;
                        case "here": args.Context.AddString("[va_glass] " + PlantAtPlayer()); break;
                        case "spawn": args.Context.AddString("[va_glass] " + SpawnWindow()); break;
                        case "clear": args.Context.AddString("[va_glass] " + ClearTestWindows()); break;
                        case "list": args.Context.AddString("[va_glass] images: " + string.Join(", ", ImageNames) + $" (current: {CurrentImage})"); break;
                        case "img":
                            if (args.Length > 2) args.Context.AddString("[va_glass] " + SetImage(args[2]));
                            else args.Context.AddString($"[va_glass] current image = {CurrentImage}");
                            break;
                        case "reach":
                            if (args.Length > 2 && float.TryParse(args[2], out float r)) Reach = Mathf.Max(0.5f, r);
                            args.Context.AddString($"[va_glass] reach = {Reach} m");
                            break;
                        case "intensity":
                            if (args.Length > 2 && float.TryParse(args[2], out float it)) Intensity = Mathf.Max(0f, it);
                            args.Context.AddString($"[va_glass] intensity = {Intensity}");
                            break;
                        case "shadow":
                            if (args.Length > 2 && float.TryParse(args[2], out float sh)) ShadowStrength = Mathf.Clamp01(sh);
                            args.Context.AddString($"[va_glass] shadow block = {ShadowStrength} (1 = hard block). bias={ShadowBias}");
                            break;
                        case "shadowbias":
                            if (args.Length > 2 && float.TryParse(args[2], out float sbias)) ShadowBias = Mathf.Max(0f, sbias);
                            args.Context.AddString($"[va_glass] shadow bias = {ShadowBias} m (raise to kill self-shadow speckle, lower for tighter contact)");
                            break;
                        case "shadowlow":
                            if (args.Length > 2 && float.TryParse(args[2], out float sl)) ShadowLow = Mathf.Clamp01(sl);
                            args.Context.AddString($"[va_glass] shadow low = {ShadowLow}");
                            break;
                        case "shadowhigh":
                            if (args.Length > 2 && float.TryParse(args[2], out float shi)) ShadowHigh = Mathf.Clamp01(shi);
                            args.Context.AddString($"[va_glass] shadow high = {ShadowHigh}");
                            break;
                        case "godray":
                            if (args.Length > 2 && float.TryParse(args[2], out float gr)) GodrayDensity = Mathf.Max(0f, gr);
                            args.Context.AddString($"[va_glass] godray density = {GodrayDensity}");
                            break;
                        default:
                            Vector3 sun = CurrentSunTravel();
                            args.Context.AddString($"[va_glass] active={Active} windows={FiresGlassWindow.Registry.Count} passHooked={s_passHooked} debug={DebugView} reach={Reach} intensity={Intensity}");
                            args.Context.AddString($"[va_glass] sun={sun} (y<0=above horizon) dayMask={Mathf.Clamp01(-sun.y * 4f):F2} img={CurrentImage} matLoaded={s_matLoaded}");
                            int shown = 0;
                            foreach (var w in FiresGlassWindow.Registry)
                            {
                                if (w == null || shown >= 3) continue;
                                float ld = w.Normal.sqrMagnitude > 1e-6f ? Vector3.Dot(sun, w.Normal.normalized) : 0f;
                                args.Context.AddString($"[va_glass]  win '{w.name}' C={w.Center} N={w.Normal.normalized} half=({w.Half.x:F2},{w.Half.y:F2}) L·N={ld:F2} tint={w.Tint}");
                                shown++;
                            }
                            break;
                    }
                }, isCheat: false);
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Terminal_InitTerminal_RegisterGlass
        {
            private static void Postfix() => Register();
        }
    }
}
