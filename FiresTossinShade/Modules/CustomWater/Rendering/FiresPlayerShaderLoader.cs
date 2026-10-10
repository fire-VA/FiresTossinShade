using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Swaps character bodies from vanilla <c>Custom/Player</c> to our reconstructed
    /// <c>Custom/FiresPlayer</c> (same property names, so the swap is value-preserving) and adds
    /// the eye-color feature. Reuses the <c>tossinshade</c> bundle mounted by FiresWaterShaderLoader.
    ///
    /// At default (<see cref="TuneEyeColor"/> = white) the result is identical to vanilla; the eye
    /// tint only kicks in once a color is set. NOTE v1: the swap hits EVERY Custom/Player material
    /// (local player, other players, humanoid NPCs), so the eye color is currently global.
    /// </summary>
    public static class FiresPlayerShaderLoader
    {
        public static bool Enabled = true;

        public const string ShaderName    = "Custom/FiresPlayer";
        public const string VanillaShader = "Custom/Player";

        private static Shader s_shader;
        private static bool s_attempted;

        private struct Orig { public Shader shader; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();

        private static readonly int Prop_EyeColor     = Shader.PropertyToID("_EyeColor");
        private static readonly int Prop_EyeThreshold = Shader.PropertyToID("_EyeThreshold");

        // Live tuning from config (FiresAdminTerrain.ApplyFiresPlayerConfig). Eye color is applied to
        // the LOCAL player only (per-renderer MPB); TuneEyeColor.a = 0 means no eye change.
        public static Color TuneEyeColor     = new Color(1f, 1f, 1f, 0f);
        public static float TuneEyeThreshold = 0.05f;

        public static int SwappedCount => s_swapped.Count;

        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attempted) return null;
            s_attempted = true;
            try
            {
                s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                if (s_shader == null) Debug.LogWarning($"[FiresPlayer] shader '{ShaderName}' not in bundle yet.");
                else FiresLogger.LogVerbose($"[FiresPlayer] loaded '{ShaderName}'.");
                return s_shader;
            }
            catch (Exception ex) { Debug.LogError($"[FiresPlayer] EnsureLoaded threw: {ex}"); return null; }
        }

        public static int ApplyToAll()
        {
            if (!Enabled) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || !ShaderNames.Is(mat.shader, VanillaShader)) continue;
                if (TrySwap(mat, shader)) n++;
            }
            if (n > 0) FiresLogger.LogVerbose($"[FiresPlayer] swapped {n} player material(s) to '{ShaderName}'.");
            return n;
        }

        private static bool TrySwap(Material mat, Shader shader)
        {
            if (s_swapped.ContainsKey(mat)) return false;
            try
            {
                s_swapped[mat] = new Orig { shader = mat.shader };
                mat.shader = shader;          // value-preserving: same property names carry across
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresPlayer] swap failed on '{mat.name}': {ex.Message}");
                s_swapped.Remove(mat);
                return false;
            }
        }

        private static MaterialPropertyBlock s_block;

        // On the character-select / barber screen there is no Player.m_localPlayer - the editable
        // character is FejdStartup's preview player. Resolve whichever exists so the player-only
        // features (eye color, hair/beard color) work both in-world AND in those menus.
        public static Player ResolveLocalOrPreview()
        {
            var p = Player.m_localPlayer;
            if (p != null) return p;
            var fej = FejdStartup.instance;
            return fej != null ? fej.GetPreviewPlayer() : null;
        }

        // Eye color is applied to YOUR character ONLY (local or preview), via a per-renderer
        // MaterialPropertyBlock. The shared Custom/FiresPlayer material keeps _EyeColor.a = 0, so
        // companions/NPCs using the same material stay vanilla. Re-applied periodically so it survives
        // VisEquipment model rebuilds. Skips the elf-ear renderers (they opt out of eye tinting).
        public static void ApplyTuningToAll()
        {
            var p = ResolveLocalOrPreview();
            if (p == null) return;
            if (s_block == null) s_block = new MaterialPropertyBlock();
            p.GetComponentsInChildren(true, s_renderers);
            for (int i = 0; i < s_renderers.Count; i++)
            {
                var r = s_renderers[i];
                if (r == null || IsElfEar(r)) continue;
                var sm = r.sharedMaterial;
                if (sm == null || !ShaderNames.Is(sm.shader, ShaderName)) continue;
                r.GetPropertyBlock(s_block);
                s_block.SetColor(Prop_EyeColor, TuneEyeColor);
                s_block.SetFloat(Prop_EyeThreshold, TuneEyeThreshold);
                r.SetPropertyBlock(s_block);
            }
            s_renderers.Clear();
        }

        private static readonly List<Renderer> s_renderers = new List<Renderer>();
        private static readonly Dictionary<int, bool> s_elfEars = new Dictionary<int, bool>();

        private static bool IsElfEar(Renderer r)
        {
            int id = r.GetInstanceID();
            if (!s_elfEars.TryGetValue(id, out bool ear))
            {
                ear = r.name != null && r.name.StartsWith("FiresElfEar");
                s_elfEars[id] = ear;
            }
            return ear;
        }

        public static int Restore()
        {
            int r = 0;
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                try { mat.shader = kv.Value.shader; r++; } catch { }
            }
            s_swapped.Clear();
            if (r > 0) Debug.Log($"[FiresPlayer] restored {r} player material(s).");
            return r;
        }
    }

    /// <summary>Registers <c>va_player</c> and re-scans for player materials as they stream in.</summary>
    public class FiresPlayerDriver : MonoBehaviour
    {
        private bool _cmdRegistered;
        private bool _found;
        private float _nextSwap;
        private float _nextEyes;

        private void Update()
        {
            if (!_cmdRegistered)
            {
                try { PlayerSwapCommand.Register(); _cmdRegistered = true; }
                catch { /* Terminal not ready yet */ }
            }

            if (!FiresPlayerShaderLoader.Enabled) return;
            // Run in-world OR on the character-select/barber screen (preview player present).
            if (ZNetScene.instance == null && FiresPlayerShaderLoader.ResolveLocalOrPreview() == null) return;

            if (Time.unscaledTime >= _nextSwap)
            {
                _nextSwap = Time.unscaledTime + (_found ? 60f : 5f);
                if (FiresPlayerShaderLoader.ApplyToAll() > 0 || FiresPlayerShaderLoader.SwappedCount > 0)
                    _found = true;
            }
            if (Time.unscaledTime >= _nextEyes)
            {
                _nextEyes = Time.unscaledTime + 1f;      // re-apply local-player eye MPB (survives model rebuilds)
                FiresPlayerShaderLoader.ApplyTuningToAll();
            }
        }
    }

    /// <summary><c>va_player on|off|status</c> — toggle the player shader swap live.</summary>
    public static class PlayerSwapCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand(
                "va_player",
                "Swap characters to Custom/FiresPlayer (adds eye color). Usage: va_player on|off|status",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off")
                    {
                        FiresPlayerShaderLoader.Enabled = false;
                        int r = FiresPlayerShaderLoader.Restore();
                        args.Context.AddString($"[FiresPlayer] disabled; restored {r} material(s).");
                    }
                    else if (a == "status")
                    {
                        args.Context.AddString($"[FiresPlayer] enabled={FiresPlayerShaderLoader.Enabled}, swapped={FiresPlayerShaderLoader.SwappedCount}.");
                    }
                    else
                    {
                        FiresPlayerShaderLoader.Enabled = true;
                        int n = FiresPlayerShaderLoader.ApplyToAll();
                        args.Context.AddString($"[FiresPlayer] enabled; swapped {n} new (total {FiresPlayerShaderLoader.SwappedCount}).");
                    }
                },
                isCheat: false);
            s_registered = true;
        }
    }
}
