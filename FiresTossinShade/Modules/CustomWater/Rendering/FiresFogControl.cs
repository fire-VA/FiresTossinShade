using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Live master control over Valheim's distance fog — the thing that was
    /// turning every far VANILLA-material object (terrain Custom/Heightmap, the
    /// orbital's Custom/Piece, etc.) into a flat fog-coloured silhouette while
    /// our celestial bundle shaders (no fog) stayed crisp.
    ///
    /// EnvMan.SetEnv writes RenderSettings.fogColor + fogDensity every frame from
    /// the active environment (and does NOT touch the master RenderSettings.fog
    /// flag). This postfix runs right after and re-imposes our overrides each
    /// frame so EnvMan can't undo them:
    ///   * Enabled       — master on/off for ALL distance fog.
    ///   * ColorOverride — replace the fog colour/hue with a fixed Color.
    ///   * Mode          — Auto keeps the env's fog mode; Linear/Exp/Exp2 force it.
    ///   * Linear start/end — for Mode=Linear, the explicit "clear until START,
    ///                        fully fogged by END" distance. This is the real
    ///                        "how far before things fog out" lever — density
    ///                        scaling can't push exp fog's render distance well.
    ///   * DensityScale  — multiplies env fog density (only bites Exp/Exp2 modes;
    ///                     a no-op if the env fog is Linear).
    /// </summary>
    public static class FiresFogControl
    {
        public enum ModeOption { Auto, Linear, Exponential, ExponentialSquared }

        /// <summary>Master-FX gate. False = stand down entirely: undo whatever we changed and leave
        /// RenderSettings exactly as EnvMan wrote it. This is NOT the vanilla-fog switch.</summary>
        public static bool Active = true;

        /// <summary>Diagnostic: force Valheim's distance fog off. Separate from <see cref="Active"/> so
        /// turning our FX off can never delete vanilla's fog.</summary>
        public static bool VanillaFogEnabled = true;

        public static float DensityScale = 1.0f;
        public static bool ColorOverride = false;
        public static Color Color = new Color(0.6f, 0.66f, 0.72f, 1f);
        public static ModeOption Mode = ModeOption.Auto;
        public static float LinearStart = 200f;
        public static float LinearEnd = 2500f;
    }

    [HarmonyPatch(typeof(EnvMan), "SetEnv",
        new[] { typeof(EnvSetup), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) })]
    internal static class EnvMan_SetEnv_FogControl
    {
        // Only restore the master flag to TRUE if WE turned it off — so we don't
        // stomp other systems (EnvironmentBoxes, the underwater fog driver) that
        // legitimately drive RenderSettings.fog.
        private static bool s_weDisabled;

        // EnvMan never writes RenderSettings.fogMode, so once we force Linear it
        // would stay Linear forever. Capture the vanilla mode on first run and
        // restore it in Auto, so switching Mode back to Auto truly reverts.
        private static bool s_capturedMode;
        private static FogMode s_origMode;
        private static bool s_weChangedMode;

        private static void Postfix()
        {
            if (!s_capturedMode)
            {
                s_origMode = RenderSettings.fogMode;
                s_capturedMode = true;
            }

            if (!FiresFogControl.Active)
            {
                RestoreVanillaFogState();
                return;
            }

            if (!FiresFogControl.VanillaFogEnabled)
            {
                RenderSettings.fog = false;
                s_weDisabled = true;
                return;
            }

            if (s_weDisabled)
            {
                RenderSettings.fog = true;
                s_weDisabled = false;
            }

            // Colour / hue of the haze.
            if (FiresFogControl.ColorOverride)
                RenderSettings.fogColor = FiresFogControl.Color;

            // Mode + render distance.
            switch (FiresFogControl.Mode)
            {
                case FiresFogControl.ModeOption.Linear:
                    RenderSettings.fogMode = FogMode.Linear;
                    RenderSettings.fogStartDistance = FiresFogControl.LinearStart;
                    RenderSettings.fogEndDistance = Mathf.Max(FiresFogControl.LinearStart + 1f, FiresFogControl.LinearEnd);
                    s_weChangedMode = true;
                    break;

                case FiresFogControl.ModeOption.Exponential:
                    RenderSettings.fogMode = FogMode.Exponential;
                    RenderSettings.fogDensity *= Mathf.Max(0f, FiresFogControl.DensityScale);
                    s_weChangedMode = true;
                    break;

                case FiresFogControl.ModeOption.ExponentialSquared:
                    RenderSettings.fogMode = FogMode.ExponentialSquared;
                    RenderSettings.fogDensity *= Mathf.Max(0f, FiresFogControl.DensityScale);
                    s_weChangedMode = true;
                    break;

                default: // Auto — restore the vanilla mode, just scale density.
                    RenderSettings.fogMode = s_origMode;
                    s_weChangedMode = false;
                    if (FiresFogControl.DensityScale != 1.0f)
                        RenderSettings.fogDensity *= Mathf.Max(0f, FiresFogControl.DensityScale);
                    break;
            }
        }

        /// <summary>Undo our two persistent writes (the master fog flag and the forced fog mode) once, then
        /// leave RenderSettings alone so EnvMan's per-frame values stand unmodified.</summary>
        private static void RestoreVanillaFogState()
        {
            if (s_weDisabled)
            {
                RenderSettings.fog = true;
                s_weDisabled = false;
            }

            if (s_weChangedMode)
            {
                RenderSettings.fogMode = s_origMode;
                s_weChangedMode = false;
            }
        }
    }
}
