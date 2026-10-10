using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// True planar reflection for FiresWater — a second "mirror camera" renders the scene
    /// reflected across the water plane into a RenderTexture, and the water shader samples
    /// it (screen-projected, ripple-distorted by the wave normal) as its reflection colour.
    ///
    /// This replaces the accidental "reflection" players used to see, which was really the
    /// REFRACTION grab offset by the wave normal accidentally sampling foreground objects
    /// (the player smeared sideways). A real mirror camera puts the reflection where physics
    /// says it goes and works at any zoom — unlike a screen-space trick, whose source has to
    /// stay on screen.
    ///
    /// Cost is one extra scene render per frame, so it self-gates hard: off unless the water
    /// shader is live, the camera is within <see cref="MaxHeightAboveWater"/> of the surface,
    /// and it renders at <see cref="Resolution"/> scale through a trimmable cull mask.
    /// </summary>
    public sealed class FiresPlanarReflection : MonoBehaviour
    {
        // Pushed from ConfigManager via FiresTossinShade.ApplyFiresWaterConfig.
        public static bool  Enabled = true;
        public static float Resolution = 0.5f;          // RT scale vs screen (0.25..1)
        public static float Distort = 0.02f;            // wave-normal ripple on the reflection sample
        public static float MaxHeightAboveWater = 80f;  // tear down above this (flying / peaks / space)
        public static float FarClip = 1500f;            // reflection camera far plane (perf)
        // Force cheaper LODs during the mirror pass — the reflection is
        // blurred + wave-distorted, so the detail drop is invisible, but it
        // roughly halves the geometry cost of the reflection (dominated by the
        // forest). 1 = full detail (off); 0.5 ≈ trees swap to their next LOD.
        public static float ReflectionLodBias = 0.5f;
        public static bool  ReflectTerrain = true;      // include terrain in the mirror (heavier)

        // Distance-based reflection softening (see FiresWater.shader). Near water is a crisp
        // mirror; far water (clipmap) blurs + ripples the reflection so it reads realistic
        // instead of razor-sharp to the horizon. Tunable live.
        public static float BlurStart   = 120f;   // camera dist (m) where softening begins
        public static float BlurRange   = 1000f;  // dist (m) over which it ramps to full
        public static float BlurMax     = 0.014f; // max UV blur radius (screen fraction) far
        public static float DistortGain = 4f;     // extra ripple multiplier far

        private const float ClipPlaneOffset = 0.05f;    // nudge the clip plane so the waterline doesn't self-clip
        // Extra height (m) added to the mirror's clip plane only — culls near-bank slivers
        // hugging the waterline from the reflection (the "stripe" across pond reflections).
        public static float BankClip = 0f;

        private static readonly int P_Tex     = Shader.PropertyToID("_FiresPlanarReflTex");
        private static readonly int P_On      = Shader.PropertyToID("_FiresPlanarReflOn");
        private static readonly int P_Distort = Shader.PropertyToID("_FiresPlanarReflDistort");
        private static readonly int P_TideOffset = Shader.PropertyToID("_FiresTideOffset");
        private static readonly int P_BlurStart   = Shader.PropertyToID("_FiresReflBlurStart");
        private static readonly int P_BlurRange   = Shader.PropertyToID("_FiresReflBlurRange");
        private static readonly int P_BlurMax     = Shader.PropertyToID("_FiresReflBlurMax");
        private static readonly int P_DistortGain = Shader.PropertyToID("_FiresReflDistortGain");

        private Camera m_reflCam;
        private RenderTexture m_rt;
        private int m_rtW, m_rtH;
        private bool m_rendering;   // recursion guard

        private void OnDisable()
        {
            Shader.SetGlobalFloat(P_On, 0f);
            Teardown();
        }

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (!Enabled || cam == null || !FiresWaterShaderLoader.Enabled)
            {
                Off();
                return;
            }

            float waterY = ZoneSystem.c_WaterLevel + Shader.GetGlobalFloat(P_TideOffset);

            // Above the surface only, and not so high the reflection isn't worth a full render.
            float camY = cam.transform.position.y;
            if (camY < waterY - 1f || camY > waterY + MaxHeightAboveWater)
            {
                Off();
                return;
            }

            RenderReflection(cam, waterY);
        }

        private void Off()
        {
            Shader.SetGlobalFloat(P_On, 0f);
            // Keep the RT/camera allocated a little — cheap — but if disabled entirely, tear down.
            if (!Enabled) Teardown();
        }

        private void RenderReflection(Camera cam, float waterY)
        {
            if (m_rendering) return;   // never let the mirror camera trigger itself

            EnsureResources(cam);
            if (m_reflCam == null || m_rt == null) { Off(); return; }

            // Mirror plane: horizontal at the water surface, facing up.
            Vector3 planePos = new Vector3(0f, waterY, 0f);
            Vector3 planeNormal = Vector3.up;

            // Copy the live main-camera settings each frame (fov / aspect can change).
            m_reflCam.CopyFrom(cam);
            m_reflCam.cullingMask = BuildCullMask(cam);
            m_reflCam.targetTexture = m_rt;
            m_reflCam.farClipPlane = Mathf.Min(cam.farClipPlane, FarClip);
            m_reflCam.enabled = false;                 // we drive it manually
            // Clear to the atmospheric HAZE colour, NOT the procedural skybox. Rendering
            // Custom/SkyboxProcedural through this camera's mirrored + oblique matrices makes
            // its world-height fog band (which the FiresSkybox override rewrites) reconstruct
            // unstably right at the horizon — the flicker that only appears with the skybox
            // override on, worst at grazing/distant angles. A flat haze clear decouples the
            // reflection from that entirely; the cloud mesh and geometry still reflect on top,
            // and grazing water reflecting the hazy horizon reads correct anyway.
            m_reflCam.clearFlags = CameraClearFlags.SolidColor;
            m_reflCam.backgroundColor = RenderSettings.fogColor;
            m_reflCam.depthTextureMode = DepthTextureMode.None;
            m_reflCam.allowHDR = cam.allowHDR;

            // Reflection matrix across the plane, applied to the main view matrix.
            float d = -Vector3.Dot(planeNormal, planePos) - ClipPlaneOffset;
            Vector4 reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, d);
            Matrix4x4 reflection = CalculateReflectionMatrix(reflectionPlane);

            m_reflCam.worldToCameraMatrix = cam.worldToCameraMatrix * reflection;

            // Oblique near-clip AT the water so nothing below the surface leaks into the mirror.
            // BankClip raises ONLY this clip plane (the mirror matrix above stays at the true
            // water level): geometry hugging the waterline — the near bank crest that smears
            // into a stripe across a pond's reflection — stops reflecting, while trees/hills
            // well above the water keep their reflections. 0 = exact waterline (off).
            Vector3 clipPos = planePos + Vector3.up * Mathf.Max(0f, BankClip);
            Vector4 clipPlane = CameraSpacePlane(m_reflCam, clipPos, planeNormal, 1f);
            m_reflCam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);

            // Position the transform too, for any position-dependent shading in the mirror pass.
            m_reflCam.transform.position = reflection.MultiplyPoint(cam.transform.position);

            // BREAK THE FEEDBACK LOOP: the FTS water meshes (clipmap ocean / extended ring)
            // live on the Default layer, not "Water", so the mirror camera renders them too.
            // If planar sampling is on while they render INTO the RT, they read the RT being
            // written — a recursive mirror that strobes. Force it off for the reflection pass
            // so water-in-the-mirror falls back to the cheap sky cubemap; restore it after for
            // the real (main-camera) pass.
            Shader.SetGlobalFloat(P_On, 0f);

            m_rendering = true;
            GL.invertCulling = true;          // reflection flips triangle winding
            float savedLod = QualitySettings.lodBias;
            float rlb = Mathf.Clamp(ReflectionLodBias, 0.1f, 1f);
            if (rlb < 0.999f) QualitySettings.lodBias = savedLod * rlb;
            m_reflCam.Render();
            QualitySettings.lodBias = savedLod;
            GL.invertCulling = false;
            m_rendering = false;

            Shader.SetGlobalTexture(P_Tex, m_rt);
            Shader.SetGlobalFloat(P_Distort, Distort);
            Shader.SetGlobalFloat(P_BlurStart, BlurStart);
            Shader.SetGlobalFloat(P_BlurRange, BlurRange);
            Shader.SetGlobalFloat(P_BlurMax, BlurMax);
            Shader.SetGlobalFloat(P_DistortGain, DistortGain);
            Shader.SetGlobalFloat(P_On, 1f);
        }

        private int BuildCullMask(Camera cam)
        {
            int mask = cam.cullingMask;
            int water = LayerMask.NameToLayer("Water");
            int ui = LayerMask.NameToLayer("UI");
            if (water >= 0) mask &= ~(1 << water);   // never reflect water (would recurse visually)
            if (ui >= 0)    mask &= ~(1 << ui);
            if (!ReflectTerrain)
            {
                int terrain = LayerMask.NameToLayer("terrain");
                if (terrain >= 0) mask &= ~(1 << terrain);
            }
            return mask;
        }

        private void EnsureResources(Camera cam)
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(cam.pixelWidth  * Mathf.Clamp(Resolution, 0.25f, 1f)));
            int h = Mathf.Max(16, Mathf.RoundToInt(cam.pixelHeight * Mathf.Clamp(Resolution, 0.25f, 1f)));

            if (m_rt == null || m_rtW != w || m_rtH != h)
            {
                if (m_rt != null) { m_rt.Release(); Destroy(m_rt); }
                m_rt = new RenderTexture(w, h, 16, RenderTextureFormat.DefaultHDR)
                {
                    name = "_FiresPlanarReflRT",
                    antiAliasing = 1,
                    useMipMap = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                m_rt.Create();
                m_rtW = w; m_rtH = h;
            }

            if (m_reflCam == null)
            {
                var go = new GameObject("_FiresPlanarReflectionCam");
                go.hideFlags = HideFlags.HideAndDontSave;
                m_reflCam = go.AddComponent<Camera>();
                m_reflCam.enabled = false;
            }
        }

        private void Teardown()
        {
            if (m_reflCam != null) { Destroy(m_reflCam.gameObject); m_reflCam = null; }
            if (m_rt != null) { m_rt.Release(); Destroy(m_rt); m_rt = null; }
            m_rtW = m_rtH = 0;
        }

        // Standard reflection matrix about a plane (Unity MirrorReflection reference math).
        private static Matrix4x4 CalculateReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = default;
            m.m00 = 1f - 2f * plane.x * plane.x;
            m.m01 = -2f * plane.x * plane.y;
            m.m02 = -2f * plane.x * plane.z;
            m.m03 = -2f * plane.w * plane.x;

            m.m10 = -2f * plane.y * plane.x;
            m.m11 = 1f - 2f * plane.y * plane.y;
            m.m12 = -2f * plane.y * plane.z;
            m.m13 = -2f * plane.w * plane.y;

            m.m20 = -2f * plane.z * plane.x;
            m.m21 = -2f * plane.z * plane.y;
            m.m22 = 1f - 2f * plane.z * plane.z;
            m.m23 = -2f * plane.w * plane.z;

            m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
            return m;
        }

        private static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            Vector3 offsetPos = pos + normal * ClipPlaneOffset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cpos = m.MultiplyPoint(offsetPos);
            Vector3 cnormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }
    }
}
