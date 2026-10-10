using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Replaces vanilla terrain (materials bound to <c>Custom/Heightmap</c>) with our reconstructed
    /// <c>Custom/FiresHeightmap</c>, which carries the Ashlands LAVA biome paint as tunable emission.
    /// Reuses the same <c>tossinshade</c> bundle <see cref="FiresWaterShaderLoader"/> already mounts.
    ///
    /// Unlike the lava swap, this is VALUE-PRESERVING: terrain materials keep all their own data
    /// (the diffuse/normal arrays, _ClearedMaskTex paint mask, _depth ocean array, _CurlNoise) — we
    /// only change the shader, and FiresHeightmap declares the same property names so everything
    /// carries over. Terrain materials are per-zone instances (not shared), so the driver re-scans
    /// to catch zones that stream in as you move; each is swapped once and cached for restore.
    /// </summary>
    public static class FiresHeightmapShaderLoader
    {
        // Off by default — terrain is the whole world, so the user opts in (the lava is the payoff).
        public static bool Enabled = false;

        public const string ShaderName             = "Custom/FiresHeightmap";
        public const string VanillaHeightmapShader = "Custom/Heightmap";

        private static Shader s_shader;
        private static bool s_attempted;

        // Custom/FiresHeightmap is a reconstruction of the PRE-1.0 Custom/Heightmap. Valheim 1.0 added
        // snow to the terrain shader that the reconstruction therefore does not carry: EnvMan publishes
        // eight _SnowGlint* globals every frame (EnvMan.cs:195-202) and the shipped
        // Heightmap_basematerial.mat sets its own copies, and Deep North stores SNOW DEPTH in the paint
        // mask's green channel, which raises the visible ground by up to 2.1 m above the collision
        // surface (Heightmap.GetHeightOffset). Swapping our shader over a snow zone drops all of it.
        //
        // Until the shader is re-derived from the 1.0 Custom/Heightmap, snow biomes keep vanilla. The
        // swap exists to add Ashlands lava emission, so nothing it is for is lost by standing off snow.
        public const Heightmap.Biome SnowBiomes = Heightmap.Biome.DeepNorth | Heightmap.Biome.Mountain;

        private struct Orig { public Shader shader; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();
        private static readonly HashSet<int> s_fatTiles = new HashSet<int>();
        private static readonly HashSet<int> s_swappable = new HashSet<int>();
        private static readonly HashSet<int> s_held = new HashSet<int>();
        private static bool s_seasonHooked;

        // The biomes whose zones stay on vanilla Custom/Heightmap right now: always Mountain and Deep North, and in a
        // Vedr winter every biome Vedr paints snow into (Vedr, through Core's SeasonBridge, which reads 0 without it).
        // Vedr repaints those zones with the Mountain splat, which our reconstruction cannot draw as 1.0 snow.
        private static Heightmap.Biome HeldBiomes()
        {
            var held = SnowBiomes;
            if (FiresCore.Bridge.SeasonBridge.IsWinter) held |= (Heightmap.Biome)FiresCore.Bridge.SeasonBridge.SnowTerrainBiomeMask;
            return held;
        }

        // Vedr repaints nearest-first after it raises VisualsChanged, so re-run the hold at once instead of on the next poll.
        private static void HookSeason()
        {
            if (s_seasonHooked) return;
            s_seasonHooked = true;
            FiresCore.Bridge.SeasonBridge.VisualsChanged += (season, stage) =>
            {
                if (Enabled) ApplyToAllTerrain();
            };
        }

        // The identity look-knob values FiresDeepSnow used to push. Taken from its own config fallbacks so
        // the terrain renders exactly as it did before that class was retired.
        private static readonly Color LookTint = Color.white;
        private static readonly Color LookPackedTint = new Color(0.80f, 0.85f, 0.94f, 1f);
        private const float LookBright = 1f;
        private const float LookSmooth = 0.35f;
        private const float LookSmoothAmt = 0f;
        private const float LookSparkle = 0.6f;
        private const float LookPackedSmooth = 0.6f;
        private const float LookPackedAmt = 0.7f;

        private static readonly int Prop_SnowTint = Shader.PropertyToID("_FiresSnowTint");
        private static readonly int Prop_SnowBright = Shader.PropertyToID("_FiresSnowBright");
        private static readonly int Prop_SnowSmooth = Shader.PropertyToID("_FiresSnowSmooth");
        private static readonly int Prop_SnowSmoothAmt = Shader.PropertyToID("_FiresSnowSmoothAmt");
        private static readonly int Prop_SnowSparkle = Shader.PropertyToID("_FiresSnowSparkle");
        private static readonly int Prop_SnowPackedTint = Shader.PropertyToID("_FiresSnowPackedTint");
        private static readonly int Prop_SnowPackedSmooth = Shader.PropertyToID("_FiresSnowPackedSmooth");
        private static readonly int Prop_SnowPackedAmt = Shader.PropertyToID("_FiresSnowPackedAmt");

        private static readonly int Prop_LavaGlow   = Shader.PropertyToID("_LavaGlowColor");
        private static readonly int Prop_LavaEmber  = Shader.PropertyToID("_LavaEmberColor");
        private static readonly int Prop_LavaFlow   = Shader.PropertyToID("_LavaFlowSpeed");
        private static readonly int Prop_LavaLayer  = Shader.PropertyToID("_LavaArrayLayer");
        private static readonly int Prop_LavaThresh = Shader.PropertyToID("_LavaThreshold");
        private static readonly int Prop_LavaCover  = Shader.PropertyToID("_LavaCoverage");
        private static readonly int Prop_IsDistantLod = Shader.PropertyToID("_IsDistantLod");
        private static readonly int Prop_ClearedMask = Shader.PropertyToID("_ClearedMaskTex");

        // Live tuning pushed from config (FiresAdminTerrain.ApplyFiresHeightmapConfig). Defaults equal
        // the shader's baked vanilla-lava values, so a swap with no config change looks like vanilla
        // terrain plus the molten glow. ApplyTuningToAll re-applies to every swapped material.
        public static Color TuneGlowColor  = new Color(6f, 0.8f, 0f, 1f);
        public static Color TuneEmberColor = new Color(0.06f, 0.008f, 0f, 1f);
        public static float TuneFlowSpeed  = 0.01f;
        public static float TuneArrayLayer = 15f;    // molten array layer (disasm instr 480-489)
        public static float TuneThreshold  = 0.2f;   // paint-alpha low edge (vanilla; IsLava channel)
        public static float TuneCoverage   = 1f;     // scales the lava amount

        // Config is the SINGLE SOURCE OF TRUTH, polled by the driver every ~1s (no reliance on
        // ConfigEntry.SettingChanged, which the in-game editor wasn't firing - changes only applied on
        // restart). The plugin sets these:
        //   ConfigPoll       = read config (enable + tune) and apply swap/restore/tune every tick.
        //   SetEnabledConfig = write the config Enabled value, so `va_terrain on/off` stays in sync
        //                      with the polled config (otherwise the poll would immediately undo it).
        public static System.Action ConfigPoll;
        public static System.Action<bool> SetEnabledConfig;
        public static System.Action ResetConfig;   // `va_terrain reset` -> restore section-17 config to defaults

        public static int SwappedCount => s_swapped.Count;

        /// <summary>Resolve our shader from the shared bundle. Idempotent.</summary>
        public static Shader EnsureLoaded()
        {
            if (s_shader != null) return s_shader;
            if (s_attempted) return null;
            s_attempted = true;

            try
            {
                s_shader = FiresWaterShaderLoader.LoadBundleShader(ShaderName, ShaderName);
                if (s_shader == null)
                    Debug.LogWarning($"[FiresHeightmap] shader '{ShaderName}' not in bundle yet — terrain swap inert.");
                else
                    Debug.Log($"[FiresHeightmap] loaded '{ShaderName}'.");
                return s_shader;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresHeightmap] EnsureLoaded threw: {ex}");
                return null;
            }
        }

        /// <summary>Swap every currently-loaded vanilla terrain material. Dedup'd + restorable.</summary>
        public static int ApplyToAllTerrain()
        {
            if (!Enabled) return 0;
            var shader = EnsureLoaded();
            if (shader == null) return 0;

            HookSeason();
            PushLookDefaults();
            PruneDead();
            var held = HeldBiomes();
            RebuildSwappable(held);
            int released = ReleaseHeld();
            if (released > 0)
                Debug.Log($"[FiresHeightmap] {released} swapped zone(s) back on vanilla Custom/Heightmap: they now carry a held biome ({held}).");
            int n = 0, lod = 0, fat = 0, unverified = 0;
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || !ShaderNames.Is(mat.shader, VanillaHeightmapShader)) continue;
                if (IsDistantLod(mat)) { lod++; continue; }     // distant-LOD mesh: leave vanilla (z-fight fix)
                int id = mat.GetInstanceID();
                if (s_fatTiles.Contains(id)) { fat++; continue; }
                if (IsFatTile(mat)) { s_fatTiles.Add(id); fat++; continue; }   // FAT placed terrain tile: leave it to FAT
                if (!s_swappable.Contains(id)) { unverified++; continue; }     // FAIL CLOSED - see RebuildSwappable
                if (TrySwap(mat, shader)) n++;
            }
            LastSkippedLod = lod;
            LastSkippedFat = fat;
            LastSkippedUnverified = unverified;
            if (n > 0) Debug.Log($"[FiresHeightmap] swapped {n} terrain material(s) (skipped {lod} distant-LOD, {fat} FAT tile, {unverified} unverified).");
            return n;
        }

        public static int LastSkippedLod;
        public static int LastSkippedFat;
        public static int LastSkippedUnverified;

        // FAIL CLOSED. Every material we are ALLOWED to swap - not the ones we are forbidden to.
        //
        // The first version of this gate listed snow-biome materials and swapped everything else, and it
        // shipped a black Mountain in 0.3.146. The hole: a zone's material is a CLONE of a shared one -
        // Heightmap.Initialize does `m_materialInstance = new Material(this.m_material)`. That shared
        // template is a Custom/Heightmap material too, it belongs to NO heightmap, and at startup it
        // exists before any zone does. So the first pass found it, had nothing in s_heightmaps to
        // classify it against, decided it was not snow, and swapped it - and from then on every zone
        // cloned an ALREADY-SWAPPED material, so the per-zone check never ran again. A gate that guards
        // the instances cannot protect a swap that hits the source.
        //
        // Inverted, the same mistake is impossible: a material is swappable only when it IS the
        // m_materialInstance of a heightmap that has GENERATED (m_buildData non-null, so its corner
        // biomes are real) and has no snow biome in it. The shared template belongs to no heightmap, so
        // it is never swappable. Anything we cannot tie to a generated heightmap is left vanilla - which
        // is also the right answer for FAT's voxel and quilt materials, which are not zone heightmaps at
        // all. Rebuilt every pass; the driver re-scans about once a second, so a zone becomes swappable
        // on the tick after it generates and nothing is lost by waiting.
        // A generated zone that carries a held biome is recorded as held, so a zone swapped before winter set in
        // goes back to vanilla (ReleaseHeld). A zone mid-regeneration is neither: it keeps whatever it wears.
        private static void RebuildSwappable(Heightmap.Biome held)
        {
            s_swappable.Clear();
            s_held.Clear();
            var maps = Heightmap.s_heightmaps;
            if (maps == null) return;
            for (int i = 0; i < maps.Count; i++)
            {
                var hm = maps[i];
                if (hm == null) continue;
                var mat = hm.m_materialInstance;
                if (mat == null) continue;
                if (hm.m_buildData == null) continue;          // not generated: corner biomes are not real yet
                if (hm.HaveBiome(held)) { s_held.Add(mat.GetInstanceID()); continue; }
                s_swappable.Add(mat.GetInstanceID());
            }
        }

        private static int ReleaseHeld()
        {
            if (s_held.Count == 0 || s_swapped.Count == 0) return 0;
            List<Material> release = null;
            foreach (var kv in s_swapped)
                if (kv.Key != null && s_held.Contains(kv.Key.GetInstanceID())) (release ??= new List<Material>()).Add(kv.Key);
            if (release == null) return 0;
            foreach (var mat in release)
            {
                try { mat.shader = s_swapped[mat].shader; }
                catch (Exception ex) { Debug.LogWarning($"[FiresHeightmap] restore failed on '{mat.name}': {ex.Message}"); }
                s_swapped.Remove(mat);
            }
            return release.Count;
        }

        // Custom/FiresHeightmap reads these as GLOBALS and they have no sane zero. They used to be pushed
        // every frame by FiresDeepSnow, whose own comment warned they "must always be valid or unset
        // globals (0) would blacken snow" - it pushed them even when deep snow was off, for exactly this
        // reason. Retiring that class in 0.3.146 left nothing pushing them, they defaulted to 0, and snow
        // terrain rendered black. Identity values live here now, next to the swap that needs them, so the
        // shader can never be applied without them. The displacement globals are NOT restored: those are
        // correct at 0, which means no shell and no trample field.
        private static void PushLookDefaults()
        {
            Shader.SetGlobalColor(Prop_SnowTint, LookTint);
            Shader.SetGlobalFloat(Prop_SnowBright, LookBright);
            Shader.SetGlobalFloat(Prop_SnowSmooth, LookSmooth);
            Shader.SetGlobalFloat(Prop_SnowSmoothAmt, LookSmoothAmt);
            Shader.SetGlobalFloat(Prop_SnowSparkle, LookSparkle);
            Shader.SetGlobalColor(Prop_SnowPackedTint, LookPackedTint);
            Shader.SetGlobalFloat(Prop_SnowPackedSmooth, LookPackedSmooth);
            Shader.SetGlobalFloat(Prop_SnowPackedAmt, LookPackedAmt);
        }

        // FAT's placed terrain tiles use a CLONE of the captured terrain material named
        // "FiresAdminTerrain_TileMaterial" (FiresAdminTerrain TerrainMaterialCache) with a BLANK paint
        // mask (no lava channel). They are built structures, not the biome ground - reskinning them with
        // our biome shader is wrong and only confuses the diagnostics. Leave them to FAT.
        private static bool IsFatTile(Material mat)
        {
            return mat.name.IndexOf("FiresAdminTerrain", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The distant-LOD terrain is a SEPARATE coarse mesh with the OPPOSITE distance behavior: the
        // vanilla LOD shader variant has NO near-discard (it draws everywhere and sits UNDER the near
        // mesh, filling the far horizon). Its material carries _IsDistantLod=1 / the _ISDISTANTLOD_ON
        // keyword. If we swap it to our near-terrain shader, our near-discard makes it render up close
        // instead of far -> it z-fights the near mesh = the "doubled terrain". So skip it; only the
        // near, full-detail heightmap the player walks on gets our shader.
        private static bool IsDistantLod(Material mat)
        {
            if (mat.HasProperty(Prop_IsDistantLod) && mat.GetFloat(Prop_IsDistantLod) >= 0.5f) return true;
            if (mat.IsKeywordEnabled("_ISDISTANTLOD_ON")) return true;
            return false;
        }

        private static bool TrySwap(Material mat, Shader shader)
        {
            if (s_swapped.ContainsKey(mat)) return false;
            try
            {
                s_swapped[mat] = new Orig { shader = mat.shader };
                mat.shader = shader;       // value-preserving: arrays / paint mask / _depth carry over
                ApplyTuning(mat);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresHeightmap] swap failed on '{mat.name}': {ex.Message}");
                s_swapped.Remove(mat);
                return false;
            }
        }

        /// <summary>Revert every swapped terrain material to its original Custom/Heightmap shader.</summary>
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
            if (r > 0) Debug.Log($"[FiresHeightmap] restored {r} terrain material(s).");
            return r;
        }

        /// <summary>Re-push the current Tune* values onto every already-swapped material (config changed).</summary>
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

        private static void ApplyTuning(Material mat)
        {
            if (mat.HasProperty(Prop_LavaGlow))  mat.SetColor(Prop_LavaGlow, TuneGlowColor);
            if (mat.HasProperty(Prop_LavaEmber)) mat.SetColor(Prop_LavaEmber, TuneEmberColor);
            if (mat.HasProperty(Prop_LavaFlow))   mat.SetFloat(Prop_LavaFlow, TuneFlowSpeed);
            if (mat.HasProperty(Prop_LavaLayer))  mat.SetFloat(Prop_LavaLayer, TuneArrayLayer);
            if (mat.HasProperty(Prop_LavaThresh)) mat.SetFloat(Prop_LavaThresh, TuneThreshold);
            if (mat.HasProperty(Prop_LavaCover))  mat.SetFloat(Prop_LavaCover, TuneCoverage);
        }

        // Drop materials whose zone unloaded (Unity-destroyed) so the dictionary stays bounded as the
        // player travels through many terrain zones.
        private static void PruneDead()
        {
            List<Material> dead = null;
            foreach (var kv in s_swapped)
                if (kv.Key == null) (dead ??= new List<Material>()).Add(kv.Key);
            if (dead != null)
                foreach (var m in dead) s_swapped.Remove(m);
        }

        /// <summary>Diagnostic for `va_terrain status`: swap count + live tune values + a sample swapped
        /// material's bound diffuse-array depth, paint mask, and the layer it's actually using - so we
        /// can see if the molten layer index is in range and whether the array/paint carried over.</summary>
        public static string Diagnose()
        {
            var sb = new System.Text.StringBuilder();
            // Scan every terrain material (ours + vanilla) and bucket by paint-mask kind: a "Nothing"/null
            // mask carries NO lava channel (e.g. FAT's FiresAdminTerrain_PaintMaskNothing); a "real" mask
            // is a per-zone m_paintMask that holds the lava alpha. If real==0, the lava DATA is gone.
            int oursN = 0, vanN = 0, lodN = 0, fatN = 0, realPM = 0, nothingPM = 0;
            var sample = new System.Collections.Generic.List<string>();
            var all = Resources.FindObjectsOfTypeAll<Material>();
            foreach (var m in all)
            {
                if (m == null || m.shader == null) continue;
                bool ours = m.shader.name == ShaderName;
                bool van  = m.shader.name == VanillaHeightmapShader;
                if (!ours && !van) continue;
                var pm = m.HasProperty(Prop_ClearedMask) ? m.GetTexture(Prop_ClearedMask) : null;
                string pmName = pm != null ? pm.name : "null";
                if (pm == null || pmName.IndexOf("Nothing", StringComparison.OrdinalIgnoreCase) >= 0) nothingPM++; else realPM++;
                if (ours) oursN++; else if (IsDistantLod(m)) lodN++; else if (IsFatTile(m)) fatN++; else vanN++;
                if (sample.Count < 8) sample.Add((ours ? "OUR" : IsFatTile(m) ? "FAT" : "VAN") + ":" + m.name + "|" + pmName);
            }
            var held = HeldBiomes();
            RebuildSwappable(held);
            int liveMaps = Heightmap.s_heightmaps != null ? Heightmap.s_heightmaps.Count : 0;
            sb.Append($"[FiresHeightmap] enabled={Enabled} swapped={SwappedCount} | scan ours={oursN} vanilla-near={vanN} fatTiles={fatN} distLOD={lodN} | gate FAIL-CLOSED: {s_swappable.Count} of {liveMaps} live heightmaps swappable, {s_held.Count} held (generated and not {held}; Vedr winter={FiresCore.Bridge.SeasonBridge.IsWinter}); everything else incl. the shared template stays vanilla | lastSkippedUnverified={LastSkippedUnverified} | paintMasks real={realPM} nothing={nothingPM} | tune layer={TuneArrayLayer} thresh={TuneThreshold} cover={TuneCoverage}");

            // Decisive: ask the GAME whether there is lava under the player (the data IsLava/damage read).
            try
            {
                var player = Player.m_localPlayer;
                if (player != null)
                {
                    Vector3 pos = player.transform.position;
                    var f = typeof(Heightmap).GetField("s_heightmaps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var list = f != null ? f.GetValue(null) as System.Collections.IList : null;
                    Heightmap best = null; float bestD = 99999f;
                    if (list != null)
                        foreach (Heightmap hm in list)
                        {
                            if (hm == null) continue;
                            var hp = hm.transform.position;
                            float d = new Vector2(pos.x - hp.x, pos.z - hp.z).magnitude;
                            if (d < bestD) { bestD = d; best = hm; }
                        }
                    if (best != null)
                        sb.Append($" | UNDER-PLAYER hmap@{bestD:F0}m IsLava={best.IsLava(pos)} vegMask={best.GetVegetationMask(pos):F2} biome={best.GetBiome(pos)}");
                    else
                        sb.Append(" | UNDER-PLAYER: no heightmap found");
                }
            }
            catch (Exception ex) { sb.Append(" | under-player-err:" + ex.Message); }

            foreach (var s in sample) sb.Append(" | " + s);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Drives the terrain swap: registers <c>va_terrain</c> once the Terminal exists, then re-scans
    /// for terrain materials on a cadence so zones that stream in as you move are caught (each is
    /// swapped once, so re-scans are cheap no-ops after the first pass over a given zone).
    /// </summary>
    public class FiresHeightmapDriver : MonoBehaviour
    {
        private bool _cmdRegistered;
        private float _next;

        private void Update()
        {
            if (!_cmdRegistered)
            {
                try { HeightmapSwapCommand.Register(); _cmdRegistered = true; }
                catch { /* Terminal type not ready yet; retry next frame */ }
            }

            // Poll the config every ~1s; the plugin's ConfigPoll reads enable + tune from the config and
            // applies swap/restore/tune. Config-driven (not event-driven) so slider + toggle changes apply
            // live regardless of whether the config editor fires SettingChanged.
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (ZNetScene.instance == null) return;
            FiresHeightmapShaderLoader.ConfigPoll?.Invoke();
        }
    }

    /// <summary><c>va_terrain on|off|status</c> — toggle the terrain swap live for A/B testing.</summary>
    public static class HeightmapSwapCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand(
                "va_terrain",
                "Replace vanilla terrain with Custom/FiresHeightmap (Ashlands lava paint). Usage: va_terrain on|off|status|reset",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "on";
                    if (a == "off")
                    {
                        FiresHeightmapShaderLoader.SetEnabledConfig?.Invoke(false);  // config is source of truth
                        FiresHeightmapShaderLoader.Enabled = false;
                        int r = FiresHeightmapShaderLoader.Restore();
                        args.Context.AddString($"[FiresHeightmap] disabled (config Enabled=false); restored {r} material(s).");
                    }
                    else if (a == "reset")
                    {
                        FiresHeightmapShaderLoader.ResetConfig?.Invoke();
                        args.Context.AddString("[FiresHeightmap] lava config reset to defaults (layer 15, threshold 0.2, coverage 1, brightness 6). " + FiresHeightmapShaderLoader.Diagnose());
                    }
                    else if (a == "status")
                    {
                        args.Context.AddString(FiresHeightmapShaderLoader.Diagnose());
                    }
                    else
                    {
                        FiresHeightmapShaderLoader.SetEnabledConfig?.Invoke(true);   // config is source of truth
                        FiresHeightmapShaderLoader.Enabled = true;
                        int n = FiresHeightmapShaderLoader.ApplyToAllTerrain();
                        args.Context.AddString($"[FiresHeightmap] enabled (config Enabled=true); swapped {n} terrain material(s) (total {FiresHeightmapShaderLoader.SwappedCount}).");
                    }
                },
                isCheat: false);
            s_registered = true;
        }
    }
}
