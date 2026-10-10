using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Extends the REAL zone water outward by a configurable number of 64m zones —
    /// water only, nothing else from the zone.
    ///
    /// WHY (vs the old approaches)
    /// Vanilla's good water is the per-zone <see cref="WaterVolume"/> surface, and
    /// it only exists in LOADED zones (bounded by ZoneSystem's zone-loading range). Bumping
    /// That range grows it but loads the WHOLE zone with it — trees, mobs,
    /// spawners — which is the megabase freeze. A single big overlay disc
    /// (FiresWaterExtendedRing) covers the area cheaply but is a DIFFERENT mesh, so
    /// its wave tessellation never matches the per-zone meshes and you see a seam
    /// where they meet.
    ///
    /// WHAT THIS DOES
    /// For each EMPTY ocean zone in a ring around the player (from the loaded edge+1 out
    /// to +<see cref="ExtendedZones"/>), it clones the real zone water SURFACE — the
    /// exact same mesh, tessellation, material and world-space wave field — and parks
    /// it at that zone's centre. Because it's the identical mesh on the identical 64m
    /// grid, it welds to the real zone water with ZERO seam. It is render-only (no
    /// WaterVolume, no collider, no content), and it only spawns over genuine deep
    /// ocean (WorldGenerator height check) so it never floods land. The tiles sit on
    /// the vanilla "Water" layer, so they cull with the real water (e.g. the planet
    /// space-view) for free.
    ///
    /// Far horizon beyond the tiles is still covered by FiresWaterExtendedRing, whose
    /// hole is widened to the tile reach so the two never overlap.
    /// </summary>
    public static class FiresWaterZoneTiles
    {
        public static bool Enabled = true;
        public static int ExtendedZones = 2;     // real-water zones beyond the loaded edge (the slider)

        private const int TileSize = 64;

        private static readonly Dictionary<Vector2s, GameObject> s_tiles = new Dictionary<Vector2s, GameObject>();
        private static readonly List<Vector2s> s_removeScratch = new List<Vector2s>();

        private static GameObject s_template;     // parked, disabled clone of a real zone water surface
        private static Material s_material;      // the live FiresWater ocean material (shared)
        private static Vector2s s_lastCenter = new Vector2s(short.MinValue, short.MinValue);
        private static int s_lastReach = -1;

        public static void Tick(Vector3 playerPos)
        {
            if (!Enabled) { ClearAll(); return; }

            var zs = ZoneSystem.instance;
            if (zs == null || WorldGenerator.instance == null) return;
            if (!EnsureTemplate()) return;        // need a live FiresWater surface to clone first

            int active = Mathf.Max(0, FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear);
            int reach = active + Mathf.Max(0, ExtendedZones);
            Vector2s center = ZoneSystem.GetZone(playerPos);

            if (center == s_lastCenter && reach == s_lastReach && s_tiles.Count > 0) return;  // set still valid
            s_lastCenter = center;
            s_lastReach = reach;

            PruneOutOfRange(center, active, reach, zs);
            FillRing(center, active, reach, zs);
        }

        public static void ClearAll()
        {
            foreach (var kv in s_tiles)
                if (kv.Value != null) Object.Destroy(kv.Value);
            s_tiles.Clear();
            s_lastCenter = new Vector2s(short.MinValue, short.MinValue);
            s_lastReach = -1;
        }

        // Drop tiles that fell outside the ring, or whose zone has since loaded as a
        // real zone (so the real WaterVolume now owns that water — never double up).
        private static void PruneOutOfRange(Vector2s center, int active, int reach, ZoneSystem zs)
        {
            s_removeScratch.Clear();
            foreach (var kv in s_tiles)
            {
                Vector2s z = kv.Key;
                int dx = Mathf.Abs(z.x - center.x);
                int dz = Mathf.Abs(z.y - center.y);
                bool inRing = dx <= reach && dz <= reach && (dx > active || dz > active);
                if (!inRing || zs.IsZoneLoaded(z) || kv.Value == null)
                    s_removeScratch.Add(z);
            }
            for (int i = 0; i < s_removeScratch.Count; i++)
            {
                var z = s_removeScratch[i];
                if (s_tiles.TryGetValue(z, out var go) && go != null) Object.Destroy(go);
                s_tiles.Remove(z);
            }
        }

        private static void FillRing(Vector2s center, int active, int reach, ZoneSystem zs)
        {
            float sea = ZoneSystem.c_WaterLevel;
            for (int dz = -reach; dz <= reach; dz++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (Mathf.Abs(dx) <= active && Mathf.Abs(dz) <= active) continue;  // real zone water owns these
                    var zone = new Vector2s(center.x + dx, center.y + dz);
                    if (s_tiles.ContainsKey(zone)) continue;
                    if (zs.IsZoneLoaded(zone)) continue;        // real water already there
                    if (!IsFullyOcean(zone, sea)) continue;     // skip land / coastline → no flooding
                    s_tiles[zone] = CreateTile(zone, sea);
                }
            }
        }

        // A tile is flat sea level across the whole 64m cell, so only place one where
        // the ENTIRE zone is below sea level — otherwise it would flood a shoreline.
        // The four corners (worst case) are sampled from the procedural world height,
        // which needs no zone load.
        private static bool IsFullyOcean(Vector2s zone, float sea)
        {
            Vector3 c = ZoneSystem.GetZonePos(zone);
            const float h = TileSize * 0.5f;
            var wg = WorldGenerator.instance;
            return wg.GetHeight(c.x - h, c.z - h) < sea
                && wg.GetHeight(c.x + h, c.z - h) < sea
                && wg.GetHeight(c.x - h, c.z + h) < sea
                && wg.GetHeight(c.x + h, c.z + h) < sea;
        }

        private static GameObject CreateTile(Vector2s zone, float sea)
        {
            var go = Object.Instantiate(s_template);
            Vector3 c = ZoneSystem.GetZonePos(zone);
            go.transform.SetPositionAndRotation(new Vector3(c.x, sea, c.z), Quaternion.identity);
            go.SetActive(true);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && s_material != null) mr.sharedMaterial = s_material;
            return go;
        }

        // Adopt the live ocean surface (the shared material FiresWaterShaderLoader
        // swapped to Custom/FiresWater) and clone ONE of its zone surfaces into a
        // parked, disabled template. Every tile is an Instantiate of that template,
        // so they share its mesh and render pixel-identical to the zone meshes.
        private static bool EnsureTemplate()
        {
            if (s_template != null && s_material != null) return true;

            var instances = WaterVolume.Instances;
            if (instances == null) return false;
            for (int i = 0; i < instances.Count; i++)
            {
                var wv = instances[i];
                if (wv == null || wv.m_waterSurface == null) continue;
                var mat = wv.m_waterSurface.sharedMaterial;
                if (mat == null || mat.shader == null
                    || mat.shader.name != FiresWaterShaderLoader.ShaderName) continue;

                s_material = mat;
                s_template = Object.Instantiate(wv.m_waterSurface.gameObject);
                s_template.name = "FiresWaterZoneTile_template";
                s_template.transform.SetParent(null, true);
                Object.DontDestroyOnLoad(s_template);
                s_template.SetActive(false);

                var mr = s_template.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                }
                Debug.Log("[FiresWaterZoneTiles] Cloned zone water surface as tile template "
                        + $"(mesh shared, material='{mat.name}').");
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Per-frame driver. The tile set only rebuilds when the player crosses into a
    /// new 64m zone (or the slider changes), so the steady-state cost is one
    /// GetZone + a couple of int compares.
    /// </summary>
    public class FiresWaterZoneTilesDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            FiresWaterZoneTiles.Tick(player.transform.position);
        }

        private void OnDisable()
        {
            FiresWaterZoneTiles.ClearAll();
        }
    }
}
