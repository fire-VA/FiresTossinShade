using System.Collections.Generic;
using System.Linq;
using FiresCore.Config;
using HarmonyLib;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// `fires_preset` — list, save, load and delete the shader presets under
    /// BepInEx/config/FiresRPGmaker/Presets/FiresTossinShade/. Presets are plain BepInEx .cfg files, so
    /// sharing one is copying one file into that folder.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class FiresPresetCommand
    {
        private const string ModName = "FiresTossinShade";

        private static bool s_registered;

        private static void Postfix()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand("fires_preset",
                "Shader presets: fires_preset list | load <name> | save <name> | delete <name> | folder",
                args => Run(args),
                optionsFetcher: () => FiresConfigPresets.ListPresetNames(ModName).ToList());

            // 0.3.158: the section-renumber migration's self-test: a temporary cfg with values under the OLD section names, the same keys
            // bound under the NEW names with other defaults, the migration run; PASS when every key took the old value
            new Terminal.ConsoleCommand("fires_config_migrate_test", "FiresTossinShade: self-test of the 0.3.158 section-renumber migration (writes a temp cfg in BepInEx/cache).",
                args => { foreach (var line in MigrateSelfTest()) args.Context?.AddString(line); });
        }

        internal static System.Collections.Generic.List<string> MigrateSelfTest()
        {
            var lines = new System.Collections.Generic.List<string>();
            string path = System.IO.Path.Combine(BepInEx.Paths.CachePath, "fts_migrate_selftest.cfg");
            try
            {
                System.IO.File.WriteAllText(path, "[10 - FiresSky]\n\nT1 Float = 3.5\n\n[16 - FiresLava]\n\nT2 Bool = false\n\n[10 - FiresPlayer]\n\nT3 Int = 7\n");
                var cfg = new BepInEx.Configuration.ConfigFile(path, true);
                var a = cfg.Bind("07 - FiresSky", "T1 Float", 1f, "test");
                var b = cfg.Bind("21 - FiresLava", "T2 Bool", true, "test");
                var c = cfg.Bind("10 - FiresPlayer", "T3 Int", 0, "test");   // unchanged section: must keep 7 by itself
                int n = VerdantsAscent.Managers.ConfigManager.MigrateRenamedSections(cfg, false);
                bool ok = n == 2 && System.Math.Abs(a.Value - 3.5f) < 0.001f && b.Value == false && c.Value == 7;
                lines.Add($"[fires_config_migrate_test] {(ok ? "PASS" : "FAIL")}: migrated {n} (need 2); 07 - FiresSky T1 = {a.Value} (need 3.5), 21 - FiresLava T2 = {b.Value} (need False), 10 - FiresPlayer T3 = {c.Value} (need 7)");
            }
            catch (System.Exception e) { lines.Add($"[fires_config_migrate_test] FAIL: threw {e.GetType().Name}: {e.Message}"); }
            finally { try { System.IO.File.Delete(path); } catch { } }
            foreach (var l in lines) UnityEngine.Debug.Log(l);
            return lines;
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            // 0.3.158 (Fire 10-08): presets rewrite the config, admin only
            if (ZNet.instance != null && !ZNet.instance.LocalPlayerIsAdminOrHost()) { args.Context?.AddString("[fires_preset] admin only"); return; }
            var config = VerdantsAscent.FiresTossinShade.Instance?.Config;
            if (config == null)
            {
                args.Context.AddString("fires_preset: config not ready yet.");
                return;
            }

            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            string name = args.Length > 2 ? string.Join(" ", args.Args.Skip(2)) : null;

            switch (verb)
            {
                case "list":
                    ListPresets(args);
                    break;

                case "folder":
                    args.Context.AddString(FiresConfigPresets.PresetFolder(ModName));
                    break;

                case "load":
                case "apply":
                    if (RequireName(args, name, verb))
                        args.Context.AddString(
                            $"Applied '{name}': {FiresConfigPresets.ApplyPreset(ModName, config, name)} setting(s) changed.");
                    break;

                case "save":
                    if (RequireName(args, name, verb))
                        args.Context.AddString(FiresConfigPresets.SaveCurrentValuesAsPreset(ModName, config, name)
                            ? $"Saved preset '{name}'."
                            : $"Could not save preset '{name}' (see log).");
                    break;

                case "delete":
                    if (RequireName(args, name, verb))
                        args.Context.AddString(FiresConfigPresets.DeletePreset(ModName, name)
                            ? $"Deleted preset '{name}'."
                            : $"No preset named '{name}'.");
                    break;

                default:
                    args.Context.AddString("fires_preset: list | load <name> | save <name> | delete <name> | folder");
                    break;
            }
        }

        private static void ListPresets(Terminal.ConsoleEventArgs args)
        {
            var presets = FiresConfigPresets.ListPresetNames(ModName);
            if (presets.Count == 0)
            {
                args.Context.AddString($"No presets yet. Save one with 'fires_preset save <name>'.");
                args.Context.AddString(FiresConfigPresets.PresetFolder(ModName));
                return;
            }

            args.Context.AddString($"{presets.Count} preset(s):");
            foreach (string preset in presets) args.Context.AddString($"  {preset}");
        }

        private static bool RequireName(Terminal.ConsoleEventArgs args, string name, string verb)
        {
            if (!string.IsNullOrWhiteSpace(name)) return true;
            args.Context.AddString($"fires_preset {verb}: needs a preset name.");
            return false;
        }
    }
}
