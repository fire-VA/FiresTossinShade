using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// `va_sky_dump` — exhaustive discovery of every sky-dome / cloud-layer /
    /// distance-fog / Yggdrasil / ground-mist GameObject currently live.
    ///
    /// The "weird negative band on distant mountains" the user sees is almost
    /// certainly one of these intermediate render layers (typically a thin
    /// inward-facing sphere or hemisphere with a cloud/fog shader). Badger's
    /// Valheim Skies looks for them by name and logs "not found" because the
    /// names changed across Valheim versions — so we discover them at runtime
    /// via EnvMan's MeshRenderer references PLUS a name-pattern scan.
    ///
    /// Output format:
    ///   * EnvMan refs (m_clouds / m_rainClouds / m_rainCloudsDownside) —
    ///     the dome we KNOW EnvMan uses; full GO path + bounds + shader
    ///   * Name-pattern scan: cloud, sky, dome, Yggdrasil, fog, mist, distant
    ///   * Each entry: world position, scale, bounds.size, shader name, layer
    ///
    /// Sorted by bounds.size descending so the biggest sphere/dome surfaces first.
    /// Full report written to BepInEx/config/FiresTossinShade/sky_dump.txt.
    /// </summary>
    public static class SkyDumpCommand
    {
        private const string CommandName = "va_sky_dump";
        private const int TerminalLineLimit = 40;
        private const string DumpFileName = "sky_dump.txt";

        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(
                CommandName,
                "Dumps every cloud/sky/dome/fog/mist GameObject in the scene. " +
                "Full report → BepInEx/config/FiresTossinShade/sky_dump.txt",
                args => Execute(args), isCheat: false);
        }

        private static readonly string[] NamePatterns =
        {
            "cloud", "sky", "dome", "yggdrasil", "fogcloud", "distantcloud",
            "groundmist", "mistlands_clouds", "skybox", "atmosphere"
        };

        private struct Find
        {
            public string source;     // "EnvMan.m_clouds" / "name-scan" / etc.
            public string path;
            public Vector3 worldPos;
            public Vector3 lossyScale;
            public Vector3 boundsSize;
            public string shader;
            public int layer;
            public bool active;
        }

        private static string Hierarchy(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }

        private static void RecordRenderer(List<Find> finds, Renderer r, string source)
        {
            if (r == null) return;
            string shader = (r.sharedMaterial != null && r.sharedMaterial.shader != null)
                ? r.sharedMaterial.shader.name
                : "<no shader>";
            finds.Add(new Find
            {
                source = source,
                path = Hierarchy(r.transform),
                worldPos = r.transform.position,
                lossyScale = r.transform.lossyScale,
                boundsSize = r.bounds.size,
                shader = shader,
                layer = r.gameObject.layer,
                active = r.gameObject.activeInHierarchy,
            });
        }

        private static void RecordTransform(List<Find> finds, Transform t, string source)
        {
            if (t == null) return;
            var r = t.GetComponent<Renderer>();
            if (r != null) { RecordRenderer(finds, r, source); return; }
            // No renderer — just record the bare transform
            finds.Add(new Find
            {
                source = source,
                path = Hierarchy(t),
                worldPos = t.position,
                lossyScale = t.lossyScale,
                boundsSize = Vector3.zero,
                shader = "<no renderer>",
                layer = t.gameObject.layer,
                active = t.gameObject.activeInHierarchy,
            });
        }

        private static void Execute(Terminal.ConsoleEventArgs args)
        {
            var finds = new List<Find>(64);
            var seenIDs = new HashSet<int>();

            // ── 1. EnvMan-referenced cloud meshes (definitive)
            try
            {
                var env = EnvMan.instance;
                if (env != null)
                {
                    var envType = typeof(EnvMan);
                    foreach (var fName in new[] { "m_clouds", "m_rainClouds", "m_rainCloudsDownside" })
                    {
                        var f = envType.GetField(fName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f == null) continue;
                        var val = f.GetValue(env) as Renderer;
                        if (val == null) continue;
                        int id = val.gameObject.GetInstanceID();
                        if (!seenIDs.Add(id)) continue;
                        RecordRenderer(finds, val, $"EnvMan.{fName}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"EnvMan introspection failed: {ex.Message}");
            }

            // ── 2. Name-pattern scan across ALL transforms
            var allTransforms = Object.FindObjectsOfType<Transform>(includeInactive: false);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                var t = allTransforms[i];
                if (t == null) continue;
                int id = t.gameObject.GetInstanceID();
                if (seenIDs.Contains(id)) continue;
                string nameLower = (t.name ?? "").ToLowerInvariant();
                bool match = false;
                for (int p = 0; p < NamePatterns.Length; p++)
                {
                    if (nameLower.Contains(NamePatterns[p])) { match = true; break; }
                }
                if (!match) continue;
                seenIDs.Add(id);
                RecordTransform(finds, t, "name-scan");
            }

            // ── 3. EVERY renderer with a cloud-y / sky-y shader name
            //      (catches custom-named GOs that use a vanilla cloud shader)
            var allRenderers = Object.FindObjectsOfType<Renderer>(includeInactive: false);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                var r = allRenderers[i];
                if (r == null) continue;
                int id = r.gameObject.GetInstanceID();
                if (seenIDs.Contains(id)) continue;
                var mat = r.sharedMaterial;
                if (mat == null || mat.shader == null) continue;
                string sl = mat.shader.name.ToLowerInvariant();
                if (!sl.Contains("cloud") && !sl.Contains("sky") && !sl.Contains("fog")) continue;
                seenIDs.Add(id);
                RecordRenderer(finds, r, "shader-scan");
            }

            // Sort by bounds size descending — biggest dome/sphere first
            finds.Sort((a, b) =>
            {
                float aSize = Mathf.Max(a.boundsSize.x, a.boundsSize.y, a.boundsSize.z);
                float bSize = Mathf.Max(b.boundsSize.x, b.boundsSize.y, b.boundsSize.z);
                return bSize.CompareTo(aSize);
            });

            // ── Format report
            var report = new StringBuilder();
            report.AppendLine("==== va_sky_dump ====");
            report.AppendLine($"Player @ ({(Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero)})");
            report.AppendLine($"Found {finds.Count} sky-like object(s):\n");

            for (int i = 0; i < finds.Count; i++)
            {
                var f = finds[i];
                report.AppendLine($"  [{f.source}] '{f.path}'");
                report.AppendLine($"    pos=({f.worldPos.x:F1},{f.worldPos.y:F1},{f.worldPos.z:F1})  " +
                                  $"scale=({f.lossyScale.x:F2},{f.lossyScale.y:F2},{f.lossyScale.z:F2})");
                report.AppendLine($"    bounds.size=({f.boundsSize.x:F1},{f.boundsSize.y:F1},{f.boundsSize.z:F1})  " +
                                  $"layer={f.layer}  active={f.active}");
                report.AppendLine($"    shader='{f.shader}'");
                report.AppendLine();
            }

            // Terminal: print header + first N entries
            args.Context.AddString($"[va_sky_dump] {finds.Count} sky/cloud/dome object(s) (sorted by bounds size desc):");
            int shown = 0;
            for (int i = 0; i < finds.Count && shown < TerminalLineLimit; i++, shown++)
            {
                var f = finds[i];
                float biggestDim = Mathf.Max(f.boundsSize.x, f.boundsSize.y, f.boundsSize.z);
                string shortPath = f.path.Length > 50 ? "..." + f.path.Substring(f.path.Length - 47) : f.path;
                args.Context.AddString(
                    $"sz={biggestDim,7:F0} l={f.layer} sh='{ShortShader(f.shader)}' src={f.source} p='{shortPath}'");
            }
            if (finds.Count > TerminalLineLimit)
                args.Context.AddString($"... {finds.Count - TerminalLineLimit} more in dump file ...");

            // File: full report
            try
            {
                string cfgDir = Path.Combine(BepInEx.Paths.ConfigPath, "FiresTossinShade");
                Directory.CreateDirectory(cfgDir);
                string filePath = Path.Combine(cfgDir, DumpFileName);
                File.WriteAllText(filePath, report.ToString());
                args.Context.AddString($"Full report → {filePath}");
                Debug.Log($"[SkyDumpCommand] Wrote {finds.Count} entries to {filePath}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"Failed to write dump: {ex.Message}");
                Debug.LogWarning($"[SkyDumpCommand] write failed: {ex}");
            }
        }

        private static string ShortShader(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int slash = s.LastIndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        private static class Terminal_InitTerminal_RegisterSkyDump
        {
            private static void Postfix() => Register();
        }
    }
}
