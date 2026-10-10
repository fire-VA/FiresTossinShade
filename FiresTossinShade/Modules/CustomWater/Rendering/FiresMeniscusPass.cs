using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Waterline "meniscus" — the thin dark band across the lens when the camera sits at the
    /// water surface (half-submerged), the detail that sells crossing the surface. Concept from
    /// Crest Water 4's underwater meniscus (MIT, (c) 2019 Wave Harmonic and contributors),
    /// reimplemented as a tiny fullscreen blit (Custom/FiresMeniscus) that bands around the
    /// screen line where the per-pixel view ray crosses the water plane.
    ///
    /// Water level comes from <c>FiresCore.Services.WaterState</c> — the Crest-style query API —
    /// so the band tracks tides/waves when the Galaxies provider is present and falls back to
    /// vanilla water otherwise. The pass only hooks while the camera is within
    /// <see cref="ActiveRange"/> of the surface; the rest of the time it is fully torn down.
    /// </summary>
    public class FiresMeniscusPass : MonoBehaviour
    {
        public static bool Enabled = true;
        public static float Strength = 0.35f;   // band opacity
        public static float Width = 0.012f;     // band half-width (normalized ray-Y units)
        public static Color Tint = new Color(0.05f, 0.12f, 0.12f, 1f);

        private const float ActiveRange = 1.2f;   // metres from the surface the pass runs within
        private const float LensDist = 0.35f;     // matches the near-plane "on the lens" reading
        private const string ShaderName = "Custom/FiresMeniscus";
        private const CameraEvent Evt = CameraEvent.AfterForwardAlpha;

        private Camera m_cam;
        private CommandBuffer m_cb;
        private Material m_mat;
        private Shader m_shader;
        private bool m_warned;

        private static readonly Rect s_fullView = new Rect(0f, 0f, 1f, 1f);
        private readonly Vector3[] m_corners = new Vector3[4];
        private static readonly int P_RayBL = Shader.PropertyToID("_MenRayBL");
        private static readonly int P_RayTL = Shader.PropertyToID("_MenRayTL");
        private static readonly int P_RayTR = Shader.PropertyToID("_MenRayTR");
        private static readonly int P_RayBR = Shader.PropertyToID("_MenRayBR");
        private static readonly int P_CamY = Shader.PropertyToID("_MenCamY");
        private static readonly int P_WaterY = Shader.PropertyToID("_MenWaterY");
        private static readonly int P_Dist = Shader.PropertyToID("_MenDist");
        private static readonly int P_Width = Shader.PropertyToID("_MenWidth");
        private static readonly int P_Color = Shader.PropertyToID("_MenColor");

        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (!Enabled || Strength <= 0.001f || cam == null || Player.m_localPlayer == null)
            {
                Teardown();
                return;
            }

            Vector3 camPos = cam.transform.position;
            float waterY = FiresCore.Services.WaterState.GetWaterSurface(camPos);
            if (Mathf.Abs(camPos.y - waterY) > ActiveRange)
            {
                Teardown();
                return;
            }

            if (!EnsureMaterial()) { Teardown(); return; }
            if (m_cam != cam) { Teardown(); Hook(cam); }
            if (m_cam == null) return;

            cam.CalculateFrustumCorners(s_fullView, cam.farClipPlane, Camera.MonoOrStereoscopicEye.Mono, m_corners);
            Transform t = cam.transform;
            m_mat.SetVector(P_RayBL, t.TransformVector(m_corners[0]));
            m_mat.SetVector(P_RayTL, t.TransformVector(m_corners[1]));
            m_mat.SetVector(P_RayTR, t.TransformVector(m_corners[2]));
            m_mat.SetVector(P_RayBR, t.TransformVector(m_corners[3]));
            m_mat.SetFloat(P_CamY, camPos.y);
            m_mat.SetFloat(P_WaterY, waterY);
            m_mat.SetFloat(P_Dist, LensDist);
            m_mat.SetFloat(P_Width, Width);
            m_mat.SetColor(P_Color, new Color(Tint.r, Tint.g, Tint.b, Strength));
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
                        Debug.LogWarning("[FiresMeniscus] Custom/FiresMeniscus not in the tossinshade bundle — waterline band off.");
                        m_warned = true;
                    }
                    return false;
                }
            }
            m_mat = new Material(m_shader);
            return true;
        }

        private void Hook(Camera cam)
        {
            m_cam = cam;
            m_cb = new CommandBuffer { name = "FiresMeniscus" };
            int tmp = Shader.PropertyToID("_FiresMeniscusScene");
            m_cb.GetTemporaryRT(tmp, -1, -1, 0, FilterMode.Bilinear);
            m_cb.Blit(BuiltinRenderTextureType.CameraTarget, tmp);
            m_cb.Blit(tmp, BuiltinRenderTextureType.CameraTarget, m_mat);
            m_cb.ReleaseTemporaryRT(tmp);
            m_cam.AddCommandBuffer(Evt, m_cb);
        }

        private void Teardown()
        {
            if (m_cam != null && m_cb != null) m_cam.RemoveCommandBuffer(Evt, m_cb);
            m_cb = null;
            m_cam = null;
        }

        private void OnDisable() => Teardown();
        private void OnDestroy() => Teardown();
    }
}
