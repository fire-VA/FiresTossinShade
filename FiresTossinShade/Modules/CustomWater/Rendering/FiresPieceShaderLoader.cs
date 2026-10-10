using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Replaces vanilla <c>Custom/Piece</c> (the Valheim building-piece shader) with our reconstruction
    /// <c>Custom/FiresPiece</c>. VALUE-PRESERVING: every piece material keeps all its own data (albedo /
    /// normal / metallic / emission textures, _Color, _Glossiness, _Metallic, triplanar + rain props) -
    /// we only change the shader, and FiresPiece declares the same property names so everything carries over.
    ///
    /// FiresPiece is a disasm-verified parity reconstruction (dry PBR + the wet/puddle model), so at the
    /// DEFAULT tuning values below it renders vanilla-exact. The Tune* knobs (all no-ops at default) let the
    /// config dial the wetness + reflection DOWN without touching any other material data.
    ///
    /// SCOPING: skips materials with <c>_TwoSidedNormals &gt; 0.5</c> or <c>_Cull == 0</c> (Cull Off) - the
    /// surface-shader form can't reproduce vanilla's VFACE normal flip, so those few double-sided pieces stay
    /// on vanilla Custom/Piece. Per-piece WearNTear build/decay tint (a MaterialPropertyBlock on _Color)
    /// survives the swap untouched. The driver re-scans each tick to catch piece materials that stream in.
    /// </summary>
    public static class FiresPieceShaderLoader
    {
        // Off by default - opt in (mirrors the other shader swaps).
        public static bool Enabled = false;

        public const string ShaderName     = "Custom/FiresPiece";
        public const string VanillaShader  = "Custom/Piece";

        private static Shader s_shader;
        private static bool s_attempted;

        private struct Orig { public Shader shader; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();

        // Live tuning pushed from config (FiresAdminTerrain). Defaults are NO-OPs => vanilla-exact pieces.
        public static float TuneWetStrength     = 1f;    // scales the whole wet amount (skyMask*_Wet). 0 = always dry.
        public static float TuneWetSmoothness   = 0.70f; // smoothness pieces reach when fully wet (vanilla = 0.70).
        public static float TuneSmoothnessScale = 1f;    // scales base (dry) smoothness -> overall reflection intensity.
        public static float TuneMetallicScale   = 1f;    // scales metallic -> reflection-probe / cubemap intensity.

        private static readonly int Prop_WetScale    = Shader.PropertyToID("_FiresWetScale");
        private static readonly int Prop_WetSmooth   = Shader.PropertyToID("_FiresWetSmoothness");
        private static readonly int Prop_SmoothScale = Shader.PropertyToID("_FiresSmoothnessScale");
        private static readonly int Prop_MetalScale  = Shader.PropertyToID("_FiresMetallicScale");
        private static readonly int Prop_TwoSided    = Shader.PropertyToID("_TwoSidedNormals");
        private static readonly int Prop_Cull        = Shader.PropertyToID("_Cull");

        // Config is the single source of truth, polled by the driver every ~1s.
        public static System.Action ConfigPoll;
        public static System.Action<bool> SetEnabledConfig;

        public static int SwappedCount => s_swapped.Count;

        /// <summary>Resolve our shader from the shared tossinshade bundle. Idempotent.</summary>
        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attempted) return null;
            s_attempted = true;
            try
            {
                s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                if (s_shader == null)
                    Debug.LogWarning($"[FiresPiece] shader '{ShaderName}' not in bundle yet - piece swap inert.");
                else
                    Debug.Log($"[FiresPiece] loaded '{ShaderName}'.");
                return s_shader;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresPiece] EnsureLoaded threw: {ex}");
                return null;
            }
        }

        // Skip double-sided pieces: the surf reconstruction omits vanilla's VFACE normal flip, so back faces
        // would shade wrong. Leave those on vanilla.
        private static bool ShouldSwap(Material mat)
        {
            if (mat.HasProperty(Prop_TwoSided) && mat.GetFloat(Prop_TwoSided) > 0.5f) return false;
            if (mat.HasProperty(Prop_Cull) && mat.GetFloat(Prop_Cull) < 0.5f) return false;   // Cull Off (0)
            return true;
        }

        /// <summary>Swap every currently-loaded vanilla piece material (minus the scoped-out ones). Dedup'd + restorable.</summary>
        public static int ApplyToAll()
        {
            if (!Enabled) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;

            PruneDead();
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || !ShaderNames.Is(mat.shader, VanillaShader)) continue;
                if (!ShouldSwap(mat)) continue;
                if (TrySwap(mat, shader)) n++;
            }
            if (n > 0) Debug.Log($"[FiresPiece] swapped {n} piece material(s).");
            return n;
        }

        private static bool TrySwap(Material mat, Shader shader)
        {
            if (s_swapped.ContainsKey(mat)) return false;
            try
            {
                s_swapped[mat] = new Orig { shader = mat.shader };
                mat.shader = shader;   // value-preserving: textures / colors / piece props carry over
                ApplyTuning(mat);      // seed the tuning knobs (no-op at defaults)
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresPiece] swap failed on '{mat.name}': {ex.Message}");
                s_swapped.Remove(mat);
                return false;
            }
        }

        /// <summary>Revert every swapped piece material to its original Custom/Piece shader.</summary>
        public static int Restore()
        {
            int r = 0;
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                try { mat.shader = kv.Value.shader; r++; }
                catch { }
            }
            s_swapped.Clear();
            if (r > 0) Debug.Log($"[FiresPiece] restored {r} piece material(s).");
            return r;
        }

        private static void ApplyTuning(Material mat)
        {
            if (mat.HasProperty(Prop_WetScale))    mat.SetFloat(Prop_WetScale, TuneWetStrength);
            if (mat.HasProperty(Prop_WetSmooth))   mat.SetFloat(Prop_WetSmooth, TuneWetSmoothness);
            if (mat.HasProperty(Prop_SmoothScale)) mat.SetFloat(Prop_SmoothScale, TuneSmoothnessScale);
            if (mat.HasProperty(Prop_MetalScale))  mat.SetFloat(Prop_MetalScale, TuneMetallicScale);
        }

        /// <summary>Re-push the current Tune* values onto every swapped piece material (config changed).</summary>
        public static void ApplyTuningToAll()
        {
            PruneDead();
            foreach (var kv in s_swapped)
            {
                var mat = kv.Key;
                if (mat == null || !ShaderNames.Is(mat.shader, ShaderName)) continue;
                ApplyTuning(mat);
            }
        }

        private static void PruneDead()
        {
            List<Material> dead = null;
            foreach (var kv in s_swapped)
                if (kv.Key == null) (dead ??= new List<Material>()).Add(kv.Key);
            if (dead != null) foreach (var m in dead) s_swapped.Remove(m);
        }

        public static string Diagnose()
        {
            int vanilla = 0, ours = 0, skipped = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || mat.shader == null) continue;
                if (mat.shader.name == VanillaShader) { vanilla++; if (!ShouldSwap(mat)) skipped++; }
                else if (mat.shader.name == ShaderName) ours++;
            }
            return $"[FiresPiece] enabled={Enabled} swapped={SwappedCount} | scan vanilla-Piece={vanilla} (double-sided skipped={skipped}) ours={ours} "
                 + $"| wet={TuneWetStrength:0.00} wetSmooth={TuneWetSmoothness:0.00} smoothScale={TuneSmoothnessScale:0.00} metalScale={TuneMetallicScale:0.00}";
        }
    }

    /// <summary>Polls the config each ~1s so the piece swap is the config's mirror (no SettingChanged reliance).</summary>
    public class FiresPieceDriver : MonoBehaviour
    {
        private const float ScanSeconds = 4f;
        private bool _cmdRegistered;
        private float _next;
        private float _nextScan;
        private void Update()
        {
            if (!_cmdRegistered)
            {
                try { PieceSwapCommand.Register(); _cmdRegistered = true; }
                catch { /* Terminal not ready yet; retry next frame */ }
            }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (ZNetScene.instance == null) return;
            FiresPieceShaderLoader.ConfigPoll?.Invoke();
            if (!FiresPieceShaderLoader.Enabled || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            FiresPieceShaderLoader.ApplyToAll();
        }
    }

    /// <summary><c>va_piece on|off|status</c> — toggle the building-piece swap live for A/B testing.</summary>
    public static class PieceSwapCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand(
                "va_piece",
                "Replace vanilla Custom/Piece (building pieces) with Custom/FiresPiece. Usage: va_piece on|off|status",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off")
                    {
                        FiresPieceShaderLoader.SetEnabledConfig?.Invoke(false); // config = source of truth
                        FiresPieceShaderLoader.Enabled = false;
                        int r = FiresPieceShaderLoader.Restore();
                        args.Context.AddString($"[FiresPiece] disabled (config Enabled=false); restored {r} piece material(s).");
                    }
                    else if (a == "status")
                    {
                        args.Context.AddString(FiresPieceShaderLoader.Diagnose());
                    }
                    else
                    {
                        FiresPieceShaderLoader.SetEnabledConfig?.Invoke(true);  // config = source of truth
                        FiresPieceShaderLoader.Enabled = true;
                        int n = FiresPieceShaderLoader.ApplyToAll();
                        args.Context.AddString($"[FiresPiece] enabled (config Enabled=true); swapped {n} piece material(s) (total {FiresPieceShaderLoader.SwappedCount}).");
                    }
                },
                isCheat: false);
            s_registered = true;
        }
    }
}
