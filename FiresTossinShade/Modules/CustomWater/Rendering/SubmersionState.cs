using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Whether the local player's view is underwater, read off the vanilla
    /// <c>Player.m_swimDepth</c> field. Self-contained so the shader mod owns its
    /// underwater-view trigger without depending on the diving mechanic (a separate mod).
    ///
    /// HYSTERESIS (2026-07-07): a single depth threshold strobes the underwater view on
    /// and off as each wave rolls the head across the line — worse near shore, where the
    /// visual wave mesh and the physical buoyancy surface don't perfectly agree, so the
    /// body bobs relative to what you SEE. Fix is a hysteresis band plus a minimum hold:
    /// once the view commits above/below it won't flip back until the head clears the
    /// FAR threshold, and never faster than <see cref="MinHoldSeconds"/>. One wave can no
    /// longer toggle it — it takes a sustained, decisive crossing.
    /// </summary>
    internal static class SubmersionState
    {
        private const float DefaultSwimDepth = 1.4f;   // vanilla treading depth

        // Hysteresis band around treading depth: go UNDER only past EnterDepth, come
        // back UP only once shallower than ExitDepth. The 0.3m gap is wider than typical
        // near-shore wave chop, so a passing swell can't strobe the view.
        private const float EnterDepth = DefaultSwimDepth + 0.30f;   // 1.70 — commit to underwater
        private const float ExitDepth  = DefaultSwimDepth + 0.05f;   // 1.45 — commit to above-water
        private const float MinHoldSeconds = 0.4f;                   // floor on how fast the view can flip

        private static bool s_submerged;
        private static float s_lastFlip;

        public static bool LocalIsSubmerged()
        {
            var p = Player.m_localPlayer;
            if (p == null) { s_submerged = false; return false; }

            float d = p.m_swimDepth;
            // Inside the band [ExitDepth, EnterDepth] the state is held (that's the
            // hysteresis); only a crossing of the far edge proposes a flip.
            bool want = s_submerged ? (d > ExitDepth) : (d > EnterDepth);

            if (want != s_submerged && Time.time - s_lastFlip >= MinHoldSeconds)
            {
                s_submerged = want;
                s_lastFlip = Time.time;
            }
            return s_submerged;
        }
    }
}
