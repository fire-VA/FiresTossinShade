using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Bumps ZoneSystem's zone-loading range while the local player is
    /// over deep open water so the inner ring of animated FiresWater zone
    /// meshes extends far enough that storm-wave crests never reach the
    /// vanilla LOD-plane boundary. Reverts to the captured vanilla value
    /// the moment the player returns to a shallow / near-shore position.
    ///
    /// Also owns the vanilla water_lod plane's visibility — three modes:
    ///   * HideLodWater = true                       -> renderer disabled
    ///   * MaskLodOutsideZones = true (default)      -> swap to FiresWaterLod
    ///                                                  shader (stencil-masked
    ///                                                  so it only fills past
    ///                                                  the FiresWater ring)
    ///   * both false                                -> vanilla LOD as shipped
    /// </summary>
    public static class OceanZoneRingExtender
    {
        public static bool Enabled = true;
        public static int ExtendedActiveArea = 2;
        public static float OceanDepthThreshold = 10f;

        // Vanilla water_lod is GONE as of 2026-06-01 — even with our
        // FiresWater shader swapped in, the low-poly base mesh made it
        // look plasticky compared to the per-zone meshes. User decision:
        // just kill it. Defaults now hide unconditionally. The
        // FiresWaterExtendedRing (animated tiles up to 10240m) + the
        // FiresWaterFlatWorldPlane (static ring with player-following
        // hole) cover the same area without the LOD look.
        // Both flags KEPT in the API so the config-binding code in
        // FiresTossinShade.cs still compiles, but HideLodWater is now
        // force-true regardless of config value.
        public static bool HideLodWater = true;

        // Stencil-masked LOD path is DEAD. Force off and ignore even if
        // the config says true — the masked path is what produced the
        // plasticky middle ring the user wanted removed.
        public static bool MaskLodOutsideZones = false;

        private const float LodRescanInterval = 5.0f;
        private static float s_nextLodRescan;
        private static LodEntry[] s_lodEntries;

        private const int UncapturedSentinel = -1;
        private static int s_vanillaActiveArea = UncapturedSentinel;
        private static bool s_isExtended;

        // For ZoneContentSuppressor: is the active area currently bumped, and what's
        // the vanilla (un-bumped) value? Zones farther than this from the player are
        // the "extra" ring — they get water+terrain only, no content.
        public static bool IsExtended => s_isExtended;
        public static int VanillaActiveArea => s_vanillaActiveArea == UncapturedSentinel ? 1 : s_vanillaActiveArea;

        public static void TickFromPlayer(Vector3 playerPos)
        {
            if (ZoneSystem.instance == null) return;

            if (s_vanillaActiveArea == UncapturedSentinel)
                s_vanillaActiveArea = FiresCore.Bridge.SimulationDistanceAccess.Near;

            if (!Enabled)
            {
                if (s_isExtended) ApplyVanilla();
                return;
            }

            // No deep-ocean / shoreline gate — extend unconditionally while enabled,
            // so the real zone water reaches out everywhere (the way it worked
            // before). Content waking in the extra zones is suppressed separately
            // (ZoneContentSuppressor), so this no longer needs the gate to stay safe.
            if (!s_isExtended) ApplyExtended();
        }

        public static void RestoreVanillaImmediate()
        {
            if (s_vanillaActiveArea == UncapturedSentinel || ZoneSystem.instance == null) return;
            ApplyVanilla();
        }

        // Valheim 1.0 replaced ZoneSystem.m_activeArea with a private SimulationDistance struct that
        // ZoneSystem refreshes from ZNet in ApplySettings(). Writing it still does what the bump always
        // did - it is the value CreateLocalZones loops over - but it no longer drags ZDO object creation
        // along with it, because ZNetScene.CreateDestroyObjects now asks ZNet directly instead of reading
        // ZoneSystem's copy. That separation is what ZoneContentSuppressor used to hand-roll.
        private static void SetNear(int near)
        {
            var current = FiresCore.Bridge.SimulationDistanceAccess.Current;
            if (FiresCore.Bridge.SimulationDistanceAccess.TryGetZoneLoading(out var live)) current = live;
            FiresCore.Bridge.SimulationDistanceAccess.TrySetZoneLoading(
                FiresCore.Bridge.SimulationDistanceAccess.WithNear(current, near));
        }

        private static void ApplyExtended()
        {
            SetNear(ExtendedActiveArea);
            s_isExtended = true;
        }

        private static void ApplyVanilla()
        {
            SetNear(s_vanillaActiveArea);
            s_isExtended = false;
        }

        private static bool IsOverDeepOcean(Vector3 worldPos)
        {
            float terrainY;
            if (!Heightmap.GetHeight(worldPos, out terrainY)) return true;
            return ZoneSystem.c_WaterLevel - terrainY > OceanDepthThreshold;
        }

        // Per-renderer LOD bookkeeping. We track the original sharedMaterial
        // AND the original transform localScale so a config toggle can
        // restore vanilla without a scene reload.
        private struct LodEntry
        {
            public MeshRenderer renderer;
            public Material originalMaterial;
            public Vector3 originalScale;
        }

        // Scale the LOD plane's GameObject transform by this factor when
        // the stencil-masked material is installed. Vanilla water_lod is
        // a fixed-size disc that follows the player; at default scale it
        // visibly cuts off at the horizon when fog is reduced (e.g. while
        // aurora glow pushes FogHeightMin down). Scaling 10× makes the
        // disc cover ~10× the area without changing any other vanilla
        // logic — boats/Floating use WaterVolume colliders so they're
        // unaffected by visual mesh size.
        public static float LodPlaneScale = 10.0f;

        // Apply HideLodWater / MaskLodOutsideZones to the cached LOD
        // renderers. Re-scans the scene at most every LodRescanInterval
        // seconds (or immediately if the cache is empty / a cached
        // renderer was destroyed) so a post-scene-transition fresh LOD
        // plane gets picked up. Steady state cost: one comparison + one
        // assignment per renderer per tick.
        public static void TickLodWaterVisibility()
        {
            bool cacheStale = s_lodEntries == null
                           || HasNullEntry(s_lodEntries)
                           || Time.unscaledTime >= s_nextLodRescan;
            if (cacheStale)
            {
                RescanLodRenderers();
                s_nextLodRescan = Time.unscaledTime + LodRescanInterval;
            }

            if (s_lodEntries == null || s_lodEntries.Length == 0) return;

            // HARD-FORCE: vanilla LOD water is killed unconditionally now.
            // Config toggles ignored. The masked-LOD swap is DEAD CODE in
            // this path — we never build the masked material, never scale
            // up. Just disable every LOD renderer we find. The good water
            // (FiresWaterExtendedRing + FiresWaterFlatWorldPlane + zone
            // water with FiresWater shader) covers everything they would
            // have covered.
            for (int i = 0; i < s_lodEntries.Length; i++)
            {
                var e = s_lodEntries[i];
                var r = e.renderer;
                if (r == null) continue;
                if (r.enabled) r.enabled = false;
            }
        }

        public static void RestoreLodWaterVisibility()
        {
            if (s_lodEntries == null) return;
            for (int i = 0; i < s_lodEntries.Length; i++)
            {
                var e = s_lodEntries[i];
                if (e.renderer == null) continue;
                if (!e.renderer.enabled) e.renderer.enabled = true;
                if (e.originalMaterial != null && e.renderer.sharedMaterial != e.originalMaterial)
                    e.renderer.sharedMaterial = e.originalMaterial;
                var t = e.renderer.transform;
                if (t.localScale != e.originalScale) t.localScale = e.originalScale;
            }
        }

        // Cached single instance of the LOD material — a renderQueue-shifted
        // clone of the captured FiresWater material. Same shader as the
        // zone meshes (zero possible color seam) but rendered AFTER them
        // so FiresWater's existing bit-4 stencil Pass Invert handles the
        // masking automatically: zone meshes at queue 2901 draw first and
        // claim bit 4; the LOD at queue 2950 is culled where bit 4 is
        // set (inside the active zone ring) and draws where bit 4 is
        // clear (outside it). Outside-ring overlaps between LOD plane
        // segments are also handled by the same bit-4 claim — first to
        // draw wins, no double-blend.
        //
        // No separate FiresWaterLod shader anymore. The prior approach
        // of writing a parallel shader produced visible color seams (no
        // matter how closely we matched the formula, output diverged
        // slightly). Using the exact same shader is the only way to get
        // a guaranteed visual match.
        private const int LodRenderQueue = 2950;
        private static Material s_maskedLodMaterial;

        private static Material EnsureMaskedLodMaterial()
        {
            if (s_maskedLodMaterial != null) return s_maskedLodMaterial;

            var src = WaterMaterialManager.GetWaterMaterial(LiquidType.Water);
            if (src == null) return null;

            // Clone preserves every property the source has set. Shader
            // stays as the FiresWater shader (same waves / colors / foam
            // / refraction / stencil block) so the LOD draws pixel-
            // identical to the zone meshes. Queue override forces draw
            // order — zone meshes (2901) first, LOD (2950) second, bit-4
            // stencil culls LOD where zones already painted.
            s_maskedLodMaterial = new Material(src) { name = "FiresWaterLod_runtime" };
            s_maskedLodMaterial.renderQueue = LodRenderQueue;

            // The captured source material's _depth[4] is whatever the
            // CAPTURED zone had at its corners — could be shore-shallow
            // values if the capture happened in a near-shore zone. Even
            // though the FiresWater shader hardcodes SampleDepth=1.0
            // ignoring the array, we set [1,1,1,1] explicitly here so
            // (a) the value matches FiresWaterExtendedRing tiles for
            // consistency, and (b) any future shader revision that
            // re-reads _depth still sees deep-ocean values. Same for
            // _UseGlobalWind — force-on so waves animate regardless of
            // the captured material's value.
            if (s_maskedLodMaterial.HasProperty("_depth"))
                s_maskedLodMaterial.SetFloatArray("_depth", new float[] { 1f, 1f, 1f, 1f });
            if (s_maskedLodMaterial.HasProperty("_UseGlobalWind"))
                s_maskedLodMaterial.SetFloat("_UseGlobalWind", 1f);

            // The vanilla water_lod mesh is a low-poly disc — scaling it
            // 10× makes each input triangle huge in world space, which
            // means HW tessellation (default _Tess=16) produces sub-quads
            // ~40m+ across. Storm waves (~10-20m wavelength) can't be
            // represented at that density → visible "flat band" of low-
            // detail water between the FiresWater zones and the far
            // horizon. Push _Tess to max (64 = D3D11 hardware ceiling)
            // so the LOD plane gets finer sub-quads and waves read more
            // like the foreground. Range constraint in the .shader is
            // 1..32 for the Inspector slider; SetFloat bypasses that.
            if (s_maskedLodMaterial.HasProperty("_Tess"))
                s_maskedLodMaterial.SetFloat("_Tess", 64f);

            FiresLogger.LogVerbose("[OceanZoneRing] Built LOD material — clone of captured FiresWater, queue " + LodRenderQueue + ", depth=1, _Tess=64.");
            return s_maskedLodMaterial;
        }

        private static bool HasNullEntry(LodEntry[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].renderer == null) return true;
            return false;
        }

        private static void RescanLodRenderers()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<MeshRenderer>();
                var hits = new System.Collections.Generic.List<LodEntry>();
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i];
                    if (r == null) continue;
                    if (!r.gameObject.scene.IsValid()) continue;
                    var mat = r.sharedMaterial;
                    if (mat == null || mat.name == null) continue;

                    // Accept the renderer if EITHER (a) its material is
                    // vanilla's water_lod (first-time adoption) OR (b)
                    // its material is already our wrapped clone (re-scan
                    // after we swapped). Earlier the filter was just (a)
                    // which dropped wrapped renderers on subsequent
                    // scans — log line "Cached 0 LOD water renderer(s)"
                    // forever after the first successful adoption. Cache
                    // loss meant the scale + restore-on-toggle bookkeeping
                    // was broken and any external mutation of the
                    // sharedMaterial would silently revert us.
                    bool isVanillaLodName = mat.name.StartsWith("water_lod");
                    bool isOurClone = (s_maskedLodMaterial != null && mat == s_maskedLodMaterial);
                    if (!isVanillaLodName && !isOurClone) continue;

                    // If already in the cache, preserve the existing
                    // entry so we keep the original material pointer
                    // (needed for restore) and original scale.
                    if (isOurClone && s_lodEntries != null)
                    {
                        bool already = false;
                        for (int j = 0; j < s_lodEntries.Length; j++)
                        {
                            if (s_lodEntries[j].renderer == r)
                            {
                                hits.Add(s_lodEntries[j]);
                                already = true;
                                break;
                            }
                        }
                        if (already) continue;
                    }

                    hits.Add(new LodEntry
                    {
                        renderer = r,
                        originalMaterial = mat,
                        originalScale = r.transform.localScale,
                    });
                }
                int previousCount = s_lodEntries.Length;
                s_lodEntries = hits.ToArray();
                // Rescan runs every few seconds; only log when the count
                // actually changes. Spam otherwise dominates the log when
                // OceanRing is enabled at a stable scene.
                if (s_lodEntries.Length != previousCount)
                    FiresLogger.LogVerbose($"[OceanZoneRing] Cached {s_lodEntries.Length} LOD water renderer(s) (was {previousCount}).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[OceanZoneRing] RescanLodRenderers failed: {ex.Message}");
                s_lodEntries = new LodEntry[0];
            }
        }
    }

    public class OceanZoneRingExtenderDriver : MonoBehaviour
    {
        private const float CheckIntervalSec = 1.0f;

        private float m_nextCheckTime;

        private void Update()
        {
            if (Time.unscaledTime < m_nextCheckTime) return;
            m_nextCheckTime = Time.unscaledTime + CheckIntervalSec;

            var player = Player.m_localPlayer;
            if (player == null) return;

            OceanZoneRingExtender.TickFromPlayer(player.transform.position);
            // LOD-water hiding is owned by FiresWaterExtendedRing now (it deactivates
            // the whole WaterPlane), so we no longer scan/disable renderers here.
        }

        private void OnDisable()
        {
            OceanZoneRingExtender.RestoreVanillaImmediate();
            OceanZoneRingExtender.RestoreLodWaterVisibility();
        }
    }
}
