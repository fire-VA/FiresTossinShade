using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Per-axis (X/Y/Z) scaling of the LOCAL player's vanilla hair and beard meshes. The instances live on
    /// VisEquipment (children of the head bone) and are reachable directly because FTS builds against the
    /// publicized assembly. We capture each instance's natural localScale the first time we see it and
    /// multiply by the configured factor every frame, so the scale survives re-equip / vanilla re-attach.
    /// A factor of 1 leaves the mesh untouched; disabling restores the captured scale.
    /// </summary>
    public static class FiresHairBeardScale
    {
        public static bool HairEnabled  = false;
        public static bool BeardEnabled = false;
        public static Vector3 HairScale  = Vector3.one;
        public static Vector3 BeardScale = Vector3.one;

        private static GameObject s_hairGO, s_beardGO;
        private static Vector3 s_hairBase = Vector3.one, s_beardBase = Vector3.one;

        // Called every frame by FiresElfEarsDriver (independent of the elf-ear feature).
        public static void Apply()
        {
            if (!HairEnabled && !BeardEnabled && s_hairGO == null && s_beardGO == null) return;
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) return;
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null) return;

            ApplyOne(ve.m_hairItemInstance,  HairEnabled,  HairScale,  ref s_hairGO,  ref s_hairBase);
            ApplyOne(ve.m_beardItemInstance, BeardEnabled, BeardScale, ref s_beardGO, ref s_beardBase);
        }

        private static void ApplyOne(GameObject go, bool enabled, Vector3 scale, ref GameObject cached, ref Vector3 baseScale)
        {
            if (go == null) { cached = null; return; }
            if (!enabled)
            {
                if (cached == go) { go.transform.localScale = baseScale; cached = null; }   // restore once, then idle
                return;
            }
            if (cached != go) { cached = go; baseScale = go.transform.localScale; }          // new instance → capture natural scale
            go.transform.localScale = Vector3.Scale(baseScale, scale);
        }
    }
}
