using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Spawns and maintains the aurora curtain GameObject — a procedurally-
    /// generated cylinder around the player's camera that renders the
    /// <c>Custom/FiresAurora</c> shader's ribbon effect over the sky.
    /// </summary>
    public static class FiresAurora
    {
        public const string ShaderName = "Custom/FiresAurora";

        public static bool Enabled = false;
        public static float Intensity = 1.0f;
        public static bool NightOnly = true;
        public static bool DuringStorms = false;

        // Hemisphere DOME centered on the camera. Replaces the prior
        // cylinder (which always read as a vertical wall around the
        // player, even when made very tall). A true dome lets curtains
        // render anywhere in the upper hemisphere including directly
        // overhead, matching real northern-lights geometry.
        //
        // UV mapping baked by BuildHemisphereMesh: UV.x = azimuth with
        // 0 at world +Z (north), 0.25 at +X (east), 0.5 at -Z (south),
        // 0.75 at -X (west), 1.0 back to north. UV.y = altitude with 0
        // at the horizon edge, 1 at the zenith. The shader uses UV.x
        // for the north-compass intensity bias and UV.y for the band /
        // gradient altitude masks.
        //
        // Radius chosen large enough that fog / depth never reach the
        // mesh; rings × segments traded for smooth band fades and
        // smooth north-azimuth gradient. 64×16 = 1024 verts is tiny.
        private const float DomeRadius = 1500f;
        private const int DomeSegments = 64;  // azimuth steps
        private const int DomeRings = 16;  // altitude steps (0..MaxAltitudeDeg)

        // Truncate the dome before the zenith so all 64 azimuth segments
        // never collapse to a single point. At 80° altitude the top ring
        // still has 64 distinct verts in a small circle (~261m radius at
        // dome radius 1500), which kills polar UV pinching and the green
        // crackle artifact the user reported. The shader's zenith-fade
        // covers the remaining 10° hole so curtains smoothly ramp to
        // zero before the truncation edge — no visible hard cut.
        private const float DomeMaxAltitudeDeg = 80f;

        private const float FadeRate = 1.5f;  // intensity units per second

        public static Shader AuroraShader { get; private set; }
        public static Material AuroraMaterial { get; private set; }

        private static GameObject s_curtainGO;
        private static MeshRenderer s_renderer;
        private static MeshFilter s_filter;
        private static float s_currentIntensity;

        private static readonly int Prop_Intensity = Shader.PropertyToID("_Intensity");

        public static bool TryLoadShader(AssetBundle bundle)
        {
            if (AuroraShader != null) return true;
            if (bundle == null) return false;

            AuroraShader = bundle.LoadAsset<Shader>(ShaderName);
            if (AuroraShader == null)
            {
                foreach (var s in bundle.LoadAllAssets<Shader>())
                {
                    if (s != null && s.name == ShaderName) { AuroraShader = s; break; }
                }
            }
            if (AuroraShader == null) return false;

            AuroraMaterial = new Material(AuroraShader) { name = "FiresAurora_runtime" };
            FiresLogger.LogVerbose($"[FiresAurora] Loaded '{ShaderName}' from bundle.");
            return true;
        }

        public static void Tick(Transform anchor)
        {
            if (anchor == null) return;
            if (AuroraShader == null || AuroraMaterial == null) return;

            bool shouldShow = Enabled
                && Intensity > 0.001f
                && (!NightOnly || IsNight())
                && (DuringStorms || !IsStormy());

            EnsureCurtain(anchor);

            // Cross-fade so toggles + night/day transitions don't pop.
            float target = shouldShow ? Intensity : 0f;
            s_currentIntensity = Mathf.MoveTowards(
                s_currentIntensity, target, FadeRate * Time.unscaledDeltaTime);

            AuroraMaterial.SetFloat(Prop_Intensity, s_currentIntensity);

            if (s_curtainGO != null)
            {
                bool active = s_currentIntensity > 0.001f;
                if (s_curtainGO.activeSelf != active) s_curtainGO.SetActive(active);
                if (active)
                {
                    // Dome sits centered on the camera. The hemisphere
                    // mesh's bottom edge (altitude 0) lands at camera
                    // height, so the curtains visibly meet the real
                    // horizon when standing at ground level. Y-rotation
                    // stays identity so the mesh's +Z = world +Z = the
                    // north compass direction — the north-bias mask in
                    // the shader depends on that.
                    s_curtainGO.transform.position = anchor.position;
                    s_curtainGO.transform.rotation = Quaternion.identity;
                }
            }
        }

        private static void EnsureCurtain(Transform anchor)
        {
            if (s_curtainGO != null) return;

            s_curtainGO = new GameObject("FiresAurora_Curtain");
            UnityEngine.Object.DontDestroyOnLoad(s_curtainGO);

            s_filter = s_curtainGO.AddComponent<MeshFilter>();
            s_filter.sharedMesh = BuildHemisphereMesh();

            s_renderer = s_curtainGO.AddComponent<MeshRenderer>();
            s_renderer.sharedMaterial = AuroraMaterial;
            s_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            s_renderer.receiveShadows = false;
            s_renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            s_renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            s_curtainGO.SetActive(false);

            // One-time confirm so the next "we see nothing" report has a
            // log line to check against. If this message never appears,
            // TryLoadShader never succeeded and the rest of the pipeline
            // is moot. Vert/tri counts are computed inside
            // BuildHemisphereMesh BEFORE UploadMeshData(true) marks the
            // mesh non-readable; pulled from constants here to avoid the
            // "Not allowed to access triangles on mesh" error.
            int vertsPerRow = DomeSegments + 1;
            int vc = vertsPerRow * (DomeRings + 1);
            int tc = DomeSegments * DomeRings * 2;
            FiresLogger.LogVerbose($"[FiresAurora] Dome created. radius={DomeRadius}m maxAlt={DomeMaxAltitudeDeg}° " +
                      $"segments={DomeSegments} rings={DomeRings} verts={vc} tris={tc}. " +
                      $"Anchored to camera every Tick; UV.x=0 → world +Z (north).");
        }

        // Upper-hemisphere dome over the camera. Vertex layout:
        //   azimuth (around)  : 0..2π, mapped to UV.x = 0..1.
        //     seg=0 sits at world +Z so UV.x=0 is NORTH (Valheim/Unity
        //     +Z = compass north). seg=segments/4 = +X = east, half = -Z
        //     south, three-quarters = -X west.
        //   altitude (upward) : 0..π/2, mapped to UV.y = 0..1.
        //     row=0 is the horizon edge (altitude 0), row=rings is the
        //     zenith (altitude π/2 = straight up).
        //
        // Winding picked so triangles face INWARD (the camera is inside
        // the dome looking up). Shader uses Cull Off so either face
        // renders, but inside-facing is the "natural" orientation for
        // the dome — keeps Unity's bound + bbox math right when the
        // camera is inside.
        //
        // Vert count: (segments+1) × (rings+1) = 65 × 17 = 1105. Tris:
        // segments × rings × 2 = 64 × 16 × 2 = 2048. Tiny.
        private static Mesh BuildHemisphereMesh()
        {
            int segments = DomeSegments;
            int rings = DomeRings;
            int vertsPerRow = segments + 1;     // seam vert duplicated for UV.x = 1.0
            int vertCount = vertsPerRow * (rings + 1);
            int triCount = segments * rings * 6;

            var verts = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var tris = new int[triCount];

            float maxAltRad = DomeMaxAltitudeDeg * Mathf.Deg2Rad;
            for (int row = 0; row <= rings; row++)
            {
                float vT = row / (float)rings;
                // Altitude ranges 0 (horizon) to DomeMaxAltitudeDeg, NOT
                // to 90° — the top ~10° is left open so the 64-seg ring
                // never converges to a point. Shader's zenith fade ramps
                // intensity to 0 across the top of UV.y so the visible
                // dome edge blends to nothing rather than showing a hard
                // mesh boundary.
                float altitude = vT * maxAltRad;
                float ringR = Mathf.Cos(altitude) * DomeRadius;
                float ringY = Mathf.Sin(altitude) * DomeRadius;

                for (int seg = 0; seg <= segments; seg++)
                {
                    float uT = seg / (float)segments;
                    float azimuth = uT * (Mathf.PI * 2f);   // 0 = +Z = north
                    float x = Mathf.Sin(azimuth) * ringR;
                    float z = Mathf.Cos(azimuth) * ringR;

                    int idx = row * vertsPerRow + seg;
                    verts[idx] = new Vector3(x, ringY, z);
                    uvs[idx] = new Vector2(uT, vT);
                }
            }

            int t = 0;
            for (int row = 0; row < rings; row++)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    int a = row * vertsPerRow + seg;
                    int b = a + 1;
                    int c = a + vertsPerRow;
                    int d = c + 1;
                    // Inward-facing winding (camera is inside the dome).
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            }

            var mesh = new Mesh { name = "FiresAuroraDome" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static bool IsNight()
        {
            try { return EnvMan.instance != null && EnvMan.IsNight(); }
            catch { return false; }
        }

        private static bool IsStormy()
        {
            try
            {
                var env = EnvMan.instance?.GetCurrentEnvironment();
                if (env == null) return false;
                string n = env.m_name ?? string.Empty;
                return n.Contains("Storm") || n.Contains("Rain") || n.Contains("Thunder");
            }
            catch { return false; }
        }
    }

    public class FiresAuroraDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return;

            // First-tick shader load — bundle is loaded by FiresWaterShaderLoader.
            if (FiresAurora.AuroraShader == null)
            {
                // Make sure the bundle is ready before we try to pull our shader.
                FiresWaterShaderLoader.EnsureLoaded();
                if (!FiresAurora.TryLoadShader(FiresWaterShaderLoader.Bundle))
                    return;  // bundle not ready yet, retry next frame
            }

            try { FiresAurora.Tick(cam.transform); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresAurora] Tick failed: {ex.Message}");
                enabled = false;
            }
        }
    }
}
