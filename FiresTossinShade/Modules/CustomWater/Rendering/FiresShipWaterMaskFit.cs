using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Keeps the ocean off a boat that has dropped BELOW the water surface.
    ///
    /// THE PROBLEM
    /// Vanilla boats carry a Custom/WaterMask mesh (render queue 2899) shaped like the
    /// hull interior, up to the rim. FiresWater's TopSurface pass reads that mask's
    /// stencil bit and skips drawing water over the interior — perfect while the boat
    /// floats. But when the deck drops under the surface (a whirlpool sink, or a big
    /// storm swell heaving over the rail), the risen water is ABOVE the mask's top, so
    /// the ocean paints straight over the deck and the player — the translucent "water
    /// over the boat" wash.
    ///
    /// THE FIX (user's call, 2026-07-07: "draw the mask at the lowest point of the boat,
    /// not the top edge")
    /// While — and only while — the local player's boat is below the surface, stretch
    /// that mask UPWARD, anchored at the keel (the boat's lowest point), just far enough
    /// to reach the surface plus a small margin. The instant the boat floats normally
    /// again the mask is restored to vanilla, so ordinary sailing is untouched: no
    /// water-hole punched above the deck from a third-person camera, because the
    /// stretch only exists when the boat is already submerged and surrounded by water.
    ///
    /// Client-only (visual). Restores the mask on every gated-off frame and whenever the
    /// tracked ship changes, so it can never strand a stretched mask.
    /// </summary>
    public sealed class FiresShipWaterMaskFit : MonoBehaviour
    {
        // Pushed from ConfigManager via FiresTossinShade.ApplyFiresWaterConfig.
        public static bool Enabled = true;
        // Metres above the surface to raise the mask top so a little chop at the
        // waterline doesn't re-expose the deck. 0 disables the whole driver.
        public static float ExtendMargin = 1.5f;

        private const float SubmergeTrigger = 0.1f;  // surface must clear the mask top by this to act
        private const string MaskShaderName = "Custom/WaterMask";

        private sealed class MaskRig
        {
            public Transform T;
            public Vector3 OrigLocalScale;
            public Vector3 OrigLocalPos;
            public Vector3 KeelLocal;   // mesh-local point at the lowest Y (the keel)
            public Vector3 TopLocal;    // mesh-local point at the highest Y (the rim)
            public bool Stretched;
        }

        private Ship m_ship;
        private MaskRig m_rig;

        private void OnDisable() => Restore();

        private void LateUpdate()
        {
            bool gatedOff = !Enabled
                || ExtendMargin <= 0f
                || !FiresWaterShaderLoader.Enabled;

            var player = Player.m_localPlayer;
            Ship ship = null;
            if (!gatedOff && player != null)
                ship = player.GetStandingOnShip() ?? Ship.GetLocalShip();

            if (ship != m_ship)
            {
                Restore();                       // put the old ship's mask back before switching
                m_ship = ship;
                m_rig = ship != null ? BuildRig(ship) : null;
            }

            if (ship == null || m_rig == null) { Restore(); return; }

            var t = m_rig.T;
            if (t == null) { m_ship = null; m_rig = null; return; }

            // Reset to the vanilla pose FIRST, then measure — Unity applies transform
            // changes immediately, so the world points read below reflect the orig mask.
            t.localScale = m_rig.OrigLocalScale;
            t.localPosition = m_rig.OrigLocalPos;
            m_rig.Stretched = false;

            float keelY = t.TransformPoint(m_rig.KeelLocal).y;
            float topY = t.TransformPoint(m_rig.TopLocal).y;

            float surfaceY = FiresCore.Services.WaterState.GetWaterSurface(m_ship.transform.position);

            // Floating normally (or keel-up) — leave the vanilla mask exactly as-is.
            if (surfaceY <= topY + SubmergeTrigger) return;

            // Stretch the mask so its top reaches the surface + margin, keel pinned.
            float desiredHeight = (surfaceY + ExtendMargin) - keelY;
            float currentHeight = Mathf.Max(0.05f, topY - keelY);
            float factor = desiredHeight / currentHeight;

            var ls = t.localScale;
            ls.y = m_rig.OrigLocalScale.y * factor;
            t.localScale = ls;

            // Re-anchor the keel in WORLD space (robust to any parent/local rotation
            // or non-uniform boat scale — we correct wherever the keel point actually
            // moved, not by a local-axis assumption).
            float keelAfter = t.TransformPoint(m_rig.KeelLocal).y;
            Vector3 wp = t.position;
            wp.y += keelY - keelAfter;
            t.position = wp;

            m_rig.Stretched = true;
        }

        private static MaskRig BuildRig(Ship ship)
        {
            foreach (var mr in ship.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mat = mr.sharedMaterial;
                if (mat == null) continue;
                bool isMask = (mat.shader != null && mat.shader.name == MaskShaderName)
                    || mat.renderQueue == 2899;
                if (!isMask) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Bounds b = mf.sharedMesh.bounds;
                var t = mr.transform;
                return new MaskRig
                {
                    T = t,
                    OrigLocalScale = t.localScale,
                    OrigLocalPos = t.localPosition,
                    KeelLocal = new Vector3(b.center.x, b.min.y, b.center.z),
                    TopLocal = new Vector3(b.center.x, b.max.y, b.center.z),
                };
            }
            return null;
        }

        private void Restore()
        {
            if (m_rig != null && m_rig.Stretched && m_rig.T != null)
            {
                m_rig.T.localScale = m_rig.OrigLocalScale;
                m_rig.T.localPosition = m_rig.OrigLocalPos;
                m_rig.Stretched = false;
            }
        }
    }
}
