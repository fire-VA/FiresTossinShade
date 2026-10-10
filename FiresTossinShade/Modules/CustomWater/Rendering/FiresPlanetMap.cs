using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// The planet's surface is the real world map: the game's own large-map material (what M shows: biomes, forests,
    /// relief, water, what you have explored and the fog over the rest) drawn into a texture framed on the whole world
    /// out to the water edge. Redrawn while the planet is in view, so new exploration shows up on it. No-map worlds
    /// get no map, exactly like M.
    /// </summary>
    public static class FiresPlanetMap
    {
        public static float RefreshSeconds = 2f;

        private const int Resolution = 2048;
        private const int AnisoLevel = 4;
        private const float VanillaPixelSizeAtZoomOne = 200f;
        private static readonly int ZoomId = Shader.PropertyToID("_zoom");
        private static readonly int PixelSizeId = Shader.PropertyToID("_pixelSize");
        private static readonly int MapCenterId = Shader.PropertyToID("_mapCenter");
        private static readonly int ColorMaskId = Shader.PropertyToID("_ColorMask");
        private static readonly float WriteColorOnly = (int)(ColorWriteMask.Red | ColorWriteMask.Green | ColorWriteMask.Blue);

        private static RenderTexture s_target;
        private static Material s_material;
        private static Minimap s_drawnFor;
        private static Minimap s_reportedFor;
        private static float s_nextDraw;

        /// <summary>The drawn map for the current world, or null when there is nothing to show.</summary>
        public static Texture Current =>
            s_drawnFor != null && s_drawnFor == Minimap.instance && !Game.m_noMap && s_target.IsCreated() ? s_target : null;

        public static void Tick(bool planetInView)
        {
            if (!planetInView)
            {
                s_nextDraw = 0f;
                return;
            }
            if (s_target != null && !s_target.IsCreated()) s_nextDraw = 0f;
            if (Game.m_noMap || Time.unscaledTime < s_nextDraw) return;
            s_nextDraw = Time.unscaledTime + RefreshSeconds;

            var minimap = Minimap.instance;
            var source = minimap != null && minimap.m_mapImageLarge != null ? minimap.m_mapImageLarge.material : null;
            if (source != null) Draw(minimap, source);
        }

        private static void Draw(Minimap minimap, Material source)
        {
            if (s_material == null || s_material.shader != source.shader)
            {
                if (s_material != null) Object.Destroy(s_material);
                s_material = new Material(source) { name = "FiresPlanetMap" };
            }
            else
            {
                s_material.CopyPropertiesFromMaterial(source);
            }

            float halfUv = WorldGenerator.waterEdge / (minimap.m_pixelSize * minimap.m_textureSize);
            float zoom = halfUv * 2f;
            s_material.SetFloat(ZoomId, zoom);
            s_material.SetFloat(PixelSizeId, VanillaPixelSizeAtZoomOne / zoom);
            s_material.SetVector(MapCenterId, Vector4.zero);
            // The map shader's alpha follows the fog but M never shows it; the planet blends by alpha, so keep it opaque.
            s_material.SetFloat(ColorMaskId, WriteColorOnly);

            EnsureTarget();
            var previous = RenderTexture.active;
            RenderTexture.active = s_target;
            GL.Clear(false, true, Color.black);
            GL.PushMatrix();
            GL.LoadOrtho();
            if (s_material.SetPass(0))
            {
                float lo = 0.5f - halfUv, hi = 0.5f + halfUv;
                GL.Begin(GL.QUADS);
                GL.TexCoord2(lo, lo); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(lo, hi); GL.Vertex3(0f, 1f, 0f);
                GL.TexCoord2(hi, hi); GL.Vertex3(1f, 1f, 0f);
                GL.TexCoord2(hi, lo); GL.Vertex3(1f, 0f, 0f);
                GL.End();
            }
            GL.PopMatrix();
            if (s_reportedFor != minimap) Report(minimap, halfUv);
            RenderTexture.active = previous;
            s_drawnFor = minimap;
        }

        private static void Report(Minimap minimap, float halfUv)
        {
            var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            probe.ReadPixels(new Rect(Resolution / 2, Resolution / 2, 1, 1), 0, 0);
            Color32 centre = probe.GetPixel(0, 0);
            Object.Destroy(probe);
            s_reportedFor = minimap;
            FiresLogger.LogInfo($"[FiresPlanet] showing the live M map ({s_material.shader.name}, {minimap.m_textureSize}px x "
                + $"{minimap.m_pixelSize}m, framed to {WorldGenerator.waterEdge}m = uv 0.5+-{halfUv:0.000}); world centre pixel "
                + $"rgb {centre.r},{centre.g},{centre.b}.");
        }

        private static void EnsureTarget()
        {
            if (s_target == null)
            {
                s_target = new RenderTexture(Resolution, Resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "FiresPlanetMap",
                    useMipMap = true,
                    autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = AnisoLevel,
                };
            }
            if (!s_target.IsCreated()) s_target.Create();
        }
    }
}
