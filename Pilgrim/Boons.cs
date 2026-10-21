using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  Pilgrim · Boons — auxiliary progression ("Rites & Keystones" rough-in)
//
//  Kills → XP → levels → a draw of 3 rolled boons; pick one, it compounds into
//  your build. D2-style affix rolls (give axis / take axis / magnitudes / rarity).
//
//  Self-contained: all [HarmonyPatch] classes here are auto-patched by Plugin.Awake
//  (it PatchAll()s every type in the assembly), and the HUD attaches itself on
//  Player.OnSpawned — no edits to Plugin.cs required.
//
//  Stat application rides entirely on a managed SE_Stats status effect (verified
//  against the decompiled assembly): m_damageModifier, m_attackStaminaUseModifier,
//  m_speedModifier, m_healthRegenMultiplier, m_staminaRegenMultiplier, m_addArmor.
//  No fragile per-method damage patches.
//
//  Persistence: Player.m_customData (saved with the character, survives relog and
//  travels between worlds). Multiplayer-safe: each client tracks its own player,
//  XP is credited only when the local player landed the killing blow.
//
//  TODO(iterate): wire tunables into Pilgrim.yaml; add affix gating by level;
//  per-axis god-roll tiers; the Monte-Carlo balance pass; nicer uGUI HUD.
// ─────────────────────────────────────────────────────────────────────────────
namespace EnvReporter
{
    // ---- facade over Plugin.Cfg.Boons (lives in Pilgrim.yaml; hot-reloads via the config watcher) ----
    internal static class BoonCfg
    {
        static readonly BoonsConfig _fb = new BoonsConfig();
        static BoonsConfig C => Plugin.Cfg?.Boons ?? _fb;

        internal static void EnsureBound() { }   // config is in Pilgrim.yaml now; nothing to bind

        internal static bool   Enabled           => C.Enabled;
        internal static int    XpPerKill         => C.XpPerKill;
        internal static int    DrawCount         => C.DrawCount;
        internal static float  TakeChance        => C.TakeChance;
        internal static float  PristineOdds      => C.PristineOdds;
        internal static int    Rerolls           => C.Rerolls;
        internal static string LevelUpVfx        => C.LevelUpVfx;
        internal static float  LevelUpVfxDensity => C.LevelUpVfxDensity;
        internal static string PickSfx           => C.PickSfx;
        internal static string ToggleSfx         => C.ToggleSfx;
        internal static string ChooserCardSprite => C.ChooserCardSprite;
        internal static string ChooserPanelSprite => C.ChooserPanelSprite;
        internal static float  ChooserPanelZoom  => C.ChooserPanelZoom;
        internal static string BoonAlert         => C.BoonAlert;
        internal static float  XpBarWidth        => C.XpBarWidth;
        internal static float  XpBarNudgeX       => C.XpBarNudgeX;
        internal static float  XpBarNudgeY       => C.XpBarNudgeY;
        // xp to go from level L -> L+1  (i.e. "level n from n-1 costs 10n")
        internal static int NeedForNext(int level) => 10 * (level + 1);
    }

    // Append new axes at the END — the int value is persisted in saved boons, so existing indices must not shift.
    internal enum BoonAxis
    {
        Damage, AtkStamina, MoveSpeed, HpRegen, StamRegen, Block, RunStamina, JumpStamina,
        BlockStamina, DodgeStamina, SwimStamina, SneakStamina, Stagger, FallDamage,
        ArmorPct, Parry, EitrRegen, SwimSpeed, Stealth, CarryWeight
    }

    internal sealed class AxisDef
    {
        // Each axis carries a positive form (for a gift) and a negative form (for a cost), and each form
        // has BOTH an adjective (prefix slot) and a noun (suffix slot) — so a name can be built either way.
        public string GiveAdj, GiveNoun, TakeAdj, TakeNoun, Noun;
        public bool Percent;             // display/semantics as % (else flat)
        public bool Cost;                // "cost" axis: a GIFT reduces the stat (shown as −), a COST raises it (+)
    }

    internal static class Axes
    {
        // Magnitudes are a unified int 0–10 roll per axis (see BoonRoller); unit is % unless flat.
        internal static readonly Dictionary<BoonAxis, AxisDef> Def = new()
        {
            [BoonAxis.Damage]      = new AxisDef { GiveAdj="Keen",      GiveNoun="the Edge",   TakeAdj="Dull",       TakeNoun="the Dull",   Noun="damage",        Percent=true },
            [BoonAxis.AtkStamina]  = new AxisDef { GiveAdj="Fluid",     GiveNoun="Grace",      TakeAdj="Labored",    TakeNoun="Toil",       Noun="attack stamina",Percent=true, Cost=true },
            [BoonAxis.MoveSpeed]   = new AxisDef { GiveAdj="Swift",     GiveNoun="Haste",      TakeAdj="Sluggish",   TakeNoun="Sloth",      Noun="move speed",    Percent=true },
            [BoonAxis.HpRegen]     = new AxisDef { GiveAdj="Mending",   GiveNoun="Recovery",   TakeAdj="Festering",  TakeNoun="the Wound",  Noun="health regen",  Percent=true },
            [BoonAxis.StamRegen]   = new AxisDef { GiveAdj="Tireless",  GiveNoun="Endurance",  TakeAdj="Weary",      TakeNoun="Weariness",  Noun="stamina regen", Percent=true },
            [BoonAxis.Block]       = new AxisDef { GiveAdj="Warded",    GiveNoun="the Bulwark",TakeAdj="Brittle",    TakeNoun="Brittleness",Noun="armor",         Percent=false },
            [BoonAxis.RunStamina]  = new AxisDef { GiveAdj="Bounding",  GiveNoun="Vigor",      TakeAdj="Heavy",      TakeNoun="the Burden", Noun="run stamina",   Percent=true, Cost=true },
            [BoonAxis.JumpStamina] = new AxisDef { GiveAdj="Lofty",     GiveNoun="the Spring", TakeAdj="Leaden",     TakeNoun="the Anchor", Noun="jump stamina",  Percent=true, Cost=true },
            [BoonAxis.BlockStamina]= new AxisDef { GiveAdj="Steady",    GiveNoun="the Guard",  TakeAdj="Strained",   TakeNoun="the Strain", Noun="block stamina", Percent=true, Cost=true },
            [BoonAxis.DodgeStamina]= new AxisDef { GiveAdj="Nimble",    GiveNoun="Evasion",    TakeAdj="Clumsy",     TakeNoun="Clumsiness", Noun="dodge stamina", Percent=true, Cost=true },
            [BoonAxis.SwimStamina] = new AxisDef { GiveAdj="Buoyant",   GiveNoun="the Tide",   TakeAdj="Sinking",    TakeNoun="the Depths", Noun="swim stamina",  Percent=true, Cost=true },
            [BoonAxis.SneakStamina]= new AxisDef { GiveAdj="Quiet",     GiveNoun="Stillness",  TakeAdj="Restless",   TakeNoun="Restlessness",Noun="sneak stamina",Percent=true, Cost=true },
            [BoonAxis.Stagger]     = new AxisDef { GiveAdj="Unshaken",  GiveNoun="Poise",      TakeAdj="Faltering",  TakeNoun="the Falter", Noun="stagger taken", Percent=true, Cost=true },
            [BoonAxis.FallDamage]  = new AxisDef { GiveAdj="Feathered", GiveNoun="the Feather",TakeAdj="Plummeting", TakeNoun="the Plummet",Noun="fall damage",   Percent=true, Cost=true },
            [BoonAxis.ArmorPct]    = new AxisDef { GiveAdj="Ironclad",  GiveNoun="the Aegis",  TakeAdj="Exposed",    TakeNoun="Exposure",   Noun="armor %",       Percent=true },
            [BoonAxis.Parry]       = new AxisDef { GiveAdj="Deflecting",GiveNoun="the Parry",  TakeAdj="Open",       TakeNoun="the Opening",Noun="parry bonus",   Percent=true },
            [BoonAxis.EitrRegen]   = new AxisDef { GiveAdj="Attuned",   GiveNoun="the Flow",   TakeAdj="Dim",        TakeNoun="the Dimming",Noun="eitr regen",    Percent=true },
            [BoonAxis.SwimSpeed]   = new AxisDef { GiveAdj="Gliding",   GiveNoun="the Current",TakeAdj="Dragging",   TakeNoun="the Drag",   Noun="swim speed",    Percent=true },
            [BoonAxis.Stealth]     = new AxisDef { GiveAdj="Unseen",    GiveNoun="the Veil",   TakeAdj="Conspicuous",TakeNoun="the Glare",  Noun="stealth",       Percent=true },
            [BoonAxis.CarryWeight] = new AxisDef { GiveAdj="Mighty",    GiveNoun="the Ox",     TakeAdj="Overladen",  TakeNoun="the Load",   Noun="carry weight",  Percent=false },
        };
        internal static readonly BoonAxis[] All = (BoonAxis[])Enum.GetValues(typeof(BoonAxis));
    }

    // ---- one rolled boon -----------------------------------------------------
    internal sealed class Boon
    {
        public BoonAxis GiveAxis;  public int GiveAmt;
        public bool     HasTake;   public BoonAxis TakeAxis; public int TakeAmt;
        public int      Rarity;    // 0 common, 1 uncommon, 2 rare, 3 exceptional, 4 legendary

        // Prefix = gift adjective (give axis); suffix = cost noun (take axis).
        // A pure boon reuses the give axis's own positive noun as the suffix ("Keen Boon of the Edge").
        public string Name => HasTake
            ? $"{Axes.Def[GiveAxis].GiveAdj} Boon of {Axes.Def[TakeAxis].TakeNoun}"
            : $"{Axes.Def[GiveAxis].GiveAdj} Boon of {Axes.Def[GiveAxis].GiveNoun}";

        public string GiveText => Line(GiveAxis, GiveAmt, give:true);
        public string TakeText => HasTake ? Line(TakeAxis, TakeAmt, give:false) : "";

        static string Line(BoonAxis ax, int amt, bool give)
        {
            var d = Axes.Def[ax];
            string sign = (give ^ d.Cost) ? "+" : "-";   // cost axis: a gift REDUCES the stat (shown −)
            string unit = d.Percent ? "%" : "";
            return $"{sign}{amt}{unit} {d.Noun}";
        }

        // compact serialization:  g,gAmt,hasTake(0/1),t,tAmt,rarity
        public string Serialize() =>
            string.Join(",", (int)GiveAxis, GiveAmt, HasTake ? 1 : 0, (int)TakeAxis, TakeAmt, Rarity);

        public static Boon Deserialize(string s)
        {
            var p = s.Split(',');
            return new Boon {
                GiveAxis=(BoonAxis)int.Parse(p[0]), GiveAmt=int.Parse(p[1]),
                HasTake=p[2]=="1", TakeAxis=(BoonAxis)int.Parse(p[3]), TakeAmt=int.Parse(p[4]),
                Rarity=int.Parse(p[5]) };
        }
    }

    // ---- the roll engine (port of the prototype) -----------------------------
    internal static class BoonRoller
    {
        static readonly System.Random Rng = new System.Random();

        // One magnitude roll: an int on 0–10. Identical rule for first roll AND rerolls (same code path).
        // PristineOdds bumps the roll into the top band (a lucky high roll), applied every time.
        static int Roll010()
        {
            double r = Rng.NextDouble();
            if (Rng.NextDouble() < BoonCfg.PristineOdds) r = 0.85 + 0.15 * Rng.NextDouble();
            return (int)Math.Round(r * 10.0);
        }

        internal static Boon Roll()
        {
            var b = new Boon();
            // give and take axes rolled independently → P(same axis) = (1/6)², intuitive and uncoerced
            b.GiveAxis = Axes.All[Rng.Next(Axes.All.Length)];
            b.TakeAxis = Axes.All[Rng.Next(Axes.All.Length)];

            int give = Roll010();
            int take = Roll010();
            if (take > give) give = Math.Min(10, take + 1);   // clamp in the player's favor: gift always beats the cost
            give = Math.Max(1, give);                          // a 0/0 roll still grants a give of 1 (weak pure boon), never a no-op

            // give and take rolled the SAME axis → they cancel into one net gift: a pure boon of (give − take), min 1
            if (b.GiveAxis == b.TakeAxis) { give = Math.Max(1, give - take); take = 0; }

            b.GiveAmt = give;
            b.TakeAmt = take;
            b.HasTake = take > 0;                               // take rolled to 0 → incidental "pure" boon

            // rarity considers the magnitudes: bigger gift = rarer, a steep cost drags it back down,
            // a pure (costless) boon gets a bonus. 5 tiers.
            float score = give - 0.5f * take + (b.HasTake ? 0f : 1.5f);
            b.Rarity = score >= 9f ? 4 : score >= 7f ? 3 : score >= 5f ? 2 : score >= 3f ? 1 : 0;
            return b;
        }

        // The offers must be meaningfully distinct: no two share the same give/take axis pair (else the player
        // just takes the better of the duplicates). Reroll until we have DrawCount unique offers.
        internal static List<Boon> Draw()
        {
            var list = new List<Boon>();
            var seen = new HashSet<(int give, int take)>();
            int guard = 0;
            while (list.Count < BoonCfg.DrawCount && guard++ < 200)
            {
                var b = Roll();
                var key = ((int)b.GiveAxis, b.HasTake ? (int)b.TakeAxis : -1);  // pure boons key on give only
                if (seen.Add(key)) list.Add(b);
            }
            return list;
        }
    }

    // ---- per-character state (persisted in Player.m_customData) ---------------
    internal sealed class BoonState
    {
        const string Key = "pilgrim.boons.v1";

        public int Level = 1, Xp = 0, Pending = 0;
        public readonly List<Boon> Boons = new();

        public static BoonState Load(Player p)
        {
            var st = new BoonState();
            if (p != null && p.m_customData != null && p.m_customData.TryGetValue(Key, out var raw) && !string.IsNullOrEmpty(raw))
            {
                try
                {
                    var head = raw.Split('|');
                    st.Level = int.Parse(head[0]); st.Xp = int.Parse(head[1]); st.Pending = int.Parse(head[2]);
                    if (head.Length > 3 && head[3].Length > 0)
                        foreach (var chunk in head[3].Split(';'))
                            if (chunk.Length > 0) st.Boons.Add(Boon.Deserialize(chunk));
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"[Pilgrim] boon load failed: {e.Message}"); }
            }
            return st;
        }

        public void Save(Player p)
        {
            if (p == null || p.m_customData == null) return;
            string boons = string.Join(";", Boons.Select(b => b.Serialize()));
            p.m_customData[Key] = $"{Level}|{Xp}|{Pending}|{boons}";
        }

        public void AddXp(int amount)
        {
            Xp += amount;
            while (Xp >= BoonCfg.NeedForNext(Level)) { Xp -= BoonCfg.NeedForNext(Level); Level++; Pending++; }
        }

        public void Respec() { Boons.Clear(); Pending = 0; Level = 1; Xp = 0; } // cheap early levels refill fast

        // aggregate give(+)/take(-) points per axis
        public Dictionary<BoonAxis, int> Totals()
        {
            var t = Axes.All.ToDictionary(a => a, _ => 0);
            foreach (var b in Boons)
            {
                t[b.GiveAxis] += b.GiveAmt;
                if (b.HasTake) t[b.TakeAxis] -= b.TakeAmt;
            }
            return t;
        }
    }

    // ---- stat application via a managed SE_Stats ------------------------------
    internal static class BoonStats
    {
        const string SeName = "PilgrimBoons";
        static readonly int SeHash = SeName.GetStableHashCode(); // matches NameHash() (uses Object.name)
        static SE_Stats _template;

        // Configured once; SEMan.AddStatusEffect CLONES this, so we mutate the clone it holds.
        static SE_Stats Template()
        {
            if (_template != null) return _template;
            _template = ScriptableObject.CreateInstance<SE_Stats>();
            _template.name  = SeName;                        // NameHash() hashes Object.name
            _template.m_name = SeName;
            _template.m_ttl = 0f;                            // permanent
            _template.m_icon = null;
            _template.m_modifyAttackSkill = Skills.SkillType.All; // damage mod applies to every weapon
            return _template;
        }

        // Ensure the SE is on the player and the LIVE clone's fields reflect the current build.
        internal static void Apply(Player p, BoonState st)
        {
            if (p == null) return;
            var seman = p.GetSEMan();
            if (seman == null) return;
            var active = seman.GetStatusEffect(SeHash) as SE_Stats;
            if (active == null)
            {
                seman.AddStatusEffect(Template(), resetTime: false);
                active = seman.GetStatusEffect(SeHash) as SE_Stats; // re-fetch the clone
            }
            if (active == null) return;

            var t = st.Totals();
            active.m_damageModifier           = 1f + t[BoonAxis.Damage]    / 100f;  // outgoing attack dmg ×
            active.m_attackStaminaUseModifier = -(t[BoonAxis.AtkStamina]   / 100f); // negative = cheaper
            active.m_runStaminaUseModifier    = -(t[BoonAxis.RunStamina]   / 100f);
            active.m_jumpStaminaUseModifier   = -(t[BoonAxis.JumpStamina]  / 100f);
            active.m_speedModifier            =  t[BoonAxis.MoveSpeed]     / 100f;
            active.m_healthRegenMultiplier    = 1f + t[BoonAxis.HpRegen]   / 100f;
            active.m_staminaRegenMultiplier   = 1f + t[BoonAxis.StamRegen] / 100f;
            active.m_addArmor                 =  t[BoonAxis.Block];
            active.m_modifyAttackSkill        = Skills.SkillType.All;

            // additional stamina-use axes (cost: negative = cheaper)
            active.m_blockStaminaUseModifier  = -(t[BoonAxis.BlockStamina] / 100f);
            active.m_dodgeStaminaUseModifier  = -(t[BoonAxis.DodgeStamina] / 100f);
            active.m_swimStaminaUseModifier   = -(t[BoonAxis.SwimStamina]  / 100f);
            active.m_sneakStaminaUseModifier  = -(t[BoonAxis.SneakStamina] / 100f);
            // defense / utility
            active.m_staggerModifier          = -(t[BoonAxis.Stagger]      / 100f);  // negative = less stagger taken
            active.m_fallDamageModifier       = -(t[BoonAxis.FallDamage]   / 100f);  // negative = less fall damage
            active.m_armorMultiplier          =  t[BoonAxis.ArmorPct]      / 100f;   // neutral 0 → ×(1+x)
            active.m_timedBlockBonus          =  t[BoonAxis.Parry]         / 100f;   // neutral 0 → parry ×(1+x)
            active.m_eitrRegenMultiplier      = 1f + t[BoonAxis.EitrRegen] / 100f;
            active.m_swimSpeedModifier        =  t[BoonAxis.SwimSpeed]     / 100f;
            active.m_stealthModifier          =  t[BoonAxis.Stealth]       / 100f;   // higher = stealthier
            active.m_addMaxCarryWeight        =  t[BoonAxis.CarryWeight];            // flat
        }

        // Live boon SE (the clone SEMan holds) — used by the character sheet to measure deltas.
        internal static SE_Stats Active(SEMan seman) => seman?.GetStatusEffect(SeHash) as SE_Stats;

        // Strip the SE so a mid-save disable returns to vanilla without a relog.
        internal static void Remove(Player p)
        {
            var seman = p?.GetSEMan();
            if (seman != null && seman.GetStatusEffect(SeHash) != null)
                seman.RemoveStatusEffect(SeHash);
        }
    }

    // ---- kill → XP -----------------------------------------------------------
    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class Boon_KillXp
    {
        static void Postfix(Character __instance)
        {
            if (!BoonCfg.Enabled) return;
            if (__instance == null || __instance is Player) return;        // only monster deaths
            var local = Player.m_localPlayer;
            if (local == null) return;
            var lastHit = Traverse.Create(__instance).Field("m_lastHit").GetValue<HitData>();
            if (lastHit == null || lastHit.GetAttacker() != local) return; // local player's kill only

            var st = BoonHud.State;
            if (st == null) return;
            st.AddXp(BoonCfg.XpPerKill);
            st.Save(local);
        }
    }

    // ---- load/attach on spawn ------------------------------------------------
    [HarmonyPatch(typeof(Player), "OnSpawned")]
    internal static class Boon_OnSpawned
    {
        static void Postfix(Player __instance)
        {
            if (__instance == null || __instance != Player.m_localPlayer) return;
            CharSheet.Ensure();                             // general feature, independent of boons
            BoonCfg.EnsureBound();
            if (!BoonCfg.Enabled) { BoonStats.Remove(__instance); return; } // pure vanilla
            BoonHud.State = BoonState.Load(__instance);
            BoonStats.Apply(__instance, BoonHud.State);     // SEMan is cleared on respawn — re-add
            BoonHud.Ensure();
        }
    }

    // Death = full respec: lose all boons, reset to level 1. Early levels are cheap, so
    // XP floods back fast. (Matches the Pilgrim death-stakes design.)
    [HarmonyPatch(typeof(Player), "OnDeath")]
    internal static class Boon_DeathRespec
    {
        static void Postfix(Player __instance)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (BoonHud.State == null) return;
            BoonHud.ForceClose();
            BoonHud.State.Respec();
            BoonHud.State.Save(__instance);
            BoonStats.Apply(__instance, BoonHud.State);   // strip boon modifiers back to baseline
        }
    }

    // While the boon chooser is open, make the game treat it exactly like the inventory
    // is open. The game's own UpdateMouseCapture() frees the cursor and its camera gate
    // stops rotation — both keyed off InventoryGui.IsVisible(). One hook → identical feel.
    [HarmonyPatch(typeof(InventoryGui), "IsVisible")]
    internal static class Boon_ModalAsInventory
    {
        static void Postfix(ref bool __result) { if (BoonHud.Modal) __result = true; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Minimal IMGUI HUD: thin xp bar, blinking orb, 3-card chooser.
    //  (Rough-in: fixed screen coords + OnGUI so there are no asset bundles to
    //  build. Replace with uGUI anchored to the health panel for the real thing.)
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class BoonHud : MonoBehaviour
    {
        internal static BoonState State;
        internal static bool Modal;        // true while the chooser is open (gates input patches)
        static BoonHud _inst;

        List<Boon> _offer;
        int _rerolls;
        bool _choosing;
        int _lastLevel = -1;
        float _applyTimer, _blink;
        Texture2D _black, _dim, _panel, _xpGrad, _white;
        GUIStyle _small, _iconStyle;
        // Valheim-ish panel palette
        static readonly Color PanelBg     = new Color(0.11f, 0.09f, 0.06f, 0.96f);
        static readonly Color PanelBgHover = new Color(0.17f, 0.14f, 0.09f, 0.98f);
        static readonly Color PanelBorder = new Color(0.47f, 0.38f, 0.22f, 1f);
        readonly Color[] _rarityCol = {
            new Color(0.72f,0.68f,0.58f), new Color(0.44f,0.56f,0.85f),
            new Color(0.85f,0.70f,0.35f), new Color(0.88f,0.54f,0.24f) };
        static readonly string[] RarityName = { "Common", "Magic", "Rare", "Exceptional" };

        internal static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("PilgrimBoonHud");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<BoonHud>();
            BoonHudUI.Ensure();      // uGUI xp bar + diamond icon
            BoonChooserUI.Ensure();  // uGUI 3-card chooser
        }

        // ---- state exposed to the uGUI chooser (BoonChooserUI) ----
        internal static bool Choosing => _inst != null && _inst._choosing;
        internal static List<Boon> Offer => _inst?._offer;
        internal static int RerollsLeft => _inst?._rerolls ?? 0;
        internal static void PickIndex(int i)
        {
            if (_inst?._offer != null && i >= 0 && i < _inst._offer.Count) _inst.Choose(_inst._offer[i]);
        }
        internal static void DoReroll()
        {
            if (_inst != null && _inst._rerolls > 0) { _inst._rerolls--; _inst._offer = BoonRoller.Draw(); }
        }
        // clicking the xp icon toggles the chooser (open if boons are pending, else close)
        internal static void ToggleDraw()
        {
            if (_inst == null) return;
            if (_inst._choosing) _inst.CloseChooser();
            else if (State != null && State.Pending > 0) _inst.OpenDraw();
        }

        // suppress hotkeys while typing in console/chat or a menu is open
        static bool Typing() =>
            Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()
            || (Chat.instance != null && Chat.instance.HasFocus());

        Texture2D Tex(Color c) { var t = new Texture2D(1,1); t.SetPixel(0,0,c); t.Apply(); return t; }

        // horizontal gold gradient (left→right), to mirror the health bar's gradient
        Texture2D GradTex()
        {
            const int N = 64;
            var t = new Texture2D(N, 1) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var lo = new Color(0.45f, 0.33f, 0.12f); var hi = new Color(0.95f, 0.82f, 0.42f);
            for (int i = 0; i < N; i++) t.SetPixel(i, 0, Color.Lerp(hi, lo, i / (float)(N - 1))); // bright→dark L→R
            t.Apply(); return t;
        }

        void Awake()
        {
            _black  = Tex(new Color(0,0,0,0.85f));
            _dim    = Tex(new Color(0,0,0,0.6f));
            _panel  = Tex(new Color(0.10f,0.09f,0.06f,0.97f));
            _white  = Tex(Color.white);
            _xpGrad = GradTex();
        }

        void EnsureStyles()
        {
            if (_small != null) return;
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            _small.normal.textColor = new Color(0.88f,0.82f,0.70f);
            _iconStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _iconStyle.normal.textColor = new Color(0.92f,0.80f,0.44f);
        }

        Rect Inset(Rect r, float n) => new Rect(r.x + n, r.y + n, r.width - 2 * n, r.height - 2 * n);
        void DrawPanel(Rect r, bool hover = false)
        {
            var c = GUI.color;
            GUI.color = PanelBorder; GUI.DrawTexture(r, _white);
            GUI.color = hover ? PanelBgHover : PanelBg; GUI.DrawTexture(Inset(r, 2f), _white);
            GUI.color = c;
        }

        void Update()
        {
            if (!BoonCfg.Enabled) return;
            var p = Player.m_localPlayer;
            if (p == null || State == null) return;

            // level-up VFX (skip the first observation so it doesn't fire on login / respec)
            if (_lastLevel < 0) _lastLevel = State.Level;
            else if (State.Level > _lastLevel)
            {
                Plugin.Log?.LogInfo($"[Pilgrim] boon level up → {State.Level} (vfx: {BoonCfg.LevelUpVfx})");
                BoonFx.PlayLevelUp(p);
                p.Message(MessageHud.MessageType.TopLeft, $"Boon level {State.Level}");
            }
            _lastLevel = State.Level;

            _blink += Time.deltaTime;
            _applyTimer += Time.deltaTime;
            if (_applyTimer > 0.5f) { _applyTimer = 0f; BoonStats.Apply(p, State); }

            // open/close the draw (ignored while typing in console/chat or a menu is up)
            if (!Typing())
            {
                if (Input.GetKeyDown(KeyCode.B))
                {
                    if (_choosing) CloseChooser();
                    else if (State.Pending > 0) OpenDraw();
                }
                if (_choosing && Input.GetKeyDown(KeyCode.Escape)) CloseChooser();
            }
            // cursor/camera handled by the InventoryGui.IsVisible hook — don't fight it here
        }

        void OpenDraw()
        {
            if (State.Pending <= 0) return;
            if (_offer == null) { _offer = BoonRoller.Draw(); _rerolls = BoonCfg.Rerolls; } // roll once per banked boon
            _choosing = true; Modal = true;
            BoonFx.PlayUi(true);   // game's inventory-open sound
        }

        void CloseChooser() { _choosing = false; Modal = false; BoonFx.PlayUi(false); } // game's inventory-close sound
        internal static void RequestClose() { if (_inst != null && _inst._choosing) _inst.CloseChooser(); } // click-off / outside the panel
        internal static void ForceClose() { if (_inst != null) { _inst._choosing = false; _inst._offer = null; } Modal = false; } // on death

        void Choose(Boon b)
        {
            State.Boons.Add(b);
            State.Pending--;
            State.Save(Player.m_localPlayer);
            BoonStats.Apply(Player.m_localPlayer, State);
            BoonFx.PlaySound(BoonCfg.PickSfx);   // extra pick sound (e.g. coins) on top of the button click
            if (State.Pending > 0) { _offer = BoonRoller.Draw(); _rerolls = BoonCfg.Rerolls; } // fresh roll for next boon
            else { _offer = null; CloseChooser(); }                                            // only close on the last one
        }

        // Screen-space rect (GUI coords, y-down) of the food-slot column, so we can sit left of it.
        bool FoodRect(out Rect r)
        {
            r = default;
            var root = Hud.instance?.m_foodBarRoot;
            if (root == null) return false;
            var c = new Vector3[4]; root.GetWorldCorners(c);      // overlay canvas → screen pixels (y-up)
            float left = c[0].x, top = Screen.height - c[1].y, bottom = Screen.height - c[0].y;
            r = new Rect(left, top, c[2].x - c[0].x, bottom - top);
            return r.height > 1f;
        }

        void OnGUI() { }  // chooser now rendered by BoonChooserUI (uGUI); xp bar by BoonHudUI
    }
}
