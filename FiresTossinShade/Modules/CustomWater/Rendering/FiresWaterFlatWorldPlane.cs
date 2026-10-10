using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// One single static "ring" mesh covering the entire Valheim world at sea
    /// level (y = ZoneSystem.c_WaterLevel = 30) — EXCEPT for a square hole
    /// directly underneath the player where the animated FiresWaterExtendedRing
    /// tiles take over. Uses the FiresWater shader with waves DISABLED
    /// (no global wind, _depth=0) so per-frame cost is one draw call.
    ///
    /// === WHY A HOLE ===
    /// Naive single-quad flat plane at y=30 conflicts with the animated tiles
    /// at the WAVE TROUGHS. When a wave dips below sea level (e.g., y=27),
    /// depth testing picks whichever is closer to the camera. For an overhead
    /// camera, the flat plane at y=30 is CLOSER than the wave trough at y=27,
    /// so the flat plane renders ON TOP of the trough, visibly intruding
    /// through the wave. Cutting a hole in the flat plane around the player
    /// (just inside the animated ring boundary) means there's no flat plane
    /// geometry where the troughs live — no depth conflict.
    ///
    /// === MESH SHAPE ===
    /// Square outer boundary (HalfExtent = 12500 → 25km × 25km), square
    /// inner hole (InnerHalf = ~95% of animated ring radius), 8 verts,
    /// 8 quads (16 tris). When the player crosses a cell boundary, the
    /// 4 inner verts re-position to keep the hole centered on the player.
    /// Outer 4 verts NEVER move.
    ///
    ///     v0────v1──────────v2────v3
    ///     │  q0 │    q1     │  q2 │
    ///     v4────v5──────────v6────v7
    ///     │  q3 │   HOLE    │  q4 │
    ///     v8────v9─────────v10────v11
    ///     │  q5 │    q6     │  q7 │
    ///     v12──v13─────────v14────v15
    ///
    /// 16 verts, 7 quads = 14 tris. Inner verts (v5, v6, v9, v10) follow
    /// the player on the XZ plane; the 4 corners of the hole.
    /// </summary>
    public static class FiresWaterFlatWorldPlane
    {
        // Disabled 2026-06-01 — user preference is the animated tile ring
        // (FiresWaterExtendedRing) at large radius (10240m) only, no static
        // flat plane underneath. Flip back to true if the ring's outer edge
        // becomes visible and you want the static fill again.
        public static bool Enabled = false;
        // Half-extent in meters. 12500 → 25km × 25km plane covers a default
        // Valheim world (10km radius) plus margin.
        public static float HalfExtent = 12500f;
        // The inner hole's half-extent is computed every frame from
        // FiresWaterExtendedRing.RadiusMeters - HoleOverlapMeters, so the
        // two systems stay in sync automatically. Whatever radius the
        // animated ring uses, the hole is sized to sit comfortably inside it.
        public static float InnerHalf => Mathf.Max(
            64f,
            FiresWaterExtendedRing.RadiusMeters - FiresWaterExtendedRing.HoleOverlapMeters);
        // Re-update the mesh when the player has moved more than this many
        // meters since the last update. 32m = half a tile. Cheap to
        // recompute (4 vertex positions + UploadMeshData) so the threshold
        // is low; raise to 64 if you see hitches.
        public static float UpdateThresholdMeters = 32f;

        private static GameObject s_planeGO;
        private static Material s_flatMaterial;
        private static Mesh s_planeMesh;
        private static Vector2 s_lastCenter = new Vector2(float.NaN, float.NaN);
        private static float s_lastInnerHalf = float.NaN;

        public static void Tick(Vector3 playerPos)
        {
            if (!Enabled)
            {
                Despawn();
                return;
            }

            if (!EnsureFlatMaterial()) return;
            EnsurePlane();

            // Center the hole on the player's XZ position. Only update if
            // the player moved enough OR if the InnerHalf changed (e.g.,
            // FiresWaterExtendedRing.RadiusMeters changed via config).
            Vector2 nowCenter = new Vector2(playerPos.x, playerPos.z);
            float nowInner = InnerHalf;
            bool centerChanged = float.IsNaN(s_lastCenter.x) ||
                                    Vector2.Distance(nowCenter, s_lastCenter) >= UpdateThresholdMeters;
            bool innerChanged = float.IsNaN(s_lastInnerHalf) ||
                                    Mathf.Abs(nowInner - s_lastInnerHalf) > 0.5f;
            if (centerChanged || innerChanged)
            {
                UpdateInnerHole(nowCenter, nowInner);
                s_lastCenter = nowCenter;
                s_lastInnerHalf = nowInner;
            }
        }

        public static void Despawn()
        {
            if (s_planeGO != null)
            {
                Object.Destroy(s_planeGO);
                s_planeGO = null;
            }
            s_lastCenter = new Vector2(float.NaN, float.NaN);
        }

        private static bool EnsureFlatMaterial()
        {
            if (s_flatMaterial != null) return true;

            var src = WaterMaterialManager.GetWaterMaterial(LiquidType.Water);
            if (src == null) return false;

            s_flatMaterial = new Material(src) { name = "FiresWaterFlatWorldPlane_runtime" };
            // Wave-driving props zeroed: no displacement, perfectly flat.
            s_flatMaterial.SetFloatArray("_depth", new float[] { 0f, 0f, 0f, 0f });
            s_flatMaterial.SetFloat("_UseGlobalWind", 0f);
            s_flatMaterial.enableInstancing = false;  // single mesh
            return true;
        }

        private static void EnsurePlane()
        {
            if (s_planeGO != null) return;

            // Build the mesh with the hole centered at (0,0) initially.
            // UpdateInnerHole will reposition the inner verts on first tick.
            s_planeMesh = BuildRingMesh(HalfExtent, InnerHalf, Vector2.zero);

            s_planeGO = new GameObject("FiresWaterFlatWorldPlane");
            Object.DontDestroyOnLoad(s_planeGO);
            s_planeGO.transform.position = new Vector3(0f, ZoneSystem.c_WaterLevel, 0f);
            s_planeGO.transform.rotation = Quaternion.identity;

            var mf = s_planeGO.AddComponent<MeshFilter>();
            mf.sharedMesh = s_planeMesh;

            var mr = s_planeGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = s_flatMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.allowOcclusionWhenDynamic = false;

            Debug.Log($"[FiresWaterFlatWorldPlane] Spawned ring mesh: outer={HalfExtent * 2:F0}m, " +
                      $"hole={InnerHalf * 2:F0}m, shader='{s_flatMaterial.shader.name}'.");
        }

        // Re-position the 4 inner-hole vertices to follow the player AND
        // resize them if the ring radius changed. Outer verts never move.
        // Only verts 5, 6, 9, 10 (hole corners) plus 1, 2, 4, 7, 8, 11, 13,
        // 14 (the outer mid-edges that share the hole's X or Z coord) need
        // updating when the hole resizes — all 12 follow the player on XZ.
        private static void UpdateInnerHole(Vector2 center, float innerHalf)
        {
            if (s_planeMesh == null) return;

            float oH = HalfExtent;
            float iH = innerHalf;
            float cx = center.x;
            float cz = center.y;

            var verts = s_planeMesh.vertices;
            // Outer-corner verts (0, 3, 12, 15) stay nailed to ±HalfExtent — skip.
            // Mid-edge verts on each side update X or Z to track the hole edge.
            verts[1] = new Vector3(cx - iH, 0f, -oH);    // S edge, west of hole
            verts[2] = new Vector3(cx + iH, 0f, -oH);    // S edge, east of hole
            verts[4] = new Vector3(-oH, 0f, cz - iH);// W edge, south of hole
            verts[5] = new Vector3(cx - iH, 0f, cz - iH);// hole SW
            verts[6] = new Vector3(cx + iH, 0f, cz - iH);// hole SE
            verts[7] = new Vector3(oH, 0f, cz - iH);// E edge, south of hole
            verts[8] = new Vector3(-oH, 0f, cz + iH);// W edge, north of hole
            verts[9] = new Vector3(cx - iH, 0f, cz + iH);// hole NW
            verts[10] = new Vector3(cx + iH, 0f, cz + iH);// hole NE
            verts[11] = new Vector3(oH, 0f, cz + iH);// E edge, north of hole
            verts[13] = new Vector3(cx - iH, 0f, oH);    // N edge, west of hole
            verts[14] = new Vector3(cx + iH, 0f, oH);    // N edge, east of hole
            s_planeMesh.vertices = verts;
            s_planeMesh.UploadMeshData(false);  // false = keep CPU copy for next update
        }

        // 16 verts laid out in a 4×4 grid, 7 quads forming a square ring
        // around the inner hole. The center quad (verts 5,6,9,10) is OMITTED
        // — that's the hole.
        //
        // Vertex indices in the 4×4 grid:
        //   v0 v1 v2 v3
        //   v4 v5 v6 v7
        //   v8 v9 v10 v11
        //   v12 v13 v14 v15
        //
        // Inner hole corners (v5, v6, v9, v10) move with the player.
        // All other verts are at the outer boundary (±HalfExtent).
        private static Mesh BuildRingMesh(float outerHalf, float innerHalf, Vector2 holeCenter)
        {
            var mesh = new Mesh { name = "FiresWaterFlatWorldPlane_RingMesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

            float oH = outerHalf;
            float iH = innerHalf;
            float cx = holeCenter.x;
            float cz = holeCenter.y;

            mesh.vertices = new[]
            {
                new Vector3(-oH,      0f, -oH),       // 0  outer SW
                new Vector3(cx - iH,  0f, -oH),       // 1  outer S, west edge of hole's column
                new Vector3(cx + iH,  0f, -oH),       // 2  outer S, east edge of hole's column
                new Vector3( oH,      0f, -oH),       // 3  outer SE

                new Vector3(-oH,      0f, cz - iH),   // 4  outer W, south edge of hole's row
                new Vector3(cx - iH,  0f, cz - iH),   // 5  hole SW
                new Vector3(cx + iH,  0f, cz - iH),   // 6  hole SE
                new Vector3( oH,      0f, cz - iH),   // 7  outer E, south edge of hole's row

                new Vector3(-oH,      0f, cz + iH),   // 8  outer W, north edge of hole's row
                new Vector3(cx - iH,  0f, cz + iH),   // 9  hole NW
                new Vector3(cx + iH,  0f, cz + iH),   // 10 hole NE
                new Vector3( oH,      0f, cz + iH),   // 11 outer E, north edge of hole's row

                new Vector3(-oH,      0f,  oH),       // 12 outer NW
                new Vector3(cx - iH,  0f,  oH),       // 13 outer N, west edge of hole's column
                new Vector3(cx + iH,  0f,  oH),       // 14 outer N, east edge of hole's column
                new Vector3( oH,      0f,  oH),       // 15 outer NE
            };

            // UVs — tile the FiresWater shader across the big plane so its
            // depth-bilinear and shore-blending sample reasonable values.
            // Range chosen to match vanilla 64m zone UVs roughly (1 UV unit
            // per 64m), so the shader's wave-period-relative sampling matches.
            float uvScale = 1f / 64f;
            var uvs = new Vector2[16];
            for (int i = 0; i < 16; i++)
            {
                var v = mesh.vertices[i];
                uvs[i] = new Vector2(v.x * uvScale, v.z * uvScale);
            }
            mesh.uv = uvs;

            // 7 quads = 14 triangles (each quad = 2 tris).
            // Skip the center quad (5,6,9,10) — that's the hole.
            mesh.triangles = new[]
            {
                // Row 0 (south strip): 3 quads
                0, 4, 1,    1, 4, 5,
                1, 5, 2,    2, 5, 6,
                2, 6, 3,    3, 6, 7,
                // Row 1 (middle strip — only left + right; center is the hole)
                4, 8, 5,    5, 8, 9,
                                              // ←→ HOLE (no tris)
                6,10, 7,    7,10,11,
                // Row 2 (north strip): 3 quads
                 8,12, 9,    9,12,13,
                 9,13,10,   10,13,14,
                10,14,11,   11,14,15,
            };

            var normals = new Vector3[16];
            for (int i = 0; i < 16; i++) normals[i] = Vector3.up;
            mesh.normals = normals;

            var colors = new Color[16];
            for (int i = 0; i < 16; i++) colors[i] = Color.white;
            mesh.colors = colors;

            mesh.bounds = new Bounds(Vector3.zero,
                new Vector3(outerHalf * 2f, 2f, outerHalf * 2f));
            mesh.UploadMeshData(false);  // false = keep CPU copy so UpdateInnerHole can modify verts
            return mesh;
        }
    }

    /// <summary>
    /// Per-frame driver for FiresWaterFlatWorldPlane. Mirrors the
    /// FiresWaterExtendedRingDriver pattern — once-per-second tick is plenty
    /// since the hole update has a 32m movement threshold internally.
    /// </summary>
    public class FiresWaterFlatWorldPlaneDriver : MonoBehaviour
    {
        private const float CheckIntervalSec = 0.5f;
        private float m_nextCheckTime;

        private void LateUpdate()
        {
            if (Time.unscaledTime < m_nextCheckTime) return;
            m_nextCheckTime = Time.unscaledTime + CheckIntervalSec;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                // No player yet — defer spawn until first player exists so
                // the initial hole position is meaningful.
                return;
            }

            FiresWaterFlatWorldPlane.Tick(player.transform.position);
        }

        private void OnDisable() { FiresWaterFlatWorldPlane.Despawn(); }
    }
}
