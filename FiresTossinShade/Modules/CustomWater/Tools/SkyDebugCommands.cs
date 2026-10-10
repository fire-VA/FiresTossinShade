using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// F5 console command for diagnosing the sky systems.
    ///
    ///   * va_sky_info — dumps the current sun direction (elevation/azimuth),
    ///                   the game day + ToD fraction, and the live fog / ambient
    ///                   / directional-light render state, so you can tell
    ///                   whether a flat-blue object is fog or lighting instead of
    ///                   guessing from a screenshot.
    ///
    /// (The moon console commands moved out with the moon system.)
    /// </summary>
    public static class SkyDebugCommands
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand(
                "va_sky_info",
                "Prints current sun direction + live fog/ambient/light render state",
                args => SkyInfo(args), isCheat: false);

            new Terminal.ConsoleCommand(
                "va_rdump",
                "Render-order diagnostic: dumps every camera's CommandBuffers (per event) + transparent renderers (queue>=2450). Run while the fog artifact / lightning is on screen.",
                args => RDump(args), isCheat: false);
        }

        // ── va_rdump : render-order diagnostic (logs to console AND BepInEx LogOutput.log) ──
        private static void RDump(Terminal.ConsoleEventArgs args)
        {
            void P(string s) { args.Context.AddString(s); Debug.Log("[va_rdump] " + s); }
            P("=== va_rdump ===");
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null) continue;
                P($"camera '{cam.name}' depth={cam.depth} depthMode={cam.depthTextureMode} clear={cam.clearFlags}");
                foreach (UnityEngine.Rendering.CameraEvent evt in System.Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
                {
                    var bufs = cam.GetCommandBuffers(evt);
                    if (bufs != null) foreach (var b in bufs) P($"   CB [{evt}] '{b.name}'");
                }
            }
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var m = r.sharedMaterial; if (m == null || m.shader == null) continue;
                int q = m.renderQueue; if (q < 2450) continue;
                float sz = r.bounds.size.magnitude;
                list.Add(new System.Collections.Generic.KeyValuePair<int, string>(q,
                    $"   q{q} {r.GetType().Name} '{r.name}' shader='{m.shader.name}' RT={m.GetTag("RenderType", false, "?")} zwrite={(m.HasProperty("_ZWrite") ? m.GetFloat("_ZWrite").ToString("0") : "-")} size={sz:F0}"));
            }
            list.Sort((a, b) => b.Key.CompareTo(a.Key));
            P($"--- transparent renderers (queue>=2450): {list.Count} ---");
            for (int i = 0; i < Mathf.Min(list.Count, 45); i++) P(list[i].Value);
            P("=== end va_rdump ===");
        }

        // ── va_sky_info ───────────────────────────────────────────────────

        private static void SkyInfo(Terminal.ConsoleEventArgs args)
        {
            Vector4 sunDirVec = Shader.GetGlobalVector("_SkyboxSunDir");
            Vector3 sunDir = new Vector3(sunDirVec.x, sunDirVec.y, sunDirVec.z);
            float sunMag = sunDir.magnitude;

            args.Context.AddString($"=== va_sky_info ===");
            args.Context.AddString($"_SkyboxSunDir raw: ({sunDirVec.x:F3}, {sunDirVec.y:F3}, {sunDirVec.z:F3})  |mag|={sunMag:F3}");

            if (sunMag < 0.001f)
            {
                args.Context.AddString("WARNING: _SkyboxSunDir is zero — EnvMan global is not bound. " +
                                       "Sun will pin to fallback direction. Check EnvMan exists + UpdateLighting is running.");
                return;
            }

            sunDir.Normalize();
            float sunElevDeg = Mathf.Asin(Mathf.Clamp(sunDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float sunAzDeg = Mathf.Atan2(sunDir.x, sunDir.z) * Mathf.Rad2Deg;
            string sunAH = sunDir.y > 0 ? "ABOVE horizon (day)" : "BELOW horizon (night)";

            args.Context.AddString($"Sun:  elev={sunElevDeg:F1}°  azimuth={sunAzDeg:F1}° (0=N, 90=E)  {sunAH}");

            if (EnvMan.instance != null)
            {
                int day = EnvMan.instance.GetDay(ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0);
                float dayFrac = EnvMan.instance.GetDayFraction();
                args.Context.AddString($"Game day: {day}  dayFraction: {dayFrac:F3}  (0=morning, 0.25=noon, 0.5=evening, 0.75=midnight)");
            }

            // === Live render state — read the ACTUAL fog / ambient / light values
            // so we stop inferring whether a flat-blue object is fog or lighting.
            args.Context.AddString("--- render state ---");
            args.Context.AddString($"fog: enabled={RenderSettings.fog}  mode={RenderSettings.fogMode}  "
                + $"density={RenderSettings.fogDensity:F4}  color={ColStr(RenderSettings.fogColor)}");
            args.Context.AddString($"ambient: mode={RenderSettings.ambientMode}  light={ColStr(RenderSettings.ambientLight)}");

            int dirCount = 0;
            Light bright = null;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional) continue;
                dirCount++;
                if (bright == null || l.intensity > bright.intensity) bright = l;
            }
            if (bright != null)
            {
                bool litsLayer0 = (bright.cullingMask & 1) != 0;
                args.Context.AddString($"dirLights={dirCount}  brightest: intensity={bright.intensity:F2}  "
                    + $"color={ColStr(bright.color)}  enabled={bright.enabled}  cullMask=0x{bright.cullingMask:X}  "
                    + $"litsLayer0={litsLayer0}");
            }
            else
            {
                args.Context.AddString($"dirLights={dirCount} (NO directional light found — everything is ambient-only!)");
            }
        }

        private static string ColStr(Color c)
            => $"({c.r:F2},{c.g:F2},{c.b:F2})";

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Terminal_InitTerminal_RegisterSkyDebug
        {
            private static void Postfix() => Register();
        }
    }
}
