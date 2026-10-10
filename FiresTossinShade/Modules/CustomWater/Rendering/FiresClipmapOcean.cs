// Clipmap ring geometry + cascade grid snapping adapted from the Crest Ocean
// System (Scripts/OceanBuilder.cs, Scripts/LodData/LodTransform.cs).
// MIT License (c) 2019 Wave Harmonic and contributors.
// https://github.com/wave-harmonic/crest

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Crest-style clipmap ocean — the config-gated ALTERNATIVE to the
    /// <see cref="FiresWaterExtendedRing"/> far-water disk (default OFF;
    /// "05 - Far Water Disk / 09 UseClipmapOcean"). One center high-density
    /// square (LOD 0) wrapped in concentric square rings, each ring 2x the
    /// world size of the previous at the SAME vertex count, so triangle
    /// density falls off with distance the way screen coverage does. All
    /// rings wear per-LOD clones of the live zone-water FiresWater material,
    /// so waves/foam/colors are pixel-identical to the near water.
    ///
    /// HOW SEAMS BETWEEN LOD RINGS STAY CLOSED (no shader changes)
    /// Crest closes cascade seams in the vertex shader (per-vert morph to the
    /// next LOD's grid). We cannot touch shaders, so we use the MESH-side half
    /// of Crest's answer instead — the skirts its Fat* patch types add:
    ///   1. Every ring shares PatchResolution, and ring L's cell size is
    ///      exactly 2x ring L-1's. Ring L's inner hole edge (at half its
    ///      extent) lands on ring L-1's outer edge, and because every ring-L
    ///      vertex coordinate g*cell_L equals (2g)*cell_L-1, coarse edge verts
    ///      coincide EXACTLY with alternate fine edge verts. Only the fine
    ///      midpoints can crack (classic T-junction).
    ///   2. Each ring's inner edge therefore carries an INWARD skirt — one
    ///      ring-density cell overlapping the finer ring's territory (Crest:
    ///      "rings have an extra side of verts that point inwards"). Cracks
    ///      open at most a displacement-interpolation error wide, and the
    ///      skirt water sits within that same error directly behind them.
    ///   3. FiresWater's bit-4 stencil is "first draw claims the pixel", and
    ///      each ring renders at queue 2950+lod (zone water is 2901): zone
    ///      water first, then rings inner->outer. Overlaps never double-blend
    ///      and the winner is deterministic — the same mechanism the disk
    ///      uses against zone water (SeamOverlapCells), just applied between
    ///      cascades too.
    ///
    /// HOW IT FOLLOWS THE CAMERA WITHOUT VERTEX SWIM
    /// LodTransform's snapping trick (pos - Mathf.Repeat(pos, texel)), applied
    /// once to the shared root at the COARSEST cascade's cell size. Cell sizes
    /// are exact power-of-two multiples of each other, so one coarse-cell snap
    /// moves EVERY ring an integer number of its own cells — each cascade's
    /// verts always land back on the same global lattice and the world-space
    /// wave field (CalcWave(worldPos)) is continuous across every recenter.
    /// Crest snaps each LOD's data independently and lets the shader morph
    /// verts; with CPU-only geometry the single coarsest-step snap is the
    /// equivalent that keeps all cascades texel-stationary at once.
    ///
    /// WHY ONE MESH PER RING (not Crest's 12 patch instances)
    /// Crest splits rings into patches for frustum-culling granularity and to
    /// reuse a handful of shared meshes. We rebuild only on config change and
    /// the whole clipmap is nearly always on screen, so per-ring meshes remove
    /// every same-ring seam by construction (single lattice, single mesh) and
    /// drop the rotation/offset patch bookkeeping — the slim/fat variants
    /// exist to fix same-ring patch overlap, which we simply don't have. The
    /// horizon skirt (FatXOuter/FatXZOuter) IS kept: the outermost ring gets
    /// one extra vert row pushed far past the fog dome so the mesh never
    /// shows a hard square edge.
    ///
    /// Same non-goals as the disk: render-only (no Collider, no WaterVolume,
    /// no ZNetView), locked at sea level (tide rides the _FiresTideOffset
    /// shader global), hides while zone water isn't rendering so it can never
    /// flood the space/planet view, and boats get the same Floating fallback.
    /// </summary>
    public static class FiresClipmapOcean
    {
        public static bool Enabled = false;
        // Half-size (m) of the outermost ring — the clipmap's total coverage.
        // ~4000 matches the disk-era far coverage / fog bubble.
        public static float TotalRadiusMeters = 4000f;
        // Number of cascades. Each adds a ring 2x the previous size, so for a
        // fixed radius +1 LOD halves the finest cell at the player.
        public static int LodCount = 6;
        // Crest's vertDensity: CELLS per patch side, shared by every ring —
        // the shared density is what makes cascade edges line up (see class
        // summary). Defaults: 4000m / 6 LODs / 32 => ~1.95m cells at LOD 0,
        // deliberately matching the real zone-water mesh's ~2m grid.
        public static int PatchResolution = 32;

        // Radius (m) around the camera the clipmap leaves EMPTY. Loaded-zone water (plus
        // the zone ring extender) owns the near field; without this hole the dense inner
        // cascades overlap it — a second full-amplitude surface poking through the shore-
        // attenuated zone waves (the "double shoreline") and millions of wasted tris.
        // Implemented by skipping every cascade whose footprint fits inside the hole: the
        // first kept ring's own inner hole (always the previous LOD's extent) becomes the
        // cutout, exactly like the disk's old centre cutout. 0 = solid centre (debug).
        public static float InnerHoleRadius = 160f;

        // Inward overlap (in ring-density cells) each ring extends past its
        // inner hole edge, hiding T-junction cracks against the finer ring.
        // Alignment between cascades is exact by construction (shared root),
        // so one cell is enough — this only has to cover sub-wave-amplitude
        // interpolation error, not positional drift.
        private const int SkirtCells = 1;
        // Outermost vert row lands this many radii out — far past the fog
        // dome (~3000m), so the square mesh edge is never visible. Crest
        // pushes to the far plane; anything fog-opaque is equivalent here.
        private const float HorizonExtentMultiplier = 4f;
        // Zone water draws at 2901 and claims the bit-4 stencil; rings draw
        // after it, inner cascade first, so every overlap has a deterministic
        // winner (see class summary). Same base queue as the disk used.
        private const int BaseRenderQueue = 2950;
        // Vertical half-size of mesh bounds — max wave crest (~25m) plus
        // margin so GPU-displaced verts never get frustum-culled mid-frame.
        private const float WaveBoundsY = 60f;

        // Crest geomorph shader hooks (FiresWater.shader :: FiresClipmapGeomorph). Per-cascade
        // (morph flag / grid width / scale) set on each cascade material; center + black/white
        // points are globals shared by all cascades.
        private static readonly int P_ClipmapMorph = Shader.PropertyToID("_FiresClipmapMorph");
        private static readonly int P_GeoGridWidth = Shader.PropertyToID("_FiresGeoGridWidth");
        private static readonly int P_SeamCellMeters = Shader.PropertyToID("_FiresSeamCellMeters");
        private static readonly int P_ZoneEdgeMeters = Shader.PropertyToID("_FiresZoneEdgeMeters");
        private static readonly int P_LodScale     = Shader.PropertyToID("_FiresLodScale");
        private static readonly int P_OceanCenter  = Shader.PropertyToID("_FiresOceanCenter");
        private static readonly int P_LodAlphaBP   = Shader.PropertyToID("_FiresLodAlphaBP");
        private static readonly int P_LodAlphaWP   = Shader.PropertyToID("_FiresLodAlphaWP");

        private static GameObject s_root;
        private static MeshRenderer[] s_renderers;
        private static Mesh[] s_meshes;
        private static Material[] s_materials;
        private static float s_builtRadius = -1f;
        private static int s_builtLodCount = -1;
        private static float s_builtHole = -1f;
        private static float[] s_cellMeters;   // per-cascade cell size, for the tess budget
        private static int s_builtResolution = -1;
        private static float s_snapStepMeters;
        private static bool s_renderersVisible = true;
        private static float s_lastSnappedX = float.NaN;
        private static float s_lastSnappedZ = float.NaN;

        public static void Tick()
        {
            if (!Enabled)
            {
                Despawn();
                return;
            }

            var cam = Utils.GetMainCamera();
            if (cam == null) return;

            if (!EnsureBuilt()) return;   // zone-water material not captured yet — retry next tick

            // Same parasite rule as the disk: only draw while real zone water
            // is painting pixels (it claims the overlap stencil and it proves
            // the Water layer isn't culled for the space/planet view). Without
            // this gate the clipmap would flood the whole view — including
            // under the player — the instant zone water goes away.
            bool zoneWaterDrawing = IsZoneWaterRendering(cam);
            SetVisible(zoneWaterDrawing);
            if (!zoneWaterDrawing) return;

            Recenter(cam.transform.position);
        }

        // Re-push the wave-detail material props (shared sliders with the
        // disk); geometry params are picked up by EnsureBuilt on next Tick.
        public static void Retune()
        {
            ApplyMaterialTuning();
        }

        public static void Despawn()
        {
            if (s_root != null) { Object.Destroy(s_root); s_root = null; }
            if (s_meshes != null)
            {
                for (int i = 0; i < s_meshes.Length; i++)
                    if (s_meshes[i] != null) Object.Destroy(s_meshes[i]);
                s_meshes = null;
            }
            if (s_materials != null)
            {
                for (int i = 0; i < s_materials.Length; i++)
                    if (s_materials[i] != null) Object.Destroy(s_materials[i]);
                s_materials = null;
            }
            s_renderers = null;
            s_builtRadius = -1f;
            s_builtLodCount = -1;
            s_builtResolution = -1;
            s_lastSnappedX = float.NaN;
            s_lastSnappedZ = float.NaN;
            // No clipmap = no coarse mesh to protect: zone water reverts to the full
            // wave spectrum (the shader treats seamCell 0 as band-limit off for it).
            Shader.SetGlobalFloat(P_SeamCellMeters, 0f);
            Shader.SetGlobalFloat(P_ZoneEdgeMeters, 0f);
        }

        // LodTransform's texel snap, at the coarsest cascade's cell size so
        // every finer cascade (whose cells divide it exactly) stays on its own
        // global lattice too. Only X/Z follow the camera; Y is locked at sea
        // level so there's no water-at-altitude artifact (the tide offset is
        // added by the shader, exactly like zone water and the disk).
        private static void Recenter(Vector3 camPos)
        {
            float sx = camPos.x - Mathf.Repeat(camPos.x, s_snapStepMeters);
            float sz = camPos.z - Mathf.Repeat(camPos.z, s_snapStepMeters);
            if (sx == s_lastSnappedX && sz == s_lastSnappedZ) return;   // NaN after (re)build forces the first write

            s_lastSnappedX = sx;
            s_lastSnappedZ = sz;
            s_root.transform.position = new Vector3(sx, ZoneSystem.c_WaterLevel, sz);
            // The geomorph measures taxicab distance from THIS recentered origin.
            Shader.SetGlobalVector(P_OceanCenter, new Vector4(sx, ZoneSystem.c_WaterLevel, sz, 0f));
        }

        private static void SetVisible(bool visible)
        {
            if (s_renderers == null || s_renderersVisible == visible) return;
            s_renderersVisible = visible;
            for (int i = 0; i < s_renderers.Length; i++)
                if (s_renderers[i] != null) s_renderers[i].enabled = visible;
        }

        // (Re)build meshes + materials + hierarchy. Steady state is three int
        // compares; a rebuild only happens on config change (radius/LODs/res).
        private static bool EnsureBuilt()
        {
            int lods = Mathf.Clamp(LodCount, 2, 10);
            int res = Mathf.Clamp(PatchResolution, 8, 256);
            float radius = Mathf.Max(512f, TotalRadiusMeters);
            // The clipmap's inner hole tracks the LOADED zone-water edge (m_activeArea * 64m)
            // so it ABUTS the real water — the same rule the disk uses (holeZones =
            // m_activeArea). This kills BOTH failure modes at once:
            //   * no dense SOLID centre double-drawing on the near water (the old "double
            //     shoreline" — a config hole < the LOD-0 footprint quantized to NO hole and
            //     LOD-0 rendered solid over the pond), and
            //   * no empty ring between the loaded water and the first cascade (the coastal
            //     "gap" — exposed seabed where the ZoneTiles ring skips non-ocean zones).
            // The clipmap draws AFTER zone water (higher queue) sharing the bit-4 "first draw
            // claims the pixel" stencil, so its overlap with the loaded water + ZoneTiles is
            // culled; it only fills genuine empty ocean beyond the loaded box, INCLUDING the
            // coastal zones ZoneTiles leaves empty. The innermost KEPT cascade is given this
            // exact inner hole (see the build loop) instead of a solid centre. Auto-sized from
            // m_activeArea so it tracks the ring extender live. (2026-07-08 — supersedes the
            // brief hole-floor fix that removed the double layer but re-opened the coast gap.
            // InnerHoleRadius is now auto; the field is retained for compat but no longer sizes it.)
            // Overlap the hole INWARD by SeamOverlap zones (shared with the disk, default 1) so
            // the clipmap always keeps a mesh under the OUTERMOST loaded zone. Crossing a zone
            // boundary fast, the loaded set shifts and the new leading zone's WaterVolume loads a
            // frame or two late; without this the hole grows to exclude it before its water exists
            // → a one-frame no-water flash. The overlap keeps the clipmap drawing there (stencil-
            // culled wherever real water IS loaded, so no double-draw), so there's always a
            // surface. Clamped to >=1 zone so the immediate player area stays pure zone water.
            int clipActiveArea = FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear;
            int clipSeamOverlap = Mathf.Max(1, FiresWaterExtendedRing.SeamOverlapCells);
            float hole = Mathf.Max(64f, (clipActiveArea - clipSeamOverlap) * 64f);

            if (s_root != null && Mathf.Approximately(s_builtRadius, radius)
                               && s_builtLodCount == lods
                               && s_builtResolution == res
                               && Mathf.Approximately(s_builtHole, hole)) return true;

            // Clone the ACTUAL in-place-swapped zone-water material, exactly
            // like the disk does (see FiresWaterExtendedRing.EnsureMaterial for
            // why the WaterMaterialManager wrapped copy is NOT good enough —
            // its property-by-property rebuild diverged and seamed visibly).
            var zoneMat = FindZoneWaterMaterial();
            if (zoneMat == null) return false;

            Despawn();

            s_builtRadius = radius;
            s_builtLodCount = lods;
            s_builtResolution = res;
            s_builtHole = hole;

            // Outermost extent = 2 * S(lods-1) = S0 * 2^lods, so the LOD-0
            // patch size falls out of the requested radius. All cell sizes are
            // radius / power-of-two — exact in float, which keeps the lattice
            // math (snap + cross-ring alignment) drift-free.
            float lod0PatchMeters = radius / Mathf.Pow(2f, lods);
            // Snap at the LOD-0 cell, not the coarsest cascade's. The waves
            // are world-position driven in the shader, and cascade-to-cascade
            // edge alignment rides the SHARED ROOT (relative geometry) — so
            // the coarse snap bought nothing and cost a 62.5m clipmap
            // TELEPORT every time the camera crossed a coarse cell: the
            // whole ocean visibly lurched against the shore ("wiggling/
            // tweaking every tick"). A ~2m snap is imperceptible.
            s_snapStepMeters = lod0PatchMeters / res;
            float horizonHalfMeters = radius * HorizonExtentMultiplier;

            // Crest LOD-alpha remap constants (OceanRenderer.cs): bp = 0.4/(baseMeshDensity/8),
            // wp = 1 - 2*bp; baseMeshDensity = our per-patch resolution. Globals — same for
            // every cascade.
            float bp = 0.4f / Mathf.Max(1f, res / 8f);
            Shader.SetGlobalFloat(P_LodAlphaBP, bp);
            Shader.SetGlobalFloat(P_LodAlphaWP, Mathf.Max(1e-3f, 1f - 2f * bp));

            s_root = new GameObject("FiresClipmapOcean");
            Object.DontDestroyOnLoad(s_root);
            s_root.transform.SetPositionAndRotation(
                new Vector3(0f, ZoneSystem.c_WaterLevel, 0f), Quaternion.identity);

            s_meshes = new Mesh[lods];
            s_materials = new Material[lods];
            s_renderers = new MeshRenderer[lods];
            s_cellMeters = new float[lods];
            s_renderersVisible = true;

            // Inner cutout: skip every cascade that fits inside the hole. The first kept
            // ring's own inner hole spans the previous (skipped) LOD's extent, so the
            // cutout falls out of the ring construction for free — no special mesh.
            //
            // The hole is QUANTIZED to ring footprints, and it must round DOWN: a hole
            // slightly smaller than the request just overlaps zone water (which the
            // stencil resolves — the designed seam mechanism), but a hole slightly
            // larger overshoots the zone-water coverage and leaves a DRY STRIPE of
            // naked seabed between the loaded area and the first ring (field-reported:
            // an L-shaped sand band a couple hundred metres out). A request smaller
            // than the smallest ring footprint therefore yields NO hole at all.
            int firstLod = 0;
            while (firstLod < lods - 1 && 2f * lod0PatchMeters * Mathf.Pow(2f, firstLod) <= hole)
                firstLod++;

            // Wave band-limit seam handoff: tell the shader the FIRST kept cascade's cell
            // size + where zone water ends, so zone water fades the chop the clipmap can't
            // carry BEFORE the seam (the "waves reset at the end of the mesh" fix). Cleared
            // in Despawn so disk-mode / clipmap-off reverts zone water to the full spectrum.
            Shader.SetGlobalFloat(P_SeamCellMeters, lod0PatchMeters * Mathf.Pow(2f, firstLod) / res);
            Shader.SetGlobalFloat(P_ZoneEdgeMeters, hole);

            int totalVerts = 0, totalTris = 0;
            for (int lod = firstLod; lod < lods; lod++)
            {
                float cellMeters = lod0PatchMeters * Mathf.Pow(2f, lod) / res;
                s_cellMeters[lod] = cellMeters;
                // The innermost KEPT cascade abuts the loaded water (hole); every cascade
                // beyond it nests against the previous cascade's outer edge (the classic
                // clipmap ring hole). Passing metres lets BuildRingMesh cut the hole to the
                // real geometry instead of a fixed solid-centre special-case.
                float innerHoleMeters = (lod == firstLod) ? hole : 2f * lod0PatchMeters * Mathf.Pow(2f, lod - 1);
                s_meshes[lod] = BuildRingMesh(lod, lods, res, cellMeters, horizonHalfMeters, innerHoleMeters);
                totalVerts += s_meshes[lod].vertexCount;
                totalTris += s_meshes[lod].triangles.Length / 3;

                s_materials[lod] = new Material(zoneMat) { name = "FiresClipmapOcean_L" + lod };
                s_materials[lod].enableInstancing = false;     // one renderer per ring — nothing to instance
                s_materials[lod].renderQueue = BaseRenderQueue + lod;
                // Crest geomorph, per cascade: mark it a clipmap cascade (morph on + tess off),
                // and hand the shader this cascade's cell size + inner extent (Crest _scale =
                // the cascade's inner radius). The morph welds this cascade to the next.
                s_materials[lod].SetFloat(P_ClipmapMorph, 1f);
                s_materials[lod].SetFloat(P_GeoGridWidth, cellMeters);
                s_materials[lod].SetFloat(P_LodScale, lod0PatchMeters * Mathf.Pow(2f, lod));

                var go = new GameObject("FiresClipmapOcean_L" + lod);
                // Same layer story as the disk's FiresWaterRing GO (Default):
                // space-view visibility is owned by the zone-water gate, not by
                // layer culling, so the planet driver's Water-layer mask doesn't
                // need to know about us.
                go.transform.SetParent(s_root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = s_meshes[lod];

                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = s_materials[lod];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                mr.allowOcclusionWhenDynamic = false;
                mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                s_renderers[lod] = mr;
            }

            ApplyMaterialTuning();

            // Info-level on purpose — this is the line to look for in LogOutput.log when
            // A/B-ing against the disk (tris here is MESH tris, pre-tessellation).
            FiresLogger.LogInfo($"[FiresClipmapOcean] Built cascades {firstLod}..{lods - 1}: radius={radius:F0}m, "
                    + $"innerHole={hole:F0}m, res={res}, snap={s_snapStepMeters:F1}m, "
                    + $"{totalVerts} verts / {totalTris} mesh tris.");
            return true;
        }

        // Mirror of FiresWaterExtendedRing.ApplyMaterialTuning, on every
        // cascade material — driven by the SAME statics so the existing
        // "05 - Far Water Disk" 03/04 sliders tune whichever far-water system
        // is active. _IsLod=1 also opts the rings into the shader's
        // distant-water look path (_FiresDistant* globals), like the disk.
        private static void ApplyMaterialTuning()
        {
            if (s_materials == null) return;
            for (int i = 0; i < s_materials.Length; i++)
            {
                var m = s_materials[i];
                if (m == null) continue;
                // Tessellation BUDGET per cascade: the disk ran _Tess=64 on 64m cells
                // (~1m effective detail). A flat 64 on every ring re-tessellated the
                // already-dense inner cascades to centimetres — the tris explosion the
                // perf overlay showed. Scaling _Tess to the ring's own cell size keeps
                // the SAME ~1m effective detail on every cascade at a fraction of the
                // triangles; the floor is capped by the same budget.
                float cell = s_cellMeters != null && i < s_cellMeters.Length ? s_cellMeters[i] : 64f;
                float tess = Mathf.Clamp(cell, 2f, 64f);
                if (m.HasProperty("_Tess")) m.SetFloat("_Tess", tess);
                // _IsLod = 0 puts the clipmap on the FULL near-water look path: planar mirror
                // reflection (the real reflected scene, not the sky cubemap), full reflection
                // strength, and NO distant tint/brightness — so the far clipmap reads IDENTICAL
                // to the near zone water. The Crest geomorph (_FiresClipmapMorph) already forces
                // tess=1 in hsConstant, so _IsLod no longer needs to drive the tessellation LOD.
                if (m.HasProperty("_IsLod")) m.SetFloat("_IsLod", 0f);
                if (m.HasProperty("_VisibleMaxDistance"))
                    m.SetFloat("_VisibleMaxDistance", Mathf.Max(64f, FiresWaterExtendedRing.WaveDetailDistance));
                if (m.HasProperty("_IsLodMinTess"))
                    m.SetFloat("_IsLodMinTess", Mathf.Min(Mathf.Clamp(FiresWaterExtendedRing.WaveDetailFloor, 1f, 64f), tess));
            }
        }

        // One mesh per cascade. LOD 0 is a solid (4*res)^2-cell square; every
        // other ring is that square minus a hole spanning the previous LOD's
        // extent, shrunk inward by SkirtCells for the crack-hiding overlap.
        // The outermost ring adds one extra vert row mapped out to the horizon
        // (Crest's FatXOuter/FatXZOuter behaviour, single-mesh form).
        private static Mesh BuildRingMesh(int lodIndex, int lodCount, int res, float cellMeters, float horizonHalfMeters, float innerHoleMeters)
        {
            int half = 2 * res;                       // cells from center to the ring's outer edge (4 patches across, like Crest)
            bool outermost = lodIndex == lodCount - 1;
            int gMin = -half - (outermost ? 1 : 0);
            int gMax = half + (outermost ? 1 : 0);
            int sideVerts = gMax - gMin + 1;
            // Inner hole cut to the requested metres (minus the crack-hiding skirt), in this
            // cascade's own cells. The innermost kept cascade gets the loaded-water edge (so it
            // abuts the real water — no solid centre, no gap); outer cascades get the previous
            // cascade's extent (classic nested clipmap ring, == res - SkirtCells).
            int holeHalf = Mathf.Clamp(Mathf.RoundToInt(innerHoleMeters / cellMeters) - SkirtCells, 0, half - 1);

            // Verts are emitted lazily as quads reference them, so hole
            // interiors cost nothing; the map array is transient.
            var vertMap = new int[sideVerts * sideVerts];
            for (int i = 0; i < vertMap.Length; i++) vertMap[i] = -1;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            // Grid coord -> local meters; the extra outermost layer maps to
            // the horizon band instead of the lattice.
            float CoordOf(int g)
            {
                if (Mathf.Abs(g) <= half) return g * cellMeters;
                return Mathf.Sign(g) * horizonHalfMeters;
            }

            int GetVert(int gi, int gj)
            {
                int key = (gj - gMin) * sideVerts + (gi - gMin);
                int idx = vertMap[key];
                if (idx >= 0) return idx;
                idx = verts.Count;
                vertMap[key] = idx;
                verts.Add(new Vector3(CoordOf(gi), 0f, CoordOf(gj)));
                uvs.Add(new Vector2(gi, gj));   // unused by the shader (world-XZ foam), kept for mesh-format parity with the disk
                return idx;
            }

            for (int gj = gMin; gj < gMax; gj++)
            {
                for (int gi = gMin; gi < gMax; gi++)
                {
                    // Skip the hole — the finer cascade owns it (minus the skirt overlap).
                    if (gi >= -holeHalf && gi < holeHalf && gj >= -holeHalf && gj < holeHalf) continue;

                    int i0 = GetVert(gi, gj);
                    int i1 = GetVert(gi + 1, gj);
                    int i2 = GetVert(gi, gj + 1);
                    int i3 = GetVert(gi + 1, gj + 1);
                    // Up-facing winding, same as the proven disk/tile meshes.
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    tris.Add(i1); tris.Add(i2); tris.Add(i3);
                }
            }

            var normals = new Vector3[verts.Count];
            var colors = new Color[verts.Count];
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = Vector3.up;
                colors[i] = Color.white;
            }

            var mesh = new Mesh { name = "FiresClipmapOcean_L" + lodIndex };
            mesh.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.SetTriangles(tris, 0);

            // Explicit bounds: cover the ring plus max wave displacement plus a
            // few cells of snap allowance (Crest #1148) so displaced verts are
            // never frustum-culled at the edge of a snap step.
            float ext = (outermost ? horizonHalfMeters : half * cellMeters) + cellMeters * 3f;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(ext * 2f, WaveBoundsY * 2f, ext * 2f));
            mesh.UploadMeshData(false);
            return mesh;
        }

        // Same "is real zone water actually painting pixels" test the disk
        // uses (private there, so mirrored): find a loaded ocean WaterVolume
        // wearing our shader and confirm its layer is in the camera's mask.
        private static bool IsZoneWaterRendering(Camera cam)
        {
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

        // The shared ocean surface material AFTER FiresWaterShaderLoader
        // swapped it in place — the disk's acquisition path, mirrored (its
        // finder is private). Never adopt a still-vanilla material: it would
        // render without the underwater pass and without the bit-4 stencil
        // the whole overlap scheme depends on.
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
    }

    /// <summary>
    /// Boat-float fallback while the clipmap owns the far water — the exact
    /// counterpart of <see cref="Floating_GetWaterLevel_ExtendedRingFallback"/>
    /// (which gates on the disk's flag, forced off while the clipmap is on).
    /// Without this, a far boat sailing over the render-only clipmap would
    /// sink the moment vanilla finds no WaterVolume under it.
    /// </summary>
    [HarmonyPatch(typeof(Floating), nameof(Floating.GetWaterLevel))]
    internal static class Floating_GetWaterLevel_ClipmapOceanFallback
    {
        private const float NoWaterSentinel = -100f;   // vanilla uses -10000; wide margin
        private const float SeaLevelBandMeters = 50f;

        private static readonly int TideOffsetProp = Shader.PropertyToID("_FiresTideOffset");

        private static void Postfix(Vector3 p, ref float __result)
        {
            if (__result > NoWaterSentinel) return;    // vanilla found real water
            if (!FiresClipmapOcean.Enabled) return;

            // 0.3.161 ([seasons]/[worldgen]: the raw tide offset with no shore damping put the fallback 4.4 m off the swim surface at
            // shores): the tide scaled by the shared field's influence plus its river raise, the numbers the buoyancy weld reads
            float tide = Shader.GetGlobalFloat(TideOffsetProp);
            var field = FiresCore.Services.WaterState.SurfaceFieldSampler;
            float sea;
            if (field != null) { Vector2 f = field(p.x, p.z); sea = ZoneSystem.c_WaterLevel + tide * f.x + f.y; }
            else sea = ZoneSystem.c_WaterLevel + tide;
            if (Mathf.Abs(p.y - sea) > SeaLevelBandMeters) return;   // stops phantom water under inland builds

            __result = sea;
        }
    }

    /// <summary>
    /// Per-frame driver, mounted unconditionally like the disk's. Steady state
    /// while disabled is one bool check; while enabled it's a couple of float
    /// compares plus (on a coarse-cell crossing) one transform write.
    /// </summary>
    public class FiresClipmapOceanDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (Player.m_localPlayer == null) return;   // defer until in-world
            FiresClipmapOcean.Tick();
        }

        private void OnDisable()
        {
            FiresClipmapOcean.Despawn();
        }
    }
}
