using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // FTS's sections for the shared FiresCore help panel (registration only).
    internal static class FtsHelpContent
    {
        private const string ModId = "Fires Tossin Shade";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "The Visual Overhaul", 0, BuildOverview);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Water, Sky & Fog", 1, BuildWaterSky);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Character Cosmetics", 2, BuildCosmetics);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Stained Glass", 3, BuildGlass);
            // 0.3.158 (Fire 10-08): the client-local toggles, cosmetics and diagnostics are open to everyone (a player heading); only va_glass and
            // fires_preset, which change the world / rewrite the config, are admin-only
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Commands", 4, BuildCommands);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Admin Commands", 5, BuildAdminCommands, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Visual Overhaul");
            w.Paragraph("Fires Tossin Shade replaces Valheim's water, sky, fog, and several biome " +
                "shaders with custom versions — plus character cosmetics and stained glass. It is " +
                "almost entirely client-side rendering: turning things off changes only what YOU see.");
            w.Divider();

            w.SubHeader("The Master Switch");
            w.Bullet("[00 - Master] CustomShaderEffects — one toggle for ALL cosmetic overrides. The " +
                "water stack stays on regardless (tides and swimming depend on it).");
            w.Bullet("Every subsystem below also has its own Enabled toggle in F8 — sliders apply live.");
            w.Divider();

            w.SubHeader("What's Included");
            w.Bullet("FiresWater — the full ocean/lake surface, reflections, caustics, underwater");
            w.Bullet("Sky: procedural skybox, cloud atmosphere, the planet dome, auroras");
            w.Bullet("Volumetric fog: land / sea / underwater layers, weather-reactive");
            w.Bullet("Biome swaps: Ashlands lava, lava terrain paint, Mistlands mist, building pieces");
            w.Bullet("Character: eye color, elf ears, physics hair & beard");
            w.Bullet("Stained-glass windows that project colored light");
        }

        private static void BuildWaterSky(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Water, Sky & Fog");
            w.SubHeader("FiresWater");
            w.Bullet("Custom surface with planar mirror reflections, sun glints, foam, and " +
                "subsurface scattering — tuned under [03 - FiresWater Surface].");
            w.Bullet("Seam-free water to the horizon ([05 - Far Water Disk] + clipmap ocean).");
            w.Bullet("Underwater: depth fog, rising bubbles, caustic light dapple, a waterline meniscus.");
            w.Bullet("Surface + seafloor caustics; [02 - FiresWater] toggles each piece.");
            w.Divider();

            w.SubHeader("Sky & Atmosphere");
            w.Bullet("[07 - FiresSky] — skybox overrides: sun size/brightness, exposure, day-sky hue, stars.");
            w.Bullet("[11 - FiresAtmosphere] — the cloud layer: coverage, softness, storm darkening.");
            w.Bullet("[12 - FiresPlanet] — fly high enough and the cloud dome reads as a planet seen " +
                "from space, with your own world map on it: what M shows, fog over the unexplored parts and all.");
            w.Bullet("[13-16 Aurora*] — northern lights: sky overlay, horizon glow, a real directional " +
                "light so water reflects them, and aurora god-ray shafts.");
            w.Divider();

            w.SubHeader("Fog");
            w.Bullet("[09* - Fog ...] — volumetric fog in separate land / sea / underwater / cloud " +
                "layers, each with color, height band, density, and patchiness.");
            w.Bullet("[09d Near-Player Push] — fog thins around you so you're never blinded.");
            w.Bullet("[09e Fog Weather] — fog reacts to rain and storms.");
            w.Bullet("[08 - Distance Fog] — vanilla distance-fog override (color, density, mode).");
            w.Bullet("Storm lightning is composited additively so flashes never punch rectangular holes " +
                "in the custom sky ([02 - FiresWater] 18 LightningFxBlend).");
        }

        private static void BuildCosmetics(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Character Cosmetics");
            w.Paragraph("All client-side and all optional — other players need FTS installed to see " +
                "yours.");
            w.Divider();

            w.SubHeader("Eyes, Ears, Hair");
            w.Bullet("Eye color — [10 - FiresPlayer] EyeColor (va_player toggles the character shader).");
            w.Bullet("Elf ears — [10b - Elf Ears]: shape set, size, splay, sweep, tilt, color. " +
                "va_ears next cycles ear sets in-game.");
            w.Bullet("Physics hair & beard — [10c - Hair Physics]: strands that sway with movement, " +
                "wind, and gravity, colliding with your head and shoulders. va_hair toggles.");
            w.Bullet("Style & color — [10d - Hair & Beard]: pick any hair/beard style index (va_styles " +
                "lists them), recolor both independently, and scale them ([10d - Hair & Beard Scale]).");
        }

        private static void BuildGlass(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Stained Glass");
            w.Paragraph("Stained-glass windows project their colors as real light — colored pools on " +
                "the floor, god-rays through the air, neon signs that cast their glow.");
            w.Bullet("Build glass pieces with the hammer (FiresGlass window set).");
            w.Bullet("[20 - FiresGlass] — projection intensity, reach, edge softness, shadow behavior, " +
                "god-ray density, and neon emission.");
            w.Bullet("va_glass here — plant a test window in front of you; va_glass img <name> swaps " +
                "the pane image.");
        }

        private static void BuildCommands(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Commands");
            w.Paragraph("Every command is client-local and takes on|off|status unless noted.");
            w.Divider();

            w.SubHeader("Shader Toggles");
            w.Bullet("va_player — character shader (eye color)");
            w.Bullet("va_piece — building-piece shader (wet look, reflections; [19 - FiresPiece])");
            w.Bullet("va_lava — Ashlands lava ([21 - FiresLava])");
            w.Bullet("va_terrain on|off|status|reset — terrain shader with Ashlands lava paint");
            w.Bullet("va_mist — Mistlands heavy-mist particles ([18 - FiresMist])");
            w.Divider();

            w.SubHeader("Cosmetics");
            w.Bullet("va_ears on|off|status | set <name> | next — elf ears");
            w.Bullet("va_hair on|off|status|diag — physics hair/beard");
            w.Bullet("va_styles — list hair/beard style indices for the config sliders");
            w.Divider();

            w.SubHeader("Sky Diagnostics");
            w.Bullet("va_sky_dump — dump every sky/cloud/fog object to config/FiresTossinShade/sky_dump.txt");
            w.Bullet("va_sky_info — current sun direction + live fog/ambient/light state");
            w.Bullet("va_rdump — per-camera command buffers + transparent renderers (render-order debug)");
            w.Bullet("fires_skydiag — sky/storm/lightning state report; arms next-flash capture");
        }

        private static void BuildAdminCommands(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Admin Commands");
            w.Paragraph("Admin only: they change the world for everyone or rewrite the config.");
            w.AdminDivider();
            w.SubHeader("Glass");
            w.Bullet("va_glass ... — status/debug/here/spawn/clear/img <name>/list/reach <m>/" +
                "intensity <x>/shadow <0..1>/godray <density>");
            w.SubHeader("Presets");
            w.Bullet("fires_preset list | load <name> | save <name> | delete <name> | folder — shader presets");
        }
    }
}
