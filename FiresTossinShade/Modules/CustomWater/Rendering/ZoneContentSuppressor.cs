using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Makes OceanZoneRingExtender's EXTRA zones load water + terrain ONLY — no
    /// vegetation, locations, or creature spawners. This is what lets the active-area
    /// bump run UNCONDITIONALLY (no deep-ocean / shoreline gate): the gate only
    /// existed to stop the bump from waking land content, and now we suppress that
    /// content directly instead.
    ///
    /// HOW: ZoneSystem.SpawnZone always instantiates m_zonePrefab (the zone's Heightmap
    /// + WaterVolume) regardless of mode, but only runs PlaceLocations / PlaceVegetation
    /// / PlaceZoneCtrl (the content + spawners) when mode is Full or Ghost. So for a
    /// zone in the EXTRA ring — farther from the player than the vanilla m_activeArea —
    /// we downgrade Full → Client in a prefix. The water + terrain still spawn; the
    /// content block is skipped; the zone is NOT marked generated, so when it later
    /// enters the real active area it generates normally.
    ///
    /// SCOPE: this suppresses first-gen procedural content (trees, rocks, dungeons,
    /// spawn points). Re-streamed ZDO objects (player builds) are created by ZNetScene
    /// separately and are NOT gated here — over open ocean there are none, so the
    /// extension reads as pure water; near a saved megabase those would still stream
    /// (a separate ZNetScene gate would be needed for that case).
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
    internal static class ZoneSystem_SpawnZone_WaterOnlyExtra
    {
        private static void Prefix(Vector2s zoneID, ref ZoneSystem.SpawnMode mode)
        {
            if (mode != ZoneSystem.SpawnMode.Full) return;      // leave Ghost/Client passes alone
            if (!OceanZoneRingExtender.IsExtended) return;      // only while the bump is active

            var player = Player.m_localPlayer;
            if (player == null) return;

            Vector2s pz = ZoneSystem.GetZone(player.transform.position);
            int cheb = Mathf.Max(Mathf.Abs(zoneID.x - pz.x), Mathf.Abs(zoneID.y - pz.y));
            if (cheb > OceanZoneRingExtender.VanillaActiveArea)
                mode = ZoneSystem.SpawnMode.Client;             // water + terrain only, no content, not marked generated
        }
    }

    // The companion patch that used to live here - ZNetScene_CreateDestroyObjects_GateExtra - is gone.
    // It temporarily restored ZoneSystem.m_activeArea to its vanilla value around CreateDestroyObjects so
    // the bumped radius would not wake saved bases and creatures in the extra ring. Valheim 1.0 does that
    // separation itself: CreateDestroyObjects now calls ZNet.GetSyncedSimulationDistance() directly rather
    // than reading ZoneSystem's copy, so bumping ZoneSystem's zone-loading range no longer reaches ZDO
    // object creation at all. Keeping the swap would have been a no-op at best and a race at worst.
}
