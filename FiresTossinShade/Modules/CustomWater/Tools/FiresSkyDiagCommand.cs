using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// `fires_skydiag` — one-shot dump of the sky/fog/storm/dome state PLUS every
    /// lightning/flash/cloud renderer's blend + shadowcaster + bounds, written to
    /// BepInEx/FiresSkyDiag.txt. Then arms a capture of the NEXT lightning flash's
    /// renderer tree (appended), so the thunderstorm "hard cutout" artifact can be
    /// diagnosed from numbers instead of screenshots. Run it during a ThunderStorm
    /// (force with `env ThunderStorm`), then wait for / trigger a strike.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class FiresSkyDiagCommand
    {
        private static bool s_reg;
        internal static string DiagPath;
        internal static bool ArmFlashCapture;
        internal static int FlashesLeft;

        private static void Postfix()
        {
            if (s_reg) return;
            s_reg = true;
            try { DiagPath = Path.GetFullPath(Path.Combine(BepInEx.Paths.ConfigPath, "..", "FiresSkyDiag.txt")); }
            catch { DiagPath = "FiresSkyDiag.txt"; }

            new Terminal.ConsoleCommand("fires_skydiag",
                "Dump sky/fog/storm/dome + lightning renderer state to FiresSkyDiag.txt; arm next-flash capture.",
                args =>
                {
                    var sb = new StringBuilder(8192);
                    void L(string s) { args.Context.AddString(s); sb.AppendLine(s); }

                    var p = Player.m_localPlayer;
                    Vector3 pos = p != null ? p.transform.position : Vector3.zero;
                    var cam = Utils.GetMainCamera();
                    float camY = cam != null ? cam.transform.position.y : pos.y;

                    L($"=== SKY DIAG @ player({pos.x:F0},{pos.y:F0},{pos.z:F0}) camY={camY:F0} ===");

                    // Environment / weather
                    string envName = "?"; float rainAlpha = -1f; bool wet = false;
                    if (EnvMan.instance != null)
                    {
                        var env = EnvMan.instance.GetCurrentEnvironment();
                        if (env != null) { envName = env.m_name; rainAlpha = env.m_rainCloudAlpha; wet = env.m_isWet; }
                    }
                    L($"env='{envName}'  m_rainCloudAlpha={rainAlpha:F3}  isWet={wet}");

                    // Unity fog — the dome's storm dissolve target. If fog is OFF the dome
                    // can't dissolve into unity_FogColor and will silhouette during a flash.
                    L($"RenderSettings.fog={RenderSettings.fog}  mode={RenderSettings.fogMode}  " +
                      $"color={FmtC(RenderSettings.fogColor)}  density={RenderSettings.fogDensity:F4}  " +
                      $"start={RenderSettings.fogStartDistance:F0} end={RenderSettings.fogEndDistance:F0}");

                    // Shader globals the dome + skybox consume (pulled into locals so the
                    // property-name string literals don't nest inside interpolation holes).
                    float gFogMode  = Shader.GetGlobalFloat("_FiresFogMode");
                    float gCloudRain= Shader.GetGlobalFloat("_FiresCloudRain");
                    Vector4 gSun    = Shader.GetGlobalVector("_SunColor");
                    Vector4 gAmb    = Shader.GetGlobalVector("_AmbientColor");
                    Vector4 gSunDir = Shader.GetGlobalVector("_SkyboxSunDir");
                    float gDarken   = Shader.GetGlobalFloat("_FiresStormDarken");
                    float gCover    = Shader.GetGlobalFloat("_FiresStormCover");
                    float gOpacity  = Shader.GetGlobalFloat("_FiresStormOpacity");
                    float gInside   = Shader.GetGlobalFloat("_FiresPlanetInside");
                    float domeTop   = FiresPlanet.DomeBaseY + FiresPlanet.DomeHeight;
                    L($"globals: _FiresFogMode={gFogMode:F0}  _FiresCloudRain={gCloudRain:F3}");
                    L($"  _SunColor={FmtV(gSun)}  (dirLight.color*intensity — a flash SPIKES this)");
                    L($"  _AmbientColor={FmtV(gAmb)}");
                    L($"  _SkyboxSunDir={FmtV(gSunDir)}");
                    L($"  _FiresStormDarken={gDarken:F2}  _FiresStormCover={gCover:F2}  " +
                      $"_FiresStormOpacity={gOpacity:F2}  _FiresPlanetInside={gInside:F0}");
                    L($"FiresPlanet.Enabled={FiresPlanet.Enabled}  " +
                      $"FiresLightningBlend.Enabled={FiresLightningBlend.Enabled}  domeTop={domeTop:F0}");

                    // Enumerate lightning / flash / cloud renderers currently in scene
                    L("--- LIGHTNING / FLASH / CLOUD RENDERERS (name|shader|blend|shadow|bounds) ---");
                    int shown = DumpRenderers(L, pos, new[] { "lightning", "thunder", "flash", "flipbook", "sparc", "cloud" });
                    if (shown == 0) L("  (none found — not currently a strike frame; the armed capture below will catch the next flash)");

                    // Active lights near the player (the flash Light shows here during a strike)
                    L("--- ACTIVE LIGHTS near player (type|intensity|color|range|pos|path) ---");
                    DumpLights(L, pos);

                    try { File.WriteAllText(DiagPath, sb.ToString()); args.Context.AddString($"[skydiag] wrote {DiagPath}"); }
                    catch (Exception e) { args.Context.AddString($"[skydiag] FILE WRITE FAILED: {e.Message}"); }

                    ArmFlashCapture = true; FlashesLeft = 3;
                    args.Context.AddString("[skydiag] ARMED — the next 3 lightning flashes append their renderer tree to the file.");
                },
                isCheat: false);
        }

        private static string FmtC(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";
        private static string FmtV(Vector4 v) => $"({v.x:F2},{v.y:F2},{v.z:F2},{v.w:F2})";

        // Map a _SrcBlend/_DstBlend pair to a readable blend + whether it's additive.
        internal static string BlendOf(Material m)
        {
            if (m == null) return "nullmat";
            if (!m.HasProperty("_DstBlend")) return "no-blend-prop";
            int src = (int)m.GetFloat("_SrcBlend");
            int dst = (int)m.GetFloat("_DstBlend");
            string tag = (dst == (int)UnityEngine.Rendering.BlendMode.One) ? "ADDITIVE" : "alpha";
            return $"src={src} dst={dst}({tag})";
        }

        internal static int DumpRenderers(Action<string> L, Vector3 pos, string[] needles)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            var rows = new List<(float d, string line)>();
            foreach (var r in all)
            {
                if (r == null) continue;
                var mat = r.sharedMaterial;
                string sh = (mat != null && mat.shader != null) ? mat.shader.name : "";
                string nm = r.gameObject.name;
                string hay = (nm + " " + sh).ToLowerInvariant();
                bool hit = false;
                foreach (var n in needles) if (hay.Contains(n)) { hit = true; break; }
                if (!hit) continue;

                float d = Vector3.Distance(r.transform.position, pos);
                var b = r.bounds.size;
                rows.Add((d,
                    $"  d={d,6:F1} {(r.enabled ? "ON " : "off")} actv={(r.gameObject.activeInHierarchy ? "Y" : "n")} " +
                    $"shadow={r.shadowCastingMode} blend={BlendOf(mat)} " +
                    $"bounds=({b.x:F1},{b.y:F1},{b.z:F1}) sh='{sh}' go='{nm}'"));
            }
            rows.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var row in rows) L(row.line);
            return rows.Count;
        }

        private static void DumpLights(Action<string> L, Vector3 pos)
        {
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var rows = new List<(float d, string line)>();
            var parked = new Dictionary<string, int>();
            foreach (var lt in lights)
            {
                if (lt == null || !lt.enabled) continue;
                float d = Vector3.Distance(lt.transform.position, pos);
                if (d > 600f) continue;
                if (!lt.gameObject.activeInHierarchy)
                {
                    // An enabled Light on an inactive GameObject renders NOTHING — these are parked
                    // template clones (Fires_PrefabHolder / FiresNpcTemplatePieces) and prefab dev
                    // lights (the NPCs' inactive DevEffects magenta). Listing them as live once read
                    // as a 43-light "leak" stacked at the holder — summarize per root instead.
                    string root = lt.transform.root != null ? lt.transform.root.name : lt.gameObject.name;
                    parked.TryGetValue(root, out int n);
                    parked[root] = n + 1;
                    continue;
                }
                Vector3 wp = lt.transform.position;
                rows.Add((d, $"  d={d,6:F1} {lt.type} intensity={lt.intensity:F2} color={FmtC(lt.color)} range={lt.range:F0} " +
                             $"pos=({wp.x:F0},{wp.y:F0},{wp.z:F0}) at='{HierarchyPath(lt.transform)}'"));
            }
            rows.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var row in rows) L(row.line);
            if (rows.Count == 0) L("  (no active lights within 600m)");
            foreach (var kv in parked)
                L($"  (+{kv.Value} enabled-but-INACTIVE light(s) under root '{kv.Key}' - not rendering, ignore)");
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            Transform cur = t.parent;
            for (int depth = 0; cur != null && depth < 10; depth++)
            {
                sb.Insert(0, cur.name + "/");
                cur = cur.parent;
            }
            if (cur != null) sb.Insert(0, ".../");
            return sb.ToString();
        }
    }

    /// <summary>
    /// One-shot capture: when fires_skydiag armed it, append the renderer tree of the
    /// next few lightning flashes (spawned via EffectList.Create by Thunder.DoFlash) to
    /// the diag file — the deterministic way to see the actual flash card's blend +
    /// shadowcaster + bounds at spawn time.
    /// </summary>
    [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
    internal static class FiresSkyDiagFlashCapture
    {
        // Run LAST so FiresLightningBlend's card-hide has already applied — the enabled=
        // reading then reflects the final state (card should read enabled=False).
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(GameObject[] __result)
        {
            if (!FiresSkyDiagCommand.ArmFlashCapture || __result == null) return;
            try
            {
                var sb = new StringBuilder();
                bool any = false;
                foreach (var go in __result)
                {
                    if (go == null) continue;
                    string gname = go.name.ToLowerInvariant();
                    bool looksFlash = gname.Contains("lightning") || gname.Contains("thunder") || gname.Contains("flash");
                    // Also inspect children names in case the root is generically named.
                    var rends = go.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in rends)
                    {
                        if (r == null) continue;
                        string rn = r.gameObject.name.ToLowerInvariant();
                        if (!looksFlash && !(rn.Contains("lightning") || rn.Contains("flash") || rn.Contains("flipbook") || rn.Contains("sparc"))) continue;
                        any = true;
                        var mat = r.sharedMaterial;
                        string sh = (mat != null && mat.shader != null) ? mat.shader.name : "";
                        var b = r.bounds.size;
                        bool notSwapped = sh.IndexOf("Unlit/Lighting", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        sb.AppendLine($"  FLASH r: enabled={r.enabled} {(notSwapped ? "[CARD-NOT-SWAPPED-bad]" : "[swapped-ok]")} " +
                                      $"shadow={r.shadowCastingMode} blend={FiresSkyDiagCommand.BlendOf(mat)} " +
                                      $"cast? {(r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off ? "STILL-CASTS(bad)" : "off(good)")} " +
                                      $"bounds=({b.x:F1},{b.y:F1},{b.z:F1}) sh='{sh}' root='{go.name}' go='{r.gameObject.name}'");
                    }
                }
                if (any && FiresSkyDiagCommand.DiagPath != null)
                {
                    File.AppendAllText(FiresSkyDiagCommand.DiagPath,
                        $"--- FLASH CAPTURE @ t={Time.time:F1} ---\n{sb}");
                    if (--FiresSkyDiagCommand.FlashesLeft <= 0) FiresSkyDiagCommand.ArmFlashCapture = false;
                }
            }
            catch { /* diagnostics must never break FX spawning */ }
        }
    }
}
