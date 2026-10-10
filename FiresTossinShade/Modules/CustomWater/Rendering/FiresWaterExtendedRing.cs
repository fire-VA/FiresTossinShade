using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// The ONE ocean-coverage water surface. A single FiresWater grid mesh,
    /// centered on the player, extending to <see cref="RadiusMeters"/> — the
    /// replacement for vanilla's camera-following <c>water_lod</c> plane (which
    /// this class hides). Not registered with ZoneSystem, so it adds waves to
    /// the horizon without waking zone content (mobs / vegetation / builds).
    ///
    /// WHY ONE MESH (and not N tiles, and not the vanilla LOD plane)
    /// The FiresWater shader evaluates wave height, surface normal, and foam
    /// entirely from WORLD position (CalcWave(worldPos), verbatim vanilla
    /// WaterVolume.CalcWave). The geometry container is irrelevant to the look:
    /// N tiles, one mesh, or vanilla's per-zone meshes all compute identical
    /// waves at any shared world point. So one mesh gives the exact vanilla
    /// look with zero seams AND cannot fight a second water surface for pixels.
    ///
    /// HOW IT FOLLOWS THE PLAYER WITHOUT POPPING
    /// Verts sit on a fixed 64m local lattice. Each move we SNAP the GameObject
    /// to the 64m world grid nearest the player. Because vert spacing == snap
    /// step == 64m, every vertex always lands on the same GLOBAL 64m lattice —
    /// the window just slides by whole cells. The world-locked wave/foam field
    /// is therefore continuous across every recenter: no rebuild, no GameObject
    /// churn, no wave-phase jump. A zone-border crossing is a single
    /// transform.position write.
    ///
    /// PHYSICS / DRYNESS
    /// Render-only (no Collider, no WaterVolume, no ZNetView). Boats float via
    /// the <see cref="Floating_GetWaterLevel_ExtendedRingFallback"/> postfix
    /// where no real WaterVolume exists; the player's own boat is always inside
    /// a loaded WaterVolume, so it rides true wave physics. Boat interiors stay
    /// dry because this uses the same FiresWater shader, whose stencil tests the
    /// boat's WaterMask bit exactly like zone water does.
    ///
    /// ALTITUDE
    /// Locked at sea level (Y = c_WaterLevel). Only X/Z translate, so at moon
    /// altitude (va_goto_moon, y=5000+) the sheet stays at y=30 far below — no
    /// "water at altitude" artifact.
    /// </summary>
    public static class FiresWaterExtendedRing
    {
        public static bool Enabled = true;
        // Radius of the water disc around the player. Default 3000m to match the
        // FiresAtmosphere fog bubble — water past it is fogged out, so there's
        // no point rendering further. Raise only if the fog radius is raised.
        public static float RadiusMeters = 3000f;
        // Legacy: read by the retired FiresWaterFlatWorldPlane to size its hole.
        // Inert here (the flat plane is no longer mounted). Kept so that file
        // still compiles.
        public static float HoleOverlapMeters = 256f;
        // When true, anchor the disc at world origin (0,0) instead of following
        // the player. Static-disc debug mode; leave false for normal play.
        public static bool WorldCoverageMode = false;

        // ── Live ring tuning (FiresWater MATERIAL props set from C# — no bundle
        // rebuild). Exposed as config sliders so the far water can be dialled in
        // against the perf overlay.
        // _VisibleMaxDistance: range (m) over which full wave detail holds before
        // the ring coarsens — raise to push wavy water further out.
        public static float WaveDetailDistance = 1000f;
        // _IsLodMinTess: the FAR-water tessellation FLOOR. Higher = wavier distant
        // water but more tris across the whole 3km ring — raise cautiously.
        public static float WaveDetailFloor = 12f;
        // Cells the ring OVERLAPS the real zone water to close the seam. The ring
        // draws AFTER zone water (higher queue) and shares its stencil, so zone
        // water wins the overlapped pixels (no double-blend) and the ring just
        // fills any gap — killing the exposed-seafloor seam line. 0 = abut exactly
        // (old behaviour, gap-prone).
        public static int SeamOverlapCells = 1;

        // Vert spacing (m). MUST equal the snap step so recentering preserves
        // the global wave lattice — see class summary. Also matches the vanilla
        // 64m water-zone grid so this mesh welds seamlessly to zone water.
        private const int TileSize = 64;
        // Vertical half-size of the mesh bounds. Covers max wave displacement
        // (~25m crest) plus margin so the GPU tessellator's displaced verts are
        // never frustum-culled mid-frame.
        private const float WaveBoundsY = 60f;
        // Render queue for the disk. Zone water uses FiresWater's default
        // (Transparent-99 = 2901). The disk overlaps zone water around the
        // player, both wear the same shader + the same bit-4 "first draw claims
        // the pixel" stencil, so at EQUAL queue the transparent sort — decided
        // by CAMERA distance — picks the overlap winner per-frame. That made the
        // disk and zone water fight as the camera moved (flicker + the "flatter"
        // rectangular patches where the disk won). A higher queue draws the disk
        // AFTER zone water, so zone water deterministically claims the overlap
        // and the disk is culled there — it only fills the gaps beyond the
        // loaded zones. No camera dependence, no flicker.
        private const int DiskRenderQueue = 2950;

        private static Material s_material;
        private static Mesh s_mesh;
        private static GameObject s_go;
        private static MeshFilter s_filter;
        private static MeshRenderer s_renderer;
        private static float s_builtRadius = -1f;
        private static int s_builtActiveArea = -1;   // rebuild the hole if the loaded-zone reach changes
        private static Vector2Int s_centerCell = new Vector2Int(int.MinValue, int.MinValue);

        // Vanilla water_lod hiding — its own concern, NOT tied to the disk. The
        // good water now is the real zone water + FiresWaterZoneTiles; vanilla's
        // flat LOD plane just paints over them. We re-scan for ALL water_lod
        // renderers (the named plane + any by material) and keep them disabled.
        public static bool HideLodWater = true;
        private static GameObject s_waterPlane;   // cached vanilla LOD plane (_Environment/WaterPlane)
        private static float s_nextLodScan;

        public static void Tick(Vector3 playerPos)
        {
            // ALWAYS first — independent of the disk being enabled, so the toggle
            // hides the vanilla LOD even when the far-horizon disk is off.
            UpdateLodWaterVisibility();

            if (!Enabled)
            {
                Despawn();
                return;
            }

            if (!EnsureMaterial()) return;  // captured FiresWater material not ready yet
            EnsureMesh();

            // The disk is a PARASITE on zone water: it only fills the gap beyond
            // the loaded zones because zone water draws FIRST and claims the
            // overlap pixels via the bit-4 stencil, culling the disk there. The
            // instant zone water isn't actually on screen — the Water layer is
            // culled for the space/planet view, or no ocean WaterVolume is loaded
            // — nothing claims those pixels and the disk floods the whole view,
            // INCLUDING under the player. So it may ONLY draw while zone water is
            // rendering; otherwise it removes itself. Now the two can never fight.
            bool zoneWaterDrawing = IsZoneWaterRendering();
            if (s_renderer != null && s_renderer.enabled != zoneWaterDrawing)
                s_renderer.enabled = zoneWaterDrawing;
            if (!zoneWaterDrawing) return;

            // Snap to the player's vanilla ZONE (exact GetZone math) so the disk
            // centre == the loaded-zone centre and the baked hole lines up 1:1 with
            // the real water zones. RoundToInt would disagree by a cell right on a
            // zone boundary and nudge the hole off the loaded set.
            int cx, cz;
            if (WorldCoverageMode) { cx = 0; cz = 0; }
            else { var z = ZoneSystem.GetZone(playerPos); cx = z.x; cz = z.y; }
            var cell = new Vector2Int(cx, cz);
            if (cell == s_centerCell) return;  // same zone — nothing to do

            s_centerCell = cell;
            s_go.transform.position =
                new Vector3(cx * TileSize, ZoneSystem.c_WaterLevel, cz * TileSize);
        }

        public static void Despawn()
        {
            // NOTE: do NOT restore the vanilla LOD here — its visibility is owned
            // by UpdateLodWaterVisibility/HideLodWater, independent of the disk.
            if (s_go != null) { Object.Destroy(s_go); s_go = null; s_filter = null; s_renderer = null; }
            if (s_mesh != null) { Object.Destroy(s_mesh); s_mesh = null; }
            s_builtRadius = -1f;
            s_builtActiveArea = -1;
            s_centerCell = new Vector2Int(int.MinValue, int.MinValue);
        }

        private static bool EnsureMaterial()
        {
            if (s_material != null) return true;

            // Clone the ACTUAL zone-water material — the shared ocean surface
            // material FiresWaterShaderLoader swapped to Custom/FiresWater IN
            // PLACE — not WaterMaterialManager's separately-wrapped copy. The
            // wrapped copy is rebuilt property-by-property via
            // CopyMatchingProperties (only shader-declared props, and the
            // _depth array etc. are special-cased), so it diverged subtly from
            // the in-place swap — THAT divergence was the visible disk->zone
            // seam. Unity's Material copy constructor duplicates every property
            // exactly, so a clone of the real zone material is pixel-identical.
            // We clone (not share) only so the queue override below can't touch
            // the zone material; identical values mean no seam regardless.
            var zoneMat = FindZoneWaterMaterial();
            if (zoneMat == null) return false;  // no swapped water volume yet — retry next tick

            s_material = new Material(zoneMat) { name = "FiresWaterRing_runtime" };
            s_material.enableInstancing = false;            // single mesh — nothing to instance
            s_material.renderQueue = DiskRenderQueue;       // draw after zone water — see DiskRenderQueue

            // Match the zone water's wave DETAIL despite the disk's coarse 64m
            // base mesh. The shader evaluates CalcWave at EVERY tessellated sub-
            // vertex, so detail = 64m / _Tess. Zone water is a ~2m base grid (so
            // ~0.5m sampling); the disk cloned its low _Tess onto a 64m cell =
            // ~16m sampling, which misses every wave under ~32m and reads dead
            // flat — that was the seam. Crank _Tess to the hardware max (64) ->
            // ~1m sampling, capturing every wave the zone water shows, and turn
            // ON distance-adaptive tessellation (_IsLod) so only NEAR patches pay
            // that cost while the far disk coarsens (uniform 64-tess over 3km
            // would be tens of millions of sub-tris). Zone water stays uniform
            // (_IsLod forced 0 at swap). _IsLodMinTess keeps far patches >1 so
            // distant waves don't clip. All four are plain material props —
            // expose as sliders later if the falloff needs tuning per GPU.
            ApplyMaterialTuning();
            return true;
        }

        // (Re)push the ring's wave-detail material props. Live-callable so config
        // sliders retune the far water without rebuilding the mesh or material.
        public static void ApplyMaterialTuning()
        {
            if (s_material == null) return;
            if (s_material.HasProperty("_Tess")) s_material.SetFloat("_Tess", 64f);
            if (s_material.HasProperty("_IsLod")) s_material.SetFloat("_IsLod", 1f);
            if (s_material.HasProperty("_VisibleMaxDistance")) s_material.SetFloat("_VisibleMaxDistance", Mathf.Max(64f, WaveDetailDistance));
            if (s_material.HasProperty("_IsLodMinTess")) s_material.SetFloat("_IsLodMinTess", Mathf.Clamp(WaveDetailFloor, 1f, 64f));
        }

        // Live retune entry for config sliders: re-push the wave-detail props and
        // force a mesh rebuild so a changed seam overlap takes effect next Tick.
        public static void Retune()
        {
            ApplyMaterialTuning();
            s_builtActiveArea = -1;
        }

        // True ONLY when zone water is actually painting pixels this frame — the
        // precondition for the disk to draw safely. Finds the loaded ocean
        // WaterVolume wearing our shader (the surface that claims the overlap
        // stencil) and confirms its layer is still in the main camera's mask.
        // Returns false when:
        //   * no ocean WaterVolume is loaded (nothing to defer to), or
        //   * that surface's layer is culled — exactly what FiresPlanet's
        //     space/planet view does to the Water layer.
        // In both cases the disk must hide instead of flooding the whole view.
        private static bool IsZoneWaterRendering()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return false;

            var instances = WaterVolume.Instances;
            if (instances == null) return false;
            for (int i = 0; i < instances.Count; i++)
            {
                var wv = instances[i];
                if (wv == null || wv.m_waterSurface == null) continue;
                var mat = wv.m_waterSurface.sharedMaterial;
                if (mat == null || mat.shader == null
                    || mat.shader.name != FiresWaterShaderLoader.ShaderName) continue;

                int layer = wv.m_waterSurface.gameObject.layer;
                return (cam.cullingMask & (1 << layer)) != 0;
            }
            return false;
        }

        // The shared ocean surface material AFTER FiresWaterShaderLoader swapped
        // it to our shader. All vanilla ocean WaterVolumes share one surface
        // material; adopt the first whose shader is ours (so we never clone a
        // still-vanilla material that would render without the underwater pass).
        private static Material FindZoneWaterMaterial()
        {
            var instances = WaterVolume.Instances;
            if (instances == null) return null;
            for (int i = 0; i < instances.Count; i++)
            {
                var wv = instances[i];
                if (wv == null || wv.m_waterSurface == null) continue;
                var mat = wv.m_waterSurface.sharedMaterial;
                if (mat != null && mat.shader != null
                    && mat.shader.name == FiresWaterShaderLoader.ShaderName)
                    return mat;
            }
            return null;
        }

        // (Re)build the grid mesh + host GameObject. Only rebuilds when the
        // radius config changes; steady state is a no-op early-out.
        private static void EnsureMesh()
        {
            // The disk fills only the FAR water; the near area is REAL zone water out
            // to ZoneSystem's zone-loading range — which OceanZoneRingExtender bumps over deep
            // ocean. Cut the hole over exactly the loaded zones so the disk abuts the
            // real water and the hole auto-grows/shrinks as the extender bumps/reverts.
            int holeZones = FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear;
            if (s_go != null && Mathf.Approximately(s_builtRadius, RadiusMeters)
                             && s_builtActiveArea == holeZones) return;

            // Overlap the real water by SeamOverlapCells so there's no exposed-
            // seafloor gap at the boundary. The ring shares zone water's stencil and
            // draws after it, so zone water wins the overlapped pixels (no double-
            // blend) and the ring only fills where real water is missing.
            int overlap = Mathf.Clamp(SeamOverlapCells, 0, holeZones);
            float holeHalf = Mathf.Max(0, holeZones - overlap) * TileSize;
            var newMesh = BuildGridMesh(RadiusMeters, holeHalf);
            s_builtRadius = RadiusMeters;
            s_builtActiveArea = holeZones;

            if (s_go == null)
            {
                s_go = new GameObject("FiresWaterRing");
                Object.DontDestroyOnLoad(s_go);
                s_go.transform.SetPositionAndRotation(
                    new Vector3(0f, ZoneSystem.c_WaterLevel, 0f), Quaternion.identity);
                s_go.transform.localScale = Vector3.one;

                s_filter = s_go.AddComponent<MeshFilter>();
                s_renderer = s_go.AddComponent<MeshRenderer>();
                s_renderer.sharedMaterial = s_material;
                s_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                s_renderer.receiveShadows = false;
                s_renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                s_renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                s_renderer.allowOcclusionWhenDynamic = false;
            }

            if (s_mesh != null) Object.Destroy(s_mesh);
            s_mesh = newMesh;
            s_filter.sharedMesh = s_mesh;
            s_centerCell = new Vector2Int(int.MinValue, int.MinValue);  // force reposition next Tick

            FiresLogger.LogVerbose($"[FiresWaterRing] Built RING mesh: radius={RadiusMeters:F0}m, "
                    + $"hole={(s_builtActiveArea * TileSize):F0}m ({s_builtActiveArea} zones: active+tiles), "
                    + $"{s_mesh.vertexCount} verts, {s_mesh.triangles.Length / 3} tris.");
        }

        // 64m grid aligned to the vanilla zone lattice, with a square HOLE cut over
        // the loaded zones. Quads are emitted only where they're (a) inside the
        // radius — round far edge, matching the fog dome — AND (b) outside the
        // hole. The hole means the disk NEVER overlaps real zone water: it abuts
        // it on the exact zone grid and fills only the empty zones beyond. That
        // removes the whole disk-vs-zone-water pixel fight at the source — no
        // stencil cull to depend on, no double-blend, no gap.
        private static Mesh BuildGridMesh(float radiusMeters, float holeHalfMeters)
        {
            int rc = Mathf.Max(1, Mathf.CeilToInt(radiusMeters / TileSize));  // radius in cells
            int side = 2 * rc + 2;                                              // verts per axis (+1 for the half-cell shift)
            int vcount = side * side;

            var verts = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            var normals = new Vector3[vcount];
            var colors = new Color[vcount];
            for (int zi = 0; zi < side; zi++)
            {
                for (int xi = 0; xi < side; xi++)
                {
                    int idx = zi * side + xi;
                    // Shift verts by HALF a cell so every QUAD centers on a 64m zone
                    // center (vanilla GetZone floors to multiples of 64). The disk's
                    // cells then coincide 1:1 with vanilla water zones.
                    float lx = (xi - rc) * TileSize - TileSize * 0.5f;
                    float lz = (zi - rc) * TileSize - TileSize * 0.5f;
                    verts[idx] = new Vector3(lx, 0f, lz);
                    uvs[idx] = new Vector2(lx / TileSize, lz / TileSize);  // unused by shader (world-XZ foam)
                    normals[idx] = Vector3.up;
                    colors[idx] = Color.white;
                }
            }

            float rSqr = radiusMeters * radiusMeters;
            float holeEps = holeHalfMeters + 1f;                                // float-safe inclusive hole
            var tris = new System.Collections.Generic.List<int>(rc * rc * 6);
            for (int zi = 0; zi < side - 1; zi++)
            {
                for (int xi = 0; xi < side - 1; xi++)
                {
                    // Quad center sits on a zone center (multiple of 64) after the shift.
                    float ccx = (xi - rc) * TileSize;
                    float ccz = (zi - rc) * TileSize;
                    if (ccx * ccx + ccz * ccz > rSqr) continue;                            // outside the disc
                    if (Mathf.Abs(ccx) <= holeEps && Mathf.Abs(ccz) <= holeEps) continue;  // loaded zone → real water owns it

                    int i0 = zi * side + xi;
                    int i1 = zi * side + xi + 1;
                    int i2 = (zi + 1) * side + xi;
                    int i3 = (zi + 1) * side + xi + 1;
                    // Up-facing winding (matches the proven FiresWater tile mesh).
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    tris.Add(i1); tris.Add(i2); tris.Add(i3);
                }
            }

            var mesh = new Mesh { name = "FiresWaterRing_Grid" };
            mesh.indexFormat = vcount > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(tris, 0);
            float half = rc * TileSize;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(half * 2f, WaveBoundsY, half * 2f));
            mesh.UploadMeshData(false);  // keep readable — never mutated, but cheap + lets us log counts
            return mesh;
        }

        // ---- Vanilla LOD plane suppression -----------------------------------
        // The vanilla far-ocean LOD is the flat lighter-blue plane that doesn't
        // stitch to our water. Disabling its RENDERER didn't stick — vanilla's
        // own water-plane script re-enables it every frame, so it kept painting
        // under/through our tiles. Instead we deactivate the WHOLE WaterPlane
        // GameObject: an inactive object's script can't run, so it can't undo us.
        // Our disk + zone water + tiles are the only ocean now.

        private static void UpdateLodWaterVisibility()
        {
            // GameObject.Find only sees ACTIVE objects, so once we deactivate the
            // plane we MUST keep the cached reference. Re-acquire (throttled) only
            // while we don't have one — i.e. at boot or after a scene reload.
            if (s_waterPlane == null && Time.time >= s_nextLodScan)
            {
                s_nextLodScan = Time.time + 1f;
                s_waterPlane = GameObject.Find("_GameMain/_Environment/WaterPlane");
            }
            if (s_waterPlane == null) return;

            bool wantActive = !HideLodWater;
            if (s_waterPlane.activeSelf != wantActive)
                s_waterPlane.SetActive(wantActive);
        }
    }

    /// <summary>
    /// Boat-float fallback for the extended ring. Vanilla
    /// <see cref="Floating.GetWaterLevel"/> returns a "no water here" sentinel
    /// when the query position isn't inside any WaterVolume — so a boat sailing
    /// through our render-only ring (which has no WaterVolume) would sink.
    ///
    /// Postfix: when vanilla found no water AND we're enabled AND the query is
    /// near sea level, return the static sea level. The player's own boat is
    /// always inside a loaded WaterVolume (it sits at the center of the active
    /// area), so it keeps true wave physics; this only catches far-away boats
    /// over the ring. The "near sea level" gate stops phantom water under
    /// inland builds.
    /// </summary>
    [HarmonyPatch(typeof(Floating), nameof(Floating.GetWaterLevel))]
    internal static class Floating_GetWaterLevel_ExtendedRingFallback
    {
        private const float NoWaterSentinel = -100f;  // vanilla uses -10000; wide margin
        private const float SeaLevelBandMeters = 50f;

        // The disc's VISUAL surface follows the tide: FiresValheimGalaxies (when
        // installed) publishes the live tide as the _FiresTideOffset shader
        // global, which the FiresWater shader adds to the sheet's y. We read it
        // back here so a far boat over the disc floats on that SAME tide-shifted
        // surface instead of clipping through it. The global is 0 whenever no
        // tide mod is loaded, so FAT standalone is unaffected.
        private static readonly int TideOffsetProp = Shader.PropertyToID("_FiresTideOffset");

        private static void Postfix(Vector3 p, ref float __result)
        {
            if (__result > NoWaterSentinel) return;       // vanilla found real water
            if (!FiresWaterExtendedRing.Enabled) return;

            float sea = ZoneSystem.c_WaterLevel + Shader.GetGlobalFloat(TideOffsetProp);
            if (Mathf.Abs(p.y - sea) > SeaLevelBandMeters) return;

            __result = sea;
        }
    }

    /// <summary>
    /// Per-frame driver. Recenter is a couple of int compares plus (on a cell
    /// crossing) one transform write, so LateUpdate every frame is free and
    /// keeps the sheet perfectly centered even at boat speed / after teleports.
    /// </summary>
    public class FiresWaterExtendedRingDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;  // defer until a player exists
            FiresWaterExtendedRing.Tick(player.transform.position);
        }

        private void OnDisable()
        {
            FiresWaterExtendedRing.Despawn();
        }
    }
}
