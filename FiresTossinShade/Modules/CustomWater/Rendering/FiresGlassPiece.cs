using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Registers the FiresGlass window prefabs as buildable FURNITURE pieces on the
    /// Hammer — the flat projector pane plus the downloaded scanned cathedral
    /// windows. Mirrors the proven Fires piece-registration pattern
    /// (FiresCompanions.VAPieceManager): load + DontDestroyOnLoad each bundle
    /// prefab, add Piece + ZNetView + collider, register into ZNetScene.m_prefabs /
    /// m_namedPrefabs at ZNetScene.Awake (BEFORE ZDO load, or placed windows get
    /// wiped) and into the Hammer PieceTable once ObjectDB is ready — with fake-null
    /// scrubbing so repeat world-loads in one session don't NRE other mods.
    /// </summary>
    public static class FiresGlassPiece
    {
        private struct WinDef
        {
            public string prefab, display, icon;
            public Color tint;
            public int image;   // projection-atlas cell this window casts
            public bool neon;   // self-lit sign: own beam direction, exempt from the sun gates
            public WinDef(string p, string d, string i, Color t, int img) { prefab = p; display = d; icon = i; tint = t; image = img; neon = false; }
            public WinDef(string p, string d, string i, Color t, int img, bool n) { prefab = p; display = d; icon = i; tint = t; image = img; neon = n; }
        }

        // Each window now casts its OWN face-on image (its atlas cell), so the tint
        // is neutral white — the real glass colours come from the image itself. The
        // image index MUST match the atlas cell order baked in FiresGlassBuilder.
        private static readonly WinDef[] Windows =
        {
            new WinDef("FiresGlassWindow",        "Stained Glass Pane",   "StainedEmit1", Color.white, 0),
            new WinDef("FiresGlass_Aristophanes", "Aristophanes Window",  "aristophanes", Color.white, 1),
            new WinDef("FiresGlass_Moses",        "Moses Window",         "moses",        Color.white, 2),
            new WinDef("FiresGlass_Biology",      "Biology Window",       "biology",      Color.white, 3),
            new WinDef("FiresGlass_BigRoche",     "Great Roche Window",   "bigRoche",     Color.white, 4),
            new WinDef("FiresGlass_Saint",        "Saint Window",         "StainedSaint", Color.white, 5),
            new WinDef("FiresGlass_Viking",       "Viking Window",        "StainedViking",       Color.white, 6),
            new WinDef("FiresGlass_Odin",         "Odin Window",          "StainedOdin",         Color.white, 7),
            new WinDef("FiresGlass_Thor",         "Thor Window",          "StainedThor",         Color.white, 8),
            new WinDef("FiresGlass_Freyja",       "Freyja Window",        "StainedFreyja",       Color.white, 9),
            new WinDef("FiresGlass_Shieldmaiden", "Shieldmaiden Window",  "StainedShieldmaiden", Color.white, 10),
            new WinDef("FiresGlass_Frigg",        "Frigg Window",         "StainedFrigg",        Color.white, 11),
            new WinDef("FiresGlass_Serpent",      "Serpent Window",       "StainedSerpent",      Color.white, 12),
            new WinDef("FiresGlass_Yggdrasil",    "Yggdrasil Window",     "StainedYggdrasil",    Color.white, 13),
            new WinDef("FiresGlass_Valkyrie",     "Valkyrie Window",      "StainedValkyrie",     Color.white, 14),
            new WinDef("FiresGlass_Berserker",    "Berserker Window",     "StainedBerserker",    Color.white, 15),
            new WinDef("FiresGlass_Elements",     "Elemental Rose Window",  "StainedElements",    Color.white, 16),
            new WinDef("FiresGlass_Sigil",        "Sigil Window",           "StainedSigil",       Color.white, 17),
            new WinDef("FiresGlass_Ring",         "Ring Rose Window",       "StainedRing",        Color.white, 18),
            new WinDef("FiresGlass_EmeraldArch",  "Emerald Arch Window",    "StainedEmeraldArch", Color.white, 19),
            new WinDef("FiresGlass_Rosette",      "Jeweled Rosette Window", "StainedRosette",     Color.white, 20),
            new WinDef("FiresGlass_SkullCrown",   "Skull Crown Window",    "StainedSkullCrown",   Color.white, 21),
            new WinDef("FiresGlass_Huskarl",      "Huskarl Window",        "StainedHuskarl",      Color.white, 22),
            new WinDef("FiresGlass_Spearmaiden",  "Spearmaiden Window",    "StainedSpearmaiden",  Color.white, 23),
            new WinDef("FiresGlass_AxeShield",    "Axe Shield Window",     "StainedAxeShield",    Color.white, 24),
            new WinDef("FiresGlass_GreatAxe",     "Great Axe Window",      "StainedGreatAxe",     Color.white, 25),
            new WinDef("FiresGlass_Mjolnir",      "Mjolnir Window",        "StainedMjolnir",      Color.white, 26),
            new WinDef("FiresGlass_WarShield",    "War Shield Window",     "StainedWarShield",    Color.white, 27),
            new WinDef("FiresGlass_Runestone",    "Runestone Window",      "StainedRunestone",    Color.white, 28),
            new WinDef("FiresGlass_RuneSword",    "Rune Sword Window",     "StainedRuneSword",    Color.white, 29),
            new WinDef("FiresGlass_Guardian",     "Guardian Window",       "StainedGuardian",     Color.white, 30),
            new WinDef("FiresGlass_Chieftain",    "Chieftain Window",      "StainedChieftain",    Color.white, 31),
            new WinDef("FiresGlass_SpearShield",  "Spear Shield Window",   "StainedSpearShield",  Color.white, 32),
            new WinDef("FiresGlass_DvergrDenNeon", "Dvergr Den Neon Sign", "DvergrDenSignNeon",   Color.white, 33, true),
        };

        private static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();

        private static GameObject EnsureLoaded(WinDef w)
        {
            if (s_prefabs.TryGetValue(w.prefab, out var cached) && cached != null) return cached;
            var go = FiresWaterShaderLoader.LoadBundleModel(w.prefab);
            if (go == null) return null;   // bundle not up yet — retry next pass
            Object.DontDestroyOnLoad(go);
            SetupPiece(go, w);
            s_prefabs[w.prefab] = go;
            return go;
        }

        // 0.3.158 (Fire 10-08): every glass piece costs 2 Crystal + 1 Fine wood, built at the workbench; applied again from AddToHammer if
        // ObjectDB was not ready at the first setup
        private static void ApplyCost(Piece piece)
        {
            var crystal = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Crystal")?.GetComponent<ItemDrop>() : null;
            var fineWood = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("FineWood")?.GetComponent<ItemDrop>() : null;
            if (crystal != null && fineWood != null)
                piece.m_resources = new[] { new Piece.Requirement { m_resItem = crystal, m_amount = 2, m_recover = true }, new Piece.Requirement { m_resItem = fineWood, m_amount = 1, m_recover = true } };
            var bench = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>() : null;
            if (bench != null) piece.m_craftingStation = bench;
        }

        private static void SetupPiece(GameObject go, WinDef w)
        {
            if (go.GetComponentInChildren<Collider>() == null)
            {
                var bounds = LocalBounds(go);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = bounds.center;
                bc.size = bounds.size.sqrMagnitude > 1e-4f ? bounds.size : new Vector3(2f, 3f, 0.1f);
            }

            var znv = go.GetComponent<ZNetView>() ?? go.AddComponent<ZNetView>();
            znv.m_persistent = true;

            var piece = go.GetComponent<Piece>() ?? go.AddComponent<Piece>();
            piece.m_name = w.display;
            piece.m_description = "A stained-glass window.";
            piece.m_category = Piece.PieceCategory.Furniture;
            piece.m_enabled = true;
            piece.m_canBeRemoved = true;
            piece.m_allowedInDungeons = true;
            piece.m_clipEverything = true;
            piece.m_clipGround = true;
            piece.m_groundPiece = false;
            piece.m_groundOnly = false;
            piece.m_cultivatedGroundOnly = false;
            piece.m_waterPiece = false;
            piece.m_noInWater = false;
            piece.m_notOnFloor = false;
            piece.m_notOnTiltingSurface = false;
            piece.m_allowRotatedOverlap = true;
            piece.m_placeEffect = new EffectList();
            ApplyCost(piece);   // 0.3.158 (Fire 10-08: "2 Crystal + 1 Fine wood at the workbench"; was free while testing)

            var tex = FiresWaterShaderLoader.LoadBundleAsset<Texture2D>(w.icon);
            if (tex != null) piece.m_icon = MakeSquareIcon(tex);

            // Make it cast light: every placed INSTANCE registers with the projector.
            // The component auto-registers in OnEnable when added here to the DDOL
            // template, so pull the template back out — only real placed instances
            // (their own OnEnable) should cast.
            var glass = go.GetComponent<FiresGlassWindow>() ?? go.AddComponent<FiresGlassWindow>();
            glass.Tint = w.tint;
            glass.ImageIndex = w.image;
            glass.Neon = w.neon;
            FiresGlassWindow.Registry.Remove(glass);
        }

        // Build-menu icon slots are SQUARE and stretch any non-square sprite to fill them — a tall narrow
        // window texture used directly reads ~3x too wide (the "stretched horizontally" bug). Letterbox the
        // window into a transparent square via a GPU blit (works on non-readable bundle textures) so the icon
        // keeps the window's true proportions inside the square slot.
        private static Sprite MakeSquareIcon(Texture2D tex)
        {
            const int S = 128;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

            var rt = RenderTexture.GetTemporary(S, S, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            int w = aspect >= 1f ? S : Mathf.Max(1, Mathf.RoundToInt(S * aspect));
            int h = aspect >= 1f ? Mathf.Max(1, Mathf.RoundToInt(S / aspect)) : S;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, S, S, 0);
            Graphics.DrawTexture(new Rect((S - w) / 2, (S - h) / 2, w, h), tex);
            GL.PopMatrix();
            var icon = new Texture2D(S, S, TextureFormat.RGBA32, false);
            icon.ReadPixels(new Rect(0, 0, S, S), 0, 0);
            icon.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return Sprite.Create(icon, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f));
        }

        private static Bounds LocalBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return new Bounds(Vector3.zero, new Vector3(2f, 3f, 0.1f));
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            // renderer.bounds are world; the prefab is at origin so world ≈ local.
            return b;
        }

        public static void RegisterWithZNetScene()
        {
            var znet = ZNetScene.instance;
            if (znet == null) return;
            znet.m_prefabs.RemoveAll(p => p == null);
            foreach (var w in Windows)
            {
                var go = EnsureLoaded(w);
                if (go == null || znet.GetPrefab(go.name) != null) continue;
                znet.m_prefabs.Add(go);
                AddNamed(znet, go);
            }
        }

        public static void AddToHammer()
        {
            foreach (var pf in s_prefabs.Values) if (pf != null && pf.GetComponent<Piece>() is Piece pc && (pc.m_resources == null || pc.m_resources.Length == 0)) ApplyCost(pc);   // 0.3.158: a cost missed at the first setup
            if (ObjectDB.instance == null) return;
            var hammer = ObjectDB.instance.GetItemPrefab("Hammer");
            var table = hammer?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null) return;
            table.m_pieces.RemoveAll(p => p == null);

            int added = 0;
            foreach (var w in Windows)
            {
                var go = EnsureLoaded(w);
                if (go == null) continue;
                if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(go.name) == null)
                {
                    ZNetScene.instance.m_prefabs.RemoveAll(p => p == null);
                    ZNetScene.instance.m_prefabs.Add(go);
                    AddNamed(ZNetScene.instance, go);
                }
                if (!table.m_pieces.Contains(go))
                {
                    table.m_pieces.Add(go);
                    added++;
                }
            }
            if (added > 0) Debug.Log("[FiresGlassPiece] Added " + added + " glass window piece(s) to the Hammer Furniture tab.");
        }

        private static void AddNamed(ZNetScene znet, GameObject go)
        {
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(znet) is Dictionary<int, GameObject> dict)
            {
                int hash = go.name.GetStableHashCode();
                if (!dict.ContainsKey(hash)) dict.Add(hash, go);
            }
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetScene_Awake_Glass
        {
            private static void Postfix()
            {
                try { RegisterWithZNetScene(); AddToHammer(); }
                catch (System.Exception ex) { Debug.LogWarning("[FiresGlassPiece] ZNetScene.Awake register failed: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class Player_OnSpawned_Glass
        {
            private static void Postfix()
            {
                try { AddToHammer(); } catch { }
            }
        }
    }
}
