using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Real Unity directional Light that originates from the aurora's
    /// position in the north sky. Adds aurora-colored ambient + specular
    /// to the world so:
    ///   * Water gets aurora-tinted reflections at night
    ///   * Surfaces facing north (where aurora is brightest) get subtle
    ///     green/cyan/violet rim light
    ///   * Bloom + sun-shaft post-processes can pick this up as a real
    ///     light source for god-ray emission
    ///
    /// Visibility is gated on: aurora enabled + night-time + not stormy.
    /// Color tracks the same cycled palette as the visible aurora ribbons
    /// in the skybox shader (FiresSkyboxAurora). Intensity is exposed as
    /// a config slider so the user can dial how dramatic the aurora
    /// lighting is — 0 = no lighting (visible aurora only), 0.5 = subtle
    /// rim, 1.0+ = pronounced aurora-tinted scene.
    ///
    /// Light TRANSFORM is positioned in world space at a configurable
    /// elevation + azimuth (default 60° elevation, north azimuth) — that
    /// way the SECOND SunShafts pass (FiresAuroraShaftsPass) can use this
    /// transform as its sunTransform and shafts radiate from the same
    /// world position the aurora visually originates from.
    /// </summary>
    public static class FiresAuroraLight
    {
        public static bool Enabled = true;
        public static float Intensity = 0.5f;
        public static float ElevationDeg = 60f;
        public static float AzimuthDeg = 0f;
        public static bool NightOnly = true;
        public static bool DuringStorms = false;

        public static GameObject LightGO { get; private set; }
        public static Light LightComp { get; private set; }
        public static Transform Anchor { get; private set; }

        // Far-distance anchor for SunShafts.WorldToViewportPoint reference.
        // Anything past 1000m works since shaft origin is screen-space.
        private const float AnchorDistance = 2000f;

        public static void Tick()
        {
            EnsureLight();
            if (LightComp == null) return;

            bool wantOn = Enabled
                && Intensity > 0.001f
                && (!NightOnly || IsNight())
                && (DuringStorms || !IsStormy());

            if (LightComp.enabled != wantOn) LightComp.enabled = wantOn;
            if (!wantOn) return;

            // Aurora-cycled color from the shared palette × pulse, exactly
            // matching what the skybox shader is painting for its color
            // stops. Same source → guaranteed visual sync between sky
            // aurora and lighting tint.
            float palette = Time.unscaledTime * FiresSkyboxAurora.CycleSpeed;
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * FiresSkyboxAurora.PulseSpeed);
            Color cycled = FiresSkyboxDriver.SampleAuroraStops(palette);

            LightComp.color = cycled;
            LightComp.intensity = Intensity * pulse;

            // Update anchor world-position so SunShafts (if pointing at
            // this transform) emits shafts from the correct screen-space
            // location. Anchor follows the camera with the aurora-direction
            // offset; SunShafts.WorldToViewportPoint converts to screen.
            var cam = Utils.GetMainCamera();
            if (cam != null && Anchor != null)
            {
                Vector3 dir = ComputeAuroraDir();
                Anchor.position = cam.transform.position + dir * AnchorDistance;
                // Light's direction = pointing TOWARD ground (forward = -dir)
                LightGO.transform.rotation = Quaternion.LookRotation(-dir);
            }
        }

        public static void RestoreImmediate()
        {
            if (LightGO != null)
            {
                Object.Destroy(LightGO);
                LightGO = null;
                LightComp = null;
                Anchor = null;
            }
        }

        // World-space direction TOWARD the aurora origin (from camera).
        // Elevation 60° + azimuth 0° (north) by default.
        public static Vector3 ComputeAuroraDir()
        {
            float el = ElevationDeg * Mathf.Deg2Rad;
            float az = AzimuthDeg * Mathf.Deg2Rad;
            float ce = Mathf.Cos(el), se = Mathf.Sin(el);
            float ca = Mathf.Cos(az), sa = Mathf.Sin(az);
            return new Vector3(sa * ce, se, ca * ce).normalized;
        }

        private static void EnsureLight()
        {
            if (LightGO != null) return;

            LightGO = new GameObject("FiresAuroraLight");
            Object.DontDestroyOnLoad(LightGO);

            LightComp = LightGO.AddComponent<Light>();
            LightComp.type = LightType.Directional;
            LightComp.shadows = LightShadows.None;     // aurora is too diffuse for crisp shadows
            LightComp.renderMode = LightRenderMode.Auto;
            LightComp.cullingMask = ~0;                    // all layers
            LightComp.intensity = 0f;                    // set per-frame
            LightComp.color = Color.white;           // set per-frame
            LightComp.enabled = false;                 // Tick activates

            // Anchor transform — a child object that lives at the aurora's
            // world position. Used by FiresAuroraShaftsPass as the
            // sunTransform reference.
            var anchorGO = new GameObject("FiresAuroraLight_Anchor");
            anchorGO.transform.SetParent(LightGO.transform, worldPositionStays: false);
            Anchor = anchorGO.transform;

            FiresLogger.LogVerbose("[FiresAuroraLight] Created directional light + shaft anchor.");
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

    public class FiresAuroraLightDriver : MonoBehaviour
    {
        private void LateUpdate() { FiresAuroraLight.Tick(); }
        private void OnDisable() { FiresAuroraLight.RestoreImmediate(); }
    }
}
