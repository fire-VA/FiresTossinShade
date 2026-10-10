using HarmonyLib;
using System;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Makes vanilla lightning composite cleanly with the custom sky/clouds/fog. The
    /// bolt is an ALPHA-BLENDED particle billboard (lightning_sparcs / Particles/
    /// Standard Unlit2): its rectangular quad shows a faint border and OVERDRAWS the
    /// dome clouds + volumetric fog where it draws (the "border + cleared the fog"
    /// bug). Switching the spawned bolt to ADDITIVE blend makes the bright bolt GLOW
    /// over the environment instead — the dark/low-alpha quad corners add nothing (no
    /// border) and the clouds/fog behind show through. Applied to the instanced
    /// material on spawn, so the shared vanilla material is left untouched.
    ///
    /// 2026-06-26: the effect ALSO carries a non-particle flash card (LightningFlipbook =
    /// a MeshRenderer QUAD with m_CastShadows=1 on a default opaque material). The original
    /// patch only touched ParticleSystemRenderers, so that card kept writing into
    /// _CameraDepthTexture via its SHADOWCASTER pass -> the FiresVolFog post-pass read a near
    /// depth there and left a clean diamond-shaped clear-hole in the fog (the "2D lightning in
    /// the fog" bug). The real lever is shadowCastingMode = Off (removes it from the depth
    /// texture); the material's _ZWrite property does NOT control the shadowcaster depth, which
    /// is why ZWrite-off alone didn't fix it. 2026-06-27: generalised — the shadowCastingMode
    /// fix now runs on EVERY spawned FX (not just "Lightning"-named ones), since ANY transient
    /// effect that casts shadows carves the fog the same way; only the additive bolt-glow tweak
    /// stays scoped to lightning so other effects keep their own blend.
    /// </summary>
    [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
    public static class FiresLightningBlend
    {
        public static bool Enabled = true;

        // The flash CARD (Valheim shader "Unlit/Lighting" on the Flash(Clone) particle +
        // "glow") is OPAQUE and writes the forward depth buffer, so it OCCLUDES every
        // transparent env effect (mist, FiresLitParticles clouds, volumetric fog) behind it
        // — a hard rectangular clear-cutout in the storm sky. It also IS the visible flash
        // (hiding it removed the strike entirely). Vanilla gets away with it because vanilla
        // has no volumetric mist between camera and card. FIX = render the card ADDITIVELY
        // (ZWrite off, transparent queue): dark/empty texels add nothing (no opaque rectangle,
        // no mist occlusion) and the bright bolt glows through — how vanilla's flash reads.
        // "Unlit/Lighting" has no blend props to flip, so we swap it to the additive particle
        // shader the bolt sparks already use, preserving the texture + tint.
        public static bool FixFlashCard = true;
        private static Shader s_additiveShader;
        private static Shader AdditiveShader()
        {
            // Fake-null aware: a cached instance whose bundle unloaded compares == null and is
            // re-resolved; isSupported rejects a broken/stripped copy instead of caching it.
            if (s_additiveShader != null && s_additiveShader.isSupported) return s_additiveShader;
            s_additiveShader = null;

            // OUR shader first — Custom/FiresAdditive ships in the tossinshade bundle, so its
            // lifetime and variants are ours. The old scavenge below grabbed ANY loaded instance
            // named "Custom/Particle (Unlit)" via FindObjectsOfTypeAll — enumeration order is
            // arbitrary, so it could cache a same-named broken copy from another mod's bundle:
            // the MAGENTA flash card. Kept only as a fallback for a stale bundle.
            s_additiveShader = FiresWaterShaderLoader.LoadBundleShader("Custom/FiresAdditive", "Custom/FiresAdditive");
            if (s_additiveShader != null && s_additiveShader.isSupported) return s_additiveShader;
            s_additiveShader = null;

            var all = Resources.FindObjectsOfTypeAll<Shader>();
            Shader fallback = null;
            foreach (var s in all)
            {
                if (s == null || string.IsNullOrEmpty(s.name) || !s.isSupported) continue;
                if (s.name.IndexOf("Particle (Unlit)", StringComparison.OrdinalIgnoreCase) >= 0)
                { s_additiveShader = s; return s_additiveShader; }
                if (fallback == null && s.name.IndexOf("Particles/Additive", StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = s;
            }
            s_additiveShader = fallback ?? Shader.Find("Sprites/Default");   // last resort: at least ZWrite-off so no occlusion
            return s_additiveShader;
        }

        // Undo ledger — every swapped material instance + its original shader/queue, so flipping
        // the toggle OFF genuinely restores live materials (the persistent storm flash card is
        // swapped IN PLACE and otherwise keeps our shader forever). Entries whose material died
        // with its transient FX compare fake-null and are skipped/pruned.
        private static readonly System.Collections.Generic.List<SwapRecord> s_swapped
            = new System.Collections.Generic.List<SwapRecord>();
        private struct SwapRecord { public Material Mat; public Shader Orig; public int Queue; }

        public static void RestoreAllSwapped()
        {
            for (int i = s_swapped.Count - 1; i >= 0; i--)
            {
                var rec = s_swapped[i];
                if (rec.Mat != null && rec.Orig != null)
                {
                    rec.Mat.shader = rec.Orig;
                    rec.Mat.renderQueue = rec.Queue;
                }
            }
            s_swapped.Clear();
        }

        private static readonly int P_SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int P_DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int P_ZWrite   = Shader.PropertyToID("_ZWrite");

        private static void Postfix(GameObject[] __result)
        {
            if (!Enabled || __result == null) return;
            try
            {
                foreach (var go in __result)
                {
                    if (go == null) continue;
                    Sanitize(go);
                }
            }
            catch { /* never break effect spawning over a cosmetic tweak */ }
        }

        // Shared FX sanitizer, applied to transient EffectList spawns AND (below) the
        // env's persistent weather psystems: kill the shadowcaster (it feeds
        // _CameraDepthTexture and carves a clean quad-shaped hole in every depth-driven
        // fog pass — the "cleared the fog around the bolt" bug), and switch lightning-
        // named renderers to additive so the bolt glows with no quad border.
        internal static void Sanitize(GameObject go)
        {
            bool isLightning = go.name.IndexOf("Lightning", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFlashFx = isLightning
                || go.name.IndexOf("Flash", StringComparison.OrdinalIgnoreCase) >= 0
                || go.name.IndexOf("Thunder", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // Swap the depth-writing flash CARD ("Unlit/Lighting") to an ADDITIVE particle
                // shader so it stops occluding the mist/fog behind it (the hard sky-cutout)
                // while STILL showing the bolt. Its own shader has no blend props to flip.
                if (FixFlashCard && isFlashFx)
                {
                    var sm = r.sharedMaterial;
                    // Swap the opaque depth-writing card — and also REPAIR a card whose previously
                    // assigned shader died or can't render (unloaded/broken donor = magenta card).
                    bool isOpaqueCard = sm != null && sm.shader != null
                        && sm.shader.name.IndexOf("Unlit/Lighting", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDeadCard = sm != null && (sm.shader == null || !sm.shader.isSupported);
                    if (isOpaqueCard || isDeadCard)
                    {
                        var add = AdditiveShader();
                        if (add != null)
                        {
                            foreach (var m in r.materials)   // instanced copies — shared vanilla material untouched
                            {
                                if (m == null) continue;
                                Texture tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : m.mainTexture;
                                Color tint = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                                s_swapped.Add(new SwapRecord { Mat = m, Orig = m.shader, Queue = m.renderQueue });
                                m.shader = add;
                                if (tex != null) { if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex); else m.mainTexture = tex; }
                                if (m.HasProperty("_Color"))     m.SetColor("_Color", tint);
                                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", tint);
                                if (m.HasProperty(P_ZWrite))   m.SetFloat(P_ZWrite, 0f);
                                if (m.HasProperty(P_DstBlend)) { m.SetFloat(P_SrcBlend, (float)UnityEngine.Rendering.BlendMode.SrcAlpha); m.SetFloat(P_DstBlend, (float)UnityEngine.Rendering.BlendMode.One); }
                                m.renderQueue = 3100;   // transparent, after the mist/fog
                            }
                            if (FiresSkyDiagCommand.ArmFlashCapture && FiresSkyDiagCommand.DiagPath != null)
                            {
                                try
                                {
                                    System.IO.File.AppendAllText(FiresSkyDiagCommand.DiagPath,
                                        $"  ADDITIVE-swapped flash card '{r.gameObject.name}' -> '{add.name}' under '{go.name}'\n");
                                }
                                catch { }
                            }
                            continue;
                        }
                    }
                }

                bool lightningHere = isLightning
                    || r.gameObject.name.IndexOf("Lightning", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!lightningHere) continue;   // the additive BOLT glow is lightning-specific (don't recolour other FX)
                foreach (var m in r.materials)   // instanced copies — shared vanilla materials stay untouched
                {
                    if (m == null) continue;
                    if (m.HasProperty(P_DstBlend))
                    {
                        m.SetFloat(P_SrcBlend, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        m.SetFloat(P_DstBlend, (float)UnityEngine.Rendering.BlendMode.One);   // additive: glow, no border, fog shows through
                    }
                    if (m.HasProperty(P_ZWrite)) m.SetFloat(P_ZWrite, 0f);
                }
            }
        }
    }

    /// <summary>
    /// The missing half of the fix: the STORM's ambient lightning (the flash card that
    /// carved fog holes in the field reports) is NOT an EffectList spawn — it lives in
    /// the environment's persistent psystems, toggled by EnvMan.SetParticleArrayEnabled
    /// when the weather changes. Sweep those with the same sanitizer every time an env's
    /// systems are ENABLED (idempotent, a handful of renderers, only on weather change).
    /// </summary>
    [HarmonyPatch(typeof(EnvMan), "SetParticleArrayEnabled")]
    public static class FiresEnvPsystemBlend
    {
        private static void Postfix(GameObject[] psystems, bool enabled)
        {
            if (!FiresLightningBlend.Enabled || !enabled || psystems == null) return;
            try
            {
                foreach (var ps in psystems)
                {
                    if (ps == null) continue;
                    FiresLightningBlend.Sanitize(ps);
                }
            }
            catch { /* cosmetic only — never break weather transitions */ }
        }
    }
}
