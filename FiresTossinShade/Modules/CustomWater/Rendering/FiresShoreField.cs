using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Bakes a world-XZ "shore field" texture around the player from the procedural
    /// world height (WorldGenerator.GetHeight — cheap, collider-free, valid on
    /// unloaded zones) and pushes it to the FiresWater shader as globals:
    ///   R = wave attenuation, WELDED to vanilla (Clamp01(waterDepth / WaveFullDepth),
    ///       i.e. full waves at 10m depth) so the rendered wave height equals vanilla's
    ///       buoyancy/swim surface — the body floats on exactly what it looks like
    ///   G = normalized water depth (shallow → deep, drives the colour darkening)
    /// The shader samples it by TRUE worldPos.xz, so every ocean mesh agrees at any
    /// shared point — seam-free wave height AND depth colour. This replaces the
    /// per-volume _depth[4] bilinear, which tore at zone borders (that tearing is
    /// why waves were pinned to full strength and depth colour to "always deep").
    /// Sampling is spread over frames so re-baking while sailing doesn't hitch.
    /// </summary>
    public class FiresShoreField : MonoBehaviour
    {
        public static bool Enabled = true;
        public static float WaveShoreFloor = 0f;    // wave strength at the shoreline (vanilla = 0: waves vanish at the edge)
        public static float WaveFullDepth = 10f;   // water depth (m) at which waves reach full strength (vanilla WaterVolume = 10)
        public static float DepthForDark = 8f;     // water depth (m) that reads as fully deep/dark
        public static float DepthDarkness = 1f;     // deep-water brightness: 1 = unchanged, <1 darker, >1 lighter
        public static float FogDomainBlend = 96f;    // coastline crossfade width (m) for the fog domain confine (B channel)
        public static int Resolution = 96;     // texels per side
        public static float Extent = 1024f;  // half-size of the baked rect (m) around the player
        public static int RowsPerFrame = 8;      // sampling budget per frame — spread to avoid hitches

        private Texture2D m_tex;
        private Color[] m_pixels;
        private float[] m_depthM;
        private bool[] m_isWater;
        private float[] m_distM;
        private int m_size;

        // Parallel "tide/river surface-shaping" field, baked over the SAME rect: R = tide
        // influence (0..1), G = river raise in METRES (RGBAHalf so no 8-bit quantization of
        // the raise). Sampled from FiresCore.Services.WaterState.SurfaceFieldSampler, which the
        // tide/river mod fills; null → the field is all (1,0) = unshaped. The FiresWater shader
        // reads it to bend the mesh in lockstep with the buoyancy weld.
        private Texture2D m_fieldTex;
        private Color[] m_fieldPixels;

        private Vector2 m_liveCenter;   // centre of the currently-published field
        private bool m_hasLive;

        private bool m_baking;
        private Vector2 m_bakeCenter;
        private float m_bakeTide, m_liveTide;   // 0.3.153: the tide offset a bake used (fixed at BeginBake), and the live field's
        private const float TideRebakeMetres = 0.05f;
        private int m_bakeRow;

        private static bool s_rebakeRequested;
        // Call when a bake-affecting setting changes so the new ramp/depth shows
        // immediately instead of waiting for the player to move into a re-bake.
        public static void RequestRebake() => s_rebakeRequested = true;

        // Tracks the tide/river field owner's version so a shaping toggle/slider (which bumps
        // WaterState.SurfaceFieldVersion) re-bakes the field live — no relog needed.
        private int m_lastFieldVersion;

        private static readonly int P_Tex = Shader.PropertyToID("_FiresShoreField");
        private static readonly int P_Center = Shader.PropertyToID("_FiresShoreFieldCenter");
        private static readonly int P_Extent = Shader.PropertyToID("_FiresShoreFieldExtent");
        private static readonly int P_On = Shader.PropertyToID("_FiresShoreFieldOn");
        private static readonly int P_Tide = Shader.PropertyToID("_FiresTideOffset");
        private static readonly int P_Darken = Shader.PropertyToID("_FiresDepthDarken");
        private static readonly int P_FieldTex = Shader.PropertyToID("_FiresTideRiverField");
        private static readonly int P_FieldOn = Shader.PropertyToID("_FiresTideRiverFieldOn");

        private void LateUpdate()
        {
            // Always live — depth darkening only varies when the field is on (depth01
            // varies); off → push 1.0 so disabling the field leaves colour unchanged.
            Shader.SetGlobalFloat(P_Darken, Enabled ? DepthDarkness : 1f);

            var player = Player.m_localPlayer;
            if (!Enabled || WorldGenerator.instance == null || player == null)
            {
                Shader.SetGlobalFloat(P_On, 0f);
                s_liveOn = false;
                return;
            }

            Vector3 pp = player.transform.position;
            Vector2 p = new Vector2(pp.x, pp.z);

            // A shaping toggle/slider bumps the field version — re-bake so it applies live.
            int fieldVersion = FiresCore.Services.WaterState.SurfaceFieldVersion;
            if (fieldVersion != m_lastFieldVersion) { m_lastFieldVersion = fieldVersion; s_rebakeRequested = true; }

            // 0.3.153 ([worldgen], FAT 0.2.699: a dedicated server evaluates the same R for the floaters it owns): the tide is
            // baked into each texel's depth, so a field the tide has since moved by TideRebakeMetres is re-baked (FAT uses the
            // live tide; the difference stays under that).
            float tideNow = Shader.GetGlobalFloat(P_Tide);
            if (m_hasLive && Mathf.Abs(tideNow - m_liveTide) > TideRebakeMetres) s_rebakeRequested = true;

            float reTrig = Extent * 0.35f;
            if (!m_baking && (s_rebakeRequested || !m_hasLive || (p - m_liveCenter).sqrMagnitude > reTrig * reTrig))
            {
                s_rebakeRequested = false;
                // 0.3.153: the centre snaps to the texel grid, so texel centres sit on one fixed world grid ((k + 0.5) * texel)
                // whatever the player's position, and a peer without this field can rebuild the same texels and bilinear.
                float texelM = 2f * Extent / Mathf.Clamp(Resolution, 16, 256);
                BeginBake(new Vector2(Mathf.Round(p.x / texelM) * texelM, Mathf.Round(p.y / texelM) * texelM));
            }

            if (m_baking)
                StepBake();

            if (m_hasLive)
            {
                Shader.SetGlobalTexture(P_Tex, m_tex);
                Shader.SetGlobalVector(P_Center, new Vector4(m_liveCenter.x, 0f, m_liveCenter.y, 0f));
                Shader.SetGlobalFloat(P_Extent, Extent);
                Shader.SetGlobalFloat(P_On, 1f);
                s_liveOn = true;
                // Tide/river shaping field rides the SAME rect (center/extent) as the shore field.
                Shader.SetGlobalTexture(P_FieldTex, m_fieldTex);
                Shader.SetGlobalFloat(P_FieldOn, FiresCore.Services.WaterState.SurfaceFieldSampler != null ? 1f : 0f);
            }
            else
            {
                Shader.SetGlobalFloat(P_On, 0f);
                Shader.SetGlobalFloat(P_FieldOn, 0f);
                s_liveOn = false;
            }
        }

        private void EnsureBuffers()
        {
            int n = Mathf.Clamp(Resolution, 16, 256);
            if (m_tex != null && m_size == n) return;
            if (m_tex != null) Destroy(m_tex);
            m_size = n;
            m_tex = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            m_pixels = new Color[n * n];
            m_depthM = new float[n * n];
            m_isWater = new bool[n * n];
            m_distM = new float[n * n];

            if (m_fieldTex != null) Destroy(m_fieldTex);
            m_fieldTex = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            m_fieldPixels = new Color[n * n];
        }

        private void BeginBake(Vector2 center)
        {
            EnsureBuffers();
            m_baking = true;
            m_bakeCenter = center;
            m_bakeRow = 0;
            m_bakeTide = Shader.GetGlobalFloat(P_Tide);
        }

        private void StepBake()
        {
            int n = m_size;
            float waterY = ZoneSystem.c_WaterLevel + m_bakeTide;   // 0.3.153: one tide value for the whole bake
            float texelM = (2f * Extent) / n;
            float origin = -Extent + texelM * 0.5f;
            var wg = WorldGenerator.instance;

            var fieldSampler = FiresCore.Services.WaterState.SurfaceFieldSampler;
            int endRow = Mathf.Min(n, m_bakeRow + Mathf.Max(1, RowsPerFrame));
            for (int z = m_bakeRow; z < endRow; z++)
            {
                float wz = m_bakeCenter.y + origin + z * texelM;
                int rowBase = z * n;
                for (int x = 0; x < n; x++)
                {
                    float wx = m_bakeCenter.x + origin + x * texelM;
                    float gh = wg.GetHeight(wx, wz);
                    float depth = Mathf.Max(0f, waterY - gh);
                    int i = rowBase + x;
                    m_depthM[i] = depth;
                    m_isWater[i] = depth > 0.05f;

                    // Tide/river shaping field (R = tide influence, G = raise metres). No sampler
                    // (vanilla install) → unshaped (1,0). Cheap when the mod's features are off —
                    // the sampler short-circuits to (1,0) without the biome/ring work.
                    if (fieldSampler != null)
                    {
                        Vector2 f = fieldSampler(wx, wz);
                        m_fieldPixels[i] = new Color(f.x, f.y, 0f, 1f);
                    }
                    else m_fieldPixels[i] = new Color(1f, 0f, 0f, 1f);
                }
            }
            m_bakeRow = endRow;
            if (m_bakeRow < n) return;

            CompleteBake();
        }

        private void CompleteBake()
        {
            int n = m_size;
            ComputeDistanceToShore(n, (2f * Extent) / n);

            float invFullDepth = 1f / Mathf.Max(0.5f, WaveFullDepth);
            float invDark = 1f / Mathf.Max(0.1f, DepthForDark);
            float invBlend = 1f / Mathf.Max(1f, FogDomainBlend);
            float floorVal = Mathf.Clamp01(WaveShoreFloor);
            for (int i = 0; i < n * n; i++)
            {
                // Wave attenuation WELDED to vanilla WaterVolume: strength ramps with WATER
                // DEPTH (Clamp01(depth/10)), NOT distance-from-shore. Vanilla buoyancy/swim
                // damps waves by depth this exact way, so matching it makes the rendered wave
                // height equal the surface the body actually floats on — the camera stops
                // toggling above/below water where the two disagreed near shore. Floor 0 +
                // full-depth 10 = exact vanilla; the knobs let you deviate on purpose.
                float wave = m_isWater[i]
                    ? Mathf.Lerp(floorVal, 1f, Mathf.Clamp01(m_depthM[i] * invFullDepth))
                    : 0f;
                float depth01 = Mathf.Clamp01(m_depthM[i] * invDark);
                // B = water-ness for the fog domain confine: 0 on land, ramping to 1
                // over FogDomainBlend metres offshore so each fog stays in its domain
                // with a soft coastline crossfade.
                float waterness = m_isWater[i] ? Mathf.Clamp01(m_distM[i] * invBlend) : 0f;
                m_pixels[i] = new Color(wave, depth01, waterness, 1f);
            }
            m_tex.SetPixels(m_pixels);
            m_tex.Apply(false, false);
            SnapshotWave();   // the CPU twin swaps with the texture and centre published below
            m_fieldTex.SetPixels(m_fieldPixels);
            m_fieldTex.Apply(false, false);

            m_liveCenter = m_bakeCenter;
            m_liveTide = m_bakeTide;
            m_hasLive = true;
            m_baking = false;
        }

        // Two-pass chamfer distance transform: metres from each water texel to the
        // nearest land texel (land = 0). Cheap O(n²), once per completed bake.
        private void ComputeDistanceToShore(int n, float texelM)
        {
            const float BIG = 1e9f;
            for (int i = 0; i < n * n; i++) m_distM[i] = m_isWater[i] ? BIG : 0f;
            float d1 = texelM, d2 = texelM * 1.4142136f;

            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int i = z * n + x;
                    float v = m_distM[i];
                    if (x > 0) v = Mathf.Min(v, m_distM[i - 1] + d1);
                    if (z > 0) v = Mathf.Min(v, m_distM[i - n] + d1);
                    if (x > 0 && z > 0) v = Mathf.Min(v, m_distM[i - n - 1] + d2);
                    if (x < n - 1 && z > 0) v = Mathf.Min(v, m_distM[i - n + 1] + d2);
                    m_distM[i] = v;
                }
            for (int z = n - 1; z >= 0; z--)
                for (int x = n - 1; x >= 0; x--)
                {
                    int i = z * n + x;
                    float v = m_distM[i];
                    if (x < n - 1) v = Mathf.Min(v, m_distM[i + 1] + d1);
                    if (z < n - 1) v = Mathf.Min(v, m_distM[i + n] + d1);
                    if (x < n - 1 && z < n - 1) v = Mathf.Min(v, m_distM[i + n + 1] + d2);
                    if (x > 0 && z < n - 1) v = Mathf.Min(v, m_distM[i + n - 1] + d2);
                    m_distM[i] = v;
                }
        }

        // ── CPU twin of the shader's SampleShoreField().r (0.3.152; Fire 10-01: "our swim trigger/float trigger doesnt actually
        // match the waves") ──────────────────────────────────────────────────────────────────────────────────────────────
        // Physics took its wave strength from vanilla WaterVolume.Depth (four corner depths per 64 m volume) while the rendered
        // sheet takes it from this field, so near shores and cliffs the swim/float wave and the visible wave differed. FAT's
        // WaterVolume.Depth postfix reads this (soft lookup, no hard dependency) so both use the same number.
        // Exactly the GPU's math on the texture that is BOUND: the R channel as the RGBA32 texture stores it (8-bit), the same
        // rect (live centre, the current Extent global), Unity's bilinear filter with Clamp over texel centres, and the same
        // 5 % fade to 1.0 at the rect's edge. The snapshot is taken when a bake completes - the moment the new texture and
        // centre are published - never from a bake in progress.
        // false: the field is off / not baked yet / the point is outside the rect (the GPU then draws full waves, 1.0;
        // the caller decides what physics does there).
        private static float[] s_liveWave;
        private static int s_liveN;
        private static Vector2 s_liveCenter;
        private static bool s_liveOn;
        private const float EdgeFade = 0.05f;

        public static bool TrySampleWave(float x, float z, out float wave)
        {
            wave = 1f;
            float[] r = s_liveWave;
            int n = s_liveN;
            if (!s_liveOn || r == null || n < 2) return false;
            float size = 2f * Mathf.Max(Extent, 1f);
            float u = (x - s_liveCenter.x) / size + 0.5f, v = (z - s_liveCenter.y) / size + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            float inside = Mathf.Clamp01((0.5f - Mathf.Abs(u - 0.5f)) / EdgeFade) * Mathf.Clamp01((0.5f - Mathf.Abs(v - 0.5f)) / EdgeFade);
            float fx = u * n - 0.5f, fz = v * n - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            int xa = Mathf.Clamp(x0, 0, n - 1), xb = Mathf.Clamp(x0 + 1, 0, n - 1);
            int za = Mathf.Clamp(z0, 0, n - 1), zb = Mathf.Clamp(z0 + 1, 0, n - 1);
            float s = Mathf.Lerp(Mathf.Lerp(r[za * n + xa], r[za * n + xb], tx), Mathf.Lerp(r[zb * n + xa], r[zb * n + xb], tx), tz);
            wave = Mathf.Lerp(1f, s, inside);
            return true;
        }

        private void SnapshotWave()
        {
            int n = m_size;
            var r = new float[n * n];
            for (int i = 0; i < r.Length; i++) r[i] = Mathf.Round(Mathf.Clamp01(m_pixels[i].r) * 255f) / 255f;   // as RGBA32 stores it
            s_liveWave = r;
            s_liveN = n;
            s_liveCenter = m_bakeCenter;
        }

        private void OnDestroy()
        {
            if (m_tex != null) Destroy(m_tex);
            Shader.SetGlobalFloat(P_On, 0f);
            s_liveOn = false;
        }
    }
}
