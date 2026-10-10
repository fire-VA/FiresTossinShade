using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Drives the LOCAL player's hair/beard STYLE and COLOR from config, mirroring the vanilla barber
    /// (PlayerCustomizaton). Styles are the same ObjectDB customization items the barber lists, applied
    /// via Player.SetHair / SetBeard (persisted to the character). Hair color uses vanilla SetHairColor.
    ///
    /// Vanilla colors the beard with the HAIR color (there is no separate beard color), so an optional
    /// beard color is applied as a LOCAL per-renderer material tint on the beard instance, re-asserted
    /// each tick (it survives vanilla's per-frame UpdateColors and beard rebuilds).
    ///
    /// Style index -1 = "leave the character as-is"; once set to a real index the config is authoritative
    /// (re-asserted whenever the model rebuilds). Run `va_styles` to print the index -> name mapping.
    /// </summary>
    public static class FiresPlayerCustomization
    {
        public static int   HairStyle  = -1;     // -1 = don't touch
        public static int   BeardStyle = -1;

        public static bool  HairColorEnabled  = false;
        public static Color HairColor  = Color.white;
        public static bool  BeardColorEnabled = false;
        public static Color BeardColor = Color.white;

        private static List<ItemDrop> s_hairs, s_beards;
        private static MaterialPropertyBlock s_block;
        private static bool s_beardApplied;
        private static string s_lastHairName, s_lastBeardName;
        private static readonly int Prop_SkinColor = Shader.PropertyToID("_SkinColor");

        // Enumerate exactly like PlayerCustomizaton.LoadHair: Customization items, drop "_" variants,
        // sort by localized name. Order is deterministic so config indices stay stable across sessions.
        private static bool EnsureLists()
        {
            if (s_hairs != null && s_beards != null) return true;
            var odb = ObjectDB.instance;
            if (odb == null) return false;
            var beards = odb.GetAllItems(ItemDrop.ItemData.ItemType.Customization, "Beard");
            var hairs  = odb.GetAllItems(ItemDrop.ItemData.ItemType.Customization, "Hair");
            if (beards == null || hairs == null) return false;
            beards.RemoveAll(x => x == null || x.name.Contains("_"));
            hairs.RemoveAll(x => x == null || x.name.Contains("_"));
            beards.Sort((x, y) => string.Compare(Loc(x), Loc(y), StringComparison.Ordinal));
            hairs.Sort((x, y) => string.Compare(Loc(x), Loc(y), StringComparison.Ordinal));
            s_beards = beards;
            s_hairs  = hairs;
            return true;
        }

        private static string Loc(ItemDrop d)
        {
            string n = d.m_itemData.m_shared.m_name;
            var loc = Localization.instance;
            return loc != null ? loc.Localize(n) : n;
        }

        // Re-enumerate next access (e.g. if modded customization items stream in after first build).
        public static void InvalidateLists() { s_hairs = null; s_beards = null; }

        public static int HairCount  => EnsureLists() ? s_hairs.Count  : 0;
        public static int BeardCount => EnsureLists() ? s_beards.Count : 0;

        // Assert the configured styles on the local player. Idempotent: only calls SetHair/SetBeard when
        // the current style actually differs, so the periodic driver tick is cheap.
        public static void ApplyStyles()
        {
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();  // local OR character-select preview
            if (p == null || !EnsureLists()) return;

            if (HairStyle >= 0 && HairStyle < s_hairs.Count)
            {
                string name = s_hairs[HairStyle].gameObject.name;
                if (p.GetHair() != name)
                {
                    p.SetHair(name);
                    if (name != s_lastHairName) { s_lastHairName = name; FiresLogger.LogVerbose($"[FiresPlayer] hair style {HairStyle} = {Loc(s_hairs[HairStyle])}"); }
                }
            }
            if (BeardStyle >= 0 && BeardStyle < s_beards.Count)
            {
                string name = s_beards[BeardStyle].gameObject.name;
                if (p.GetBeard() != name)
                {
                    p.SetBeard(name);
                    if (name != s_lastBeardName) { s_lastBeardName = name; FiresLogger.LogVerbose($"[FiresPlayer] beard style {BeardStyle} = {Loc(s_beards[BeardStyle])}"); }
                }
            }
        }

        // Vanilla hair color (persists + syncs); also colors the beard unless a beard override is set.
        // Works on the character-select/barber preview too (ResolveLocalOrPreview).
        public static void ApplyHairColor()
        {
            if (!HairColorEnabled) return;
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) return;
            p.SetHairColor(new Vector3(HairColor.r, HairColor.g, HairColor.b)); // VisEquipment guards no-change
        }

        // Beard gets its OWN color via a per-renderer MaterialPropertyBlock (overrides the hair color that
        // vanilla UpdateColors writes to the beard material every frame). LOCAL only; cleared when disabled.
        public static void ApplyBeardColorTick()
        {
            var p = FiresPlayerShaderLoader.ResolveLocalOrPreview();
            if (p == null) return;
            var ve = p.GetComponent<VisEquipment>();
            if (ve == null) return;
            var beard = GetBeardInstance(ve);
            if (beard == null) return;
            if (s_block == null) s_block = new MaterialPropertyBlock();

            if (BeardColorEnabled)
            {
                foreach (var r in beard.GetComponentsInChildren<Renderer>())
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(s_block);
                    s_block.SetColor(Prop_SkinColor, BeardColor);
                    r.SetPropertyBlock(s_block);
                }
                s_beardApplied = true;
            }
            else if (s_beardApplied)
            {
                foreach (var r in beard.GetComponentsInChildren<Renderer>())
                    if (r != null) r.SetPropertyBlock(null); // back to the material's (hair) color
                s_beardApplied = false;
            }
        }

        private static GameObject GetBeardInstance(VisEquipment ve)
        {
            var f = typeof(VisEquipment).GetField("m_beardItemInstance",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return f != null ? f.GetValue(ve) as GameObject : null;
        }

        public static string ListStyles()
        {
            if (!EnsureLists()) return "ObjectDB not ready yet — load into a world first.";
            var sb = new StringBuilder();
            sb.Append("Hair (").Append(s_hairs.Count).Append("):");
            for (int i = 0; i < s_hairs.Count; i++) sb.Append("  ").Append(i).Append('=').Append(Loc(s_hairs[i]));
            sb.Append("\nBeard (").Append(s_beards.Count).Append("):");
            for (int i = 0; i < s_beards.Count; i++) sb.Append("  ").Append(i).Append('=').Append(Loc(s_beards[i]));
            return sb.ToString();
        }
    }

    /// <summary>Applies the configured styles/colors once the local player exists; registers <c>va_styles</c>.</summary>
    public class FiresPlayerCustomizationDriver : MonoBehaviour
    {
        private bool _cmd;
        private float _next;

        private void Update()
        {
            if (!_cmd) { try { StylesCommand.Register(); _cmd = true; } catch { } }
            if (FiresPlayerShaderLoader.ResolveLocalOrPreview() == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;

            FiresPlayerCustomization.ApplyStyles();       // local or character-select preview
            FiresPlayerCustomization.ApplyHairColor();     // local or preview
            FiresPlayerCustomization.ApplyBeardColorTick();
        }
    }

    /// <summary><c>va_styles</c> — list available hair/beard style indices for the config sliders.</summary>
    public static class StylesCommand
    {
        private static bool s_registered;

        public static void Register()
        {
            if (s_registered) return;
            new Terminal.ConsoleCommand("va_styles",
                "List hair/beard style indices for the FiresPlayer config sliders.",
                args => { args.Context.AddString(FiresPlayerCustomization.ListStyles()); },
                isCheat: false);
            s_registered = true;
        }
    }
}
