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
        internal static float LegendaryChance => C.LegendaryChance;
        internal static float RareChance => C.RareChance;
        internal static bool LegendaryEnabled(KeystoneId id) => id switch
        {
            KeystoneId.Grief => C.GriefEnabled,
            KeystoneId.Grace => C.GraceEnabled,
            KeystoneId.Conviction => C.ConvictionEnabled,
            KeystoneId.Sonder => C.SonderEnabled,
            KeystoneId.Dread => C.DreadEnabled,
            KeystoneId.Avarice => C.AvariceEnabled,
            KeystoneId.Regret => C.RegretEnabled,
            KeystoneId.Resent => C.ResentEnabled,
            KeystoneId.Wrath => C.WrathEnabled,
            KeystoneId.Betrayal => C.BetrayalEnabled,
            KeystoneId.Panic => C.PanicEnabled,
            KeystoneId.Nostalgia => C.NostalgiaEnabled,
            _ => true,
        };
        // xp to go from level L -> L+1  (i.e. "level n from n-1 costs 10n")
        internal static int NeedForNext(int level) => 10 * (level + 1);
    }

    // ---- legendaries: "keystones" (the Unique tier). Each is a named on/off effect with its own Harmony hook. ----
    internal enum KeystoneId { None = 0, Grief = 1, Grace = 2, Conviction = 3, Sonder = 4, Dread = 5, Avarice = 6, Regret = 7, Resent = 8, Wrath = 9, Betrayal = 10, Panic = 11, Nostalgia = 12 }   // append new ids at the END (persisted)

    // Desc = flowery card text; Sheet = plain effect text for the character sheet.
    internal sealed class KeystoneDef { public KeystoneId Id; public string Name, Desc, Sheet; }

    internal static class Keystones
    {
        internal static readonly Dictionary<KeystoneId, KeystoneDef> Def = new()
        {
            [KeystoneId.Grief]      = new KeystoneDef { Id = KeystoneId.Grief,      Name = "Grief",      Desc = "You are the wound that never heals",   Sheet = "Damage you inflict cannot be healed" },
            [KeystoneId.Grace]      = new KeystoneDef { Id = KeystoneId.Grace,      Name = "Grace",      Desc = "You are the gasp of a dying breath",    Sheet = "A mortal blow leaves you at 1 HP, once per minute" },
            [KeystoneId.Conviction] = new KeystoneDef { Id = KeystoneId.Conviction, Name = "Conviction", Desc = "You are the shield that never yields", Sheet = "You cannot be staggered when blocking with a shield" },
            [KeystoneId.Sonder]  = new KeystoneDef { Id = KeystoneId.Sonder,  Name = "Sonder",  Desc = "You are the wisdom of perspective", Sheet = "You are granted 3x experience gained" },
            [KeystoneId.Dread]   = new KeystoneDef { Id = KeystoneId.Dread,   Name = "Dread",   Desc = "You are the premonition of danger",  Sheet = "You are granted sneak +30" },
            [KeystoneId.Avarice] = new KeystoneDef { Id = KeystoneId.Avarice, Name = "Avarice", Desc = "You are the hand in every pocket",    Sheet = "You are granted 3x treasure" },
            [KeystoneId.Regret]  = new KeystoneDef { Id = KeystoneId.Regret,  Name = "Regret",  Desc = "You'll bear anything but the weight of what you did", Sheet = "You are granted 2x carrying weight" },
            [KeystoneId.Resent]  = new KeystoneDef { Id = KeystoneId.Resent,  Name = "Resent",  Desc = "You lash out and don't remember why anymore", Sheet = "Unblocked damage reflects to the attacker" },
            [KeystoneId.Wrath]    = new KeystoneDef { Id = KeystoneId.Wrath,    Name = "Wrath",    Desc = "Your anger shall not be mitigated",  Sheet = "You deal 2x slash damage" },
            [KeystoneId.Betrayal] = new KeystoneDef { Id = KeystoneId.Betrayal, Name = "Betrayal", Desc = "You let them put their trust in you", Sheet = "You deal 10x backstab damage with knives" },
            [KeystoneId.Panic]     = new KeystoneDef { Id = KeystoneId.Panic,     Name = "Panic",     Desc = "Your hands tremble with intent",      Sheet = "Drawing your bow takes 25% as long as normal" },
            [KeystoneId.Nostalgia] = new KeystoneDef { Id = KeystoneId.Nostalgia, Name = "Nostalgia", Desc = "You long for the fellowship of youth", Sheet = "Foods last 20% longer" },
        };
        internal static readonly KeystoneId[] All = Def.Keys.ToArray();
        internal static bool Available(KeystoneId id) => Def.ContainsKey(id) && BoonCfg.LegendaryEnabled(id);
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
            [BoonAxis.AtkStamina]  = new AxisDef { GiveAdj="Fluid",     GiveNoun="Grace",      TakeAdj="Labored",    TakeNoun="Toil",       Noun="attack stamina use",Percent=true, Cost=true },
            [BoonAxis.MoveSpeed]   = new AxisDef { GiveAdj="Swift",     GiveNoun="Haste",      TakeAdj="Sluggish",   TakeNoun="Sloth",      Noun="move speed",    Percent=true },
            [BoonAxis.HpRegen]     = new AxisDef { GiveAdj="Mending",   GiveNoun="Recovery",   TakeAdj="Festering",  TakeNoun="the Wound",  Noun="health regen",  Percent=true },
            [BoonAxis.StamRegen]   = new AxisDef { GiveAdj="Tireless",  GiveNoun="Endurance",  TakeAdj="Weary",      TakeNoun="Weariness",  Noun="stamina regen", Percent=true },
            [BoonAxis.Block]       = new AxisDef { GiveAdj="Warded",    GiveNoun="the Bulwark",TakeAdj="Brittle",    TakeNoun="Brittleness",Noun="armor",         Percent=false },
            [BoonAxis.RunStamina]  = new AxisDef { GiveAdj="Bounding",  GiveNoun="Vigor",      TakeAdj="Heavy",      TakeNoun="the Burden", Noun="run stamina use",   Percent=true, Cost=true },
            [BoonAxis.JumpStamina] = new AxisDef { GiveAdj="Lofty",     GiveNoun="the Spring", TakeAdj="Leaden",     TakeNoun="the Anchor", Noun="jump stamina use",  Percent=true, Cost=true },
            [BoonAxis.BlockStamina]= new AxisDef { GiveAdj="Steady",    GiveNoun="the Guard",  TakeAdj="Strained",   TakeNoun="the Strain", Noun="block stamina use", Percent=true, Cost=true },
            [BoonAxis.DodgeStamina]= new AxisDef { GiveAdj="Nimble",    GiveNoun="Evasion",    TakeAdj="Clumsy",     TakeNoun="Clumsiness", Noun="dodge stamina use", Percent=true, Cost=true },
            [BoonAxis.SwimStamina] = new AxisDef { GiveAdj="Buoyant",   GiveNoun="the Tide",   TakeAdj="Sinking",    TakeNoun="the Depths", Noun="swim stamina use",  Percent=true, Cost=true },
            [BoonAxis.SneakStamina]= new AxisDef { GiveAdj="Quiet",     GiveNoun="Stillness",  TakeAdj="Restless",   TakeNoun="Restlessness",Noun="sneak stamina use",Percent=true, Cost=true },
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
        public bool     HasGive2;  public BoonAxis Give2Axis; public int Give2Amt;   // rares roll a second gift
        public bool     HasTake;   public BoonAxis TakeAxis; public int TakeAmt;
        public int      Rarity;    // 0 common, 1 magic, 2 rare, 3 unique (keystone)
        public KeystoneId Keystone = KeystoneId.None;   // set → this boon is a legendary keystone (no axes)

        public bool IsKeystone => Keystone != KeystoneId.None;

        // Prefix = gift adjective (give axis); suffix = cost noun (take axis).
        // A pure boon reuses the give axis's own positive noun as the suffix ("Keen Boon of the Edge").
        public string Name => IsKeystone ? Keystones.Def[Keystone].Name
            : HasTake ? $"{Axes.Def[GiveAxis].GiveAdj} Boon of {Axes.Def[TakeAxis].TakeNoun}"
            : $"{Axes.Def[GiveAxis].GiveAdj} Boon of {Axes.Def[GiveAxis].GiveNoun}";

        public string Desc => IsKeystone ? Keystones.Def[Keystone].Desc : "";

        public string GiveText => Line(GiveAxis, GiveAmt, give:true);
        public string Give2Text => HasGive2 ? Line(Give2Axis, Give2Amt, give:true) : "";
        public string TakeText => HasTake ? Line(TakeAxis, TakeAmt, give:false) : "";

        static string Line(BoonAxis ax, int amt, bool give)
        {
            var d = Axes.Def[ax];
            string sign = (give ^ d.Cost) ? "+" : "-";   // cost axis: a gift REDUCES the stat (shown −)
            string unit = d.Percent ? "%" : "";
            return $"{sign}{amt}{unit} {d.Noun}";
        }

        // compact serialization:  g,gAmt,hasTake,t,tAmt,rarity,keystone[,hasGive2,g2,g2Amt]
        public string Serialize() =>
            string.Join(",", (int)GiveAxis, GiveAmt, HasTake ? 1 : 0, (int)TakeAxis, TakeAmt, Rarity, (int)Keystone,
                        HasGive2 ? 1 : 0, (int)Give2Axis, Give2Amt);

        public static Boon Deserialize(string s)
        {
            var p = s.Split(',');
            return new Boon {
                GiveAxis=(BoonAxis)int.Parse(p[0]), GiveAmt=int.Parse(p[1]),
                HasTake=p[2]=="1", TakeAxis=(BoonAxis)int.Parse(p[3]), TakeAmt=int.Parse(p[4]),
                Rarity=int.Parse(p[5]),
                Keystone = p.Length > 6 ? (KeystoneId)int.Parse(p[6]) : KeystoneId.None,
                HasGive2 = p.Length > 7 && p[7] == "1",
                Give2Axis = p.Length > 8 ? (BoonAxis)int.Parse(p[8]) : BoonAxis.Damage,
                Give2Amt = p.Length > 9 ? int.Parse(p[9]) : 0 };
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

        internal static Boon Roll(bool rare)
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

            if (rare)
            {
                // rare: a SECOND gift (2 gives + 1 take); a lucky 0 take → 2 gives, no cost
                b.Rarity = 2;
                b.HasGive2 = true;
                b.Give2Axis = Axes.All[Rng.Next(Axes.All.Length)];
                b.Give2Amt = Math.Max(1, Roll010());
            }
            else
            {
                // common vs uncommon by magnitude: bigger gift / smaller cost = uncommon. (Rare & Unique are rolled by chance.)
                float score = give - 0.5f * take + (b.HasTake ? 0f : 1.5f);
                b.Rarity = score >= 5f ? 1 : 0;
            }
            return b;
        }

        // A keystone (Unique) from the pool: enabled, not already owned, not already offered this draw.
        static Boon RollKeystone(HashSet<KeystoneId> owned, HashSet<KeystoneId> offered)
        {
            var pool = Keystones.All.Where(k => Keystones.Available(k) && !owned.Contains(k) && !offered.Contains(k)).ToArray();
            if (pool.Length == 0) return null;
            return new Boon { Keystone = pool[Rng.Next(pool.Length)], Rarity = 3 };
        }

        // Each slot: LegendaryChance to roll a keystone, else a give/take boon. Offers must be meaningfully
        // distinct — no two share a give/take axis pair, no duplicate keystones. Reroll until DrawCount filled.
        internal static List<Boon> Draw()
        {
            var list = new List<Boon>();
            var seenAxes = new HashSet<(int give, int take)>();
            var offeredKeys = new HashSet<KeystoneId>();
            var owned = BoonHud.State != null
                ? new HashSet<KeystoneId>(BoonHud.State.Boons.Where(b => b.IsKeystone).Select(b => b.Keystone))
                : new HashSet<KeystoneId>();
            int guard = 0;
            while (list.Count < BoonCfg.DrawCount && guard++ < 400)
            {
                Boon b = null;
                double r = Rng.NextDouble();
                if (r < BoonCfg.LegendaryChance) b = RollKeystone(owned, offeredKeys);        // Unique
                else if (r < BoonCfg.LegendaryChance + BoonCfg.RareChance) b = Roll(true);     // Rare (2 gives)
                if (b == null) b = Roll(false);   // remainder (or keystone pool exhausted) → common/uncommon

                if (b.IsKeystone) { if (offeredKeys.Add(b.Keystone)) list.Add(b); }
                else { var key = ((int)b.GiveAxis, b.HasTake ? (int)b.TakeAxis : -1); if (seenAxes.Add(key)) list.Add(b); }
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

        public bool HasKeystone(KeystoneId id) => Boons.Any(b => b.Keystone == id);

        // aggregate give(+)/take(-) points per axis (keystones have no axis → skipped)
        public Dictionary<BoonAxis, int> Totals()
        {
            var t = Axes.All.ToDictionary(a => a, _ => 0);
            foreach (var b in Boons)
            {
                if (b.IsKeystone) continue;
                t[b.GiveAxis] += b.GiveAmt;
                if (b.HasGive2) t[b.Give2Axis] += b.Give2Amt;
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
            KeystoneNet.WriteOwned(p, st);   // publish owned keystones to the player's ZDO (server/other clients read this)
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
            float carry = t[BoonAxis.CarryWeight];                                   // flat axis bonus
            if (st.HasKeystone(KeystoneId.Regret)) carry += p.m_maxCarryWeight;      // Regret: +base = 2x carry weight
            active.m_addMaxCarryWeight        =  carry;

            // Dread (keystone): +30 effective Sneak skill, via the SE's skill-level slot
            bool dread = st.HasKeystone(KeystoneId.Dread);
            active.m_skillLevel         = dread ? Skills.SkillType.Sneak : Skills.SkillType.None;
            active.m_skillLevelModifier = dread ? 30f : 0f;

            // Wrath (keystone): +100% slash on outgoing attacks (2x), via the SE's per-type damage modifiers
            var pdm = default(HitData.DamageTypes);
            if (st.HasKeystone(KeystoneId.Wrath)) pdm.m_slash = 1f;
            active.m_percentigeDamageModifiers = pdm;
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
            int xp = BoonCfg.XpPerKill;
            if (BoonHud.Owns(KeystoneId.Sonder)) xp *= 3;   // Sonder: triple experience
            st.AddXp(xp);
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

    // ─── Legendary keystone networking (dedicated-server safe) ────────────────
    // Keystone ownership is written to each player's ZDO (readable by any client / the server). Grief wounds
    // are accumulated on the ENEMY's ZDO, so the enemy's owner (the server, typically) is authoritative for
    // both marking and the heal cap. Player-side effects (Grace, Conviction) run on the player's own client.
    internal static class KeystoneNet
    {
        static readonly int OwnedHash   = "pilgrim_keystones".GetStableHashCode();
        static readonly int GriefDmgHash = "pilgrim_grief_dmg".GetStableHashCode();

        // write the local player's owned-keystone bitmask to its ZDO (called whenever boons change)
        internal static void WriteOwned(Player p, BoonState st)
        {
            if (p == null || st == null || ZDOMan.instance == null) return;
            var zdo = ZDOMan.instance.GetZDO(p.GetZDOID());
            if (zdo == null) return;
            int mask = 0;
            foreach (var b in st.Boons) if (b.IsKeystone) mask |= 1 << (int)b.Keystone;
            zdo.Set(OwnedHash, mask);
        }

        internal static bool Has(Character c, KeystoneId id)
        {
            if (c == null || ZDOMan.instance == null) return false;
            return HasZ(c.GetZDOID(), id);
        }

        internal static bool HasZ(ZDOID id, KeystoneId k)   // read a keystone bit straight off a ZDOID (no instance needed)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            return zdo != null && (zdo.GetInt(OwnedHash) & (1 << (int)k)) != 0;
        }

        internal static float GriefDmg(Character c)
        {
            var zdo = ZDOMan.instance?.GetZDO(c.GetZDOID());
            return zdo != null ? zdo.GetFloat(GriefDmgHash) : 0f;
        }
        internal static void AddGriefDmg(Character c, float amt)
        {
            var zdo = ZDOMan.instance?.GetZDO(c.GetZDOID());
            if (zdo != null) zdo.Set(GriefDmgHash, zdo.GetFloat(GriefDmgHash) + amt);
        }
    }

    // ─── Legendary keystone effects ──────────────────────────────────────────
    // Grief — "You are the wound that never heals." Enemy ApplyDamage/Heal run on the enemy's OWNER (server on
    // a dedicated host). We accumulate the HP a Grief-holder removes onto the enemy's ZDO and cap its healing
    // at (maxHP − grief damage): damage from others still heals up to that ceiling, your wounds never close.
    [HarmonyPatch(typeof(Character), "ApplyDamage")]
    internal static class Grief_Track
    {
        static void Prefix(Character __instance, out float __state) => __state = __instance != null ? __instance.GetHealth() : 0f;
        static void Postfix(Character __instance, HitData hit, float __state)
        {
            if (!BoonCfg.Enabled || !BoonCfg.LegendaryEnabled(KeystoneId.Grief)) return;
            if (__instance == null || __instance is Player || hit == null) return;
            if (!(hit.GetAttacker() is Player atk) || !KeystoneNet.Has(atk, KeystoneId.Grief)) return;
            float dealt = __state - __instance.GetHealth();
            if (dealt > 0f) KeystoneNet.AddGriefDmg(__instance, dealt);
        }
    }

    [HarmonyPatch(typeof(Character), "Heal")]
    internal static class Grief_HealCap
    {
        static void Prefix(Character __instance, ref float hp)
        {
            if (!BoonCfg.Enabled || !BoonCfg.LegendaryEnabled(KeystoneId.Grief)) return;
            if (__instance == null || __instance is Player || hp <= 0f) return;
            float gd = KeystoneNet.GriefDmg(__instance);
            if (gd <= 0f) return;
            float room = (__instance.GetMaxHealth() - gd) - __instance.GetHealth();
            hp = room <= 0f ? 0f : Mathf.Min(hp, room);
        }
    }

    // Grace — "You are the gasp of a dying death." A mortal blow leaves you at 1 HP, once per minute.
    // Runs in ApplyDamage on the player's OWN client (owner), so it's MP-correct without any sync.
    internal static class Grace { internal static float LastProc = -999f; internal const float Cooldown = 60f; }

    [HarmonyPatch(typeof(Character), "ApplyDamage")]
    internal static class Grace_CheatDeath
    {
        static void Postfix(Character __instance)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (!BoonHud.Owns(KeystoneId.Grace) || Time.time - Grace.LastProc < Grace.Cooldown) return;
            if (__instance.GetHealth() > 0f) return;   // the blow wasn't lethal
            __instance.SetHealth(1f);
            Grace.LastProc = Time.time;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "With your dying breath");
        }
    }

    // Conviction — "You are the shield that never yields." No stagger accrues while blocking with a shield.
    // Stagger on the player runs on the player's own client, so a local check is MP-correct.
    [HarmonyPatch(typeof(Character), "AddStaggerDamage")]
    internal static class Conviction_NoStagger
    {
        static bool Prefix(Character __instance, ref bool __result)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return true;
            if (!BoonHud.Owns(KeystoneId.Conviction)) return true;
            if (!(__instance is Humanoid h) || !h.IsBlocking()) return true;
            var blk = Traverse.Create(h).Method("GetCurrentBlocker").GetValue<ItemDrop.ItemData>();
            if (blk == null || blk.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield) return true;
            __result = false;   // no stagger added
            return false;
        }
    }

    // Resent — "You lash out and don't remember why anymore." After blocking, the leftover (unblocked) damage is
    // dealt back to the attacker. BlockAttack runs on the blocker's own client (via RPC_Damage on the player's owner),
    // so this is MP-correct and the reflected hit is attributed to the player like any attack.
    [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
    internal static class Resent_Reflect
    {
        static void Postfix(Humanoid __instance, HitData hit, Character attacker)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (!BoonHud.Owns(KeystoneId.Resent) || attacker == null || hit == null || attacker == __instance) return;
            float leftover = hit.GetTotalDamage();     // what still gets through after the block reduction
            if (leftover <= 0f) return;
            var rh = new HitData { m_damage = hit.m_damage.Clone() };
            rh.m_point = attacker.transform.position;
            rh.m_dir = (attacker.transform.position - __instance.transform.position).normalized;
            rh.SetAttacker(__instance);
            attacker.Damage(rh);
        }
    }

    // Betrayal — "You let them put their trust in you." Knife backstabs deal 10x. Runs in RPC_Damage on the target's
    // OWNER (server for monsters); the attacker's Betrayal is read off their ZDO, so it's server-authoritative.
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class Betrayal_KnifeBackstab
    {
        static void Prefix(HitData hit)
        {
            if (!BoonCfg.Enabled || !BoonCfg.LegendaryEnabled(KeystoneId.Betrayal) || hit == null) return;
            if (hit.m_skill != Skills.SkillType.Knives || hit.m_backstabBonus <= 1f) return;
            if (KeystoneNet.HasZ(hit.m_attacker, KeystoneId.Betrayal)) hit.m_backstabBonus = 10f;
        }
    }

    // Panic — "Your hands tremble with intent." Bow draw reaches full charge 4x faster (25% of the time).
    // GetAttackDrawPercentage is computed on the player's own client → MP-correct.
    [HarmonyPatch(typeof(Humanoid), "GetAttackDrawPercentage")]
    internal static class Panic_FastDraw
    {
        static void Postfix(Humanoid __instance, ref float __result)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (__result <= 0f || !BoonHud.Owns(KeystoneId.Panic)) return;   // >0 only while drawing a bow
            __result = Mathf.Clamp01(__result * 4f);
        }
    }

    // Nostalgia — "You long for the fellowship of youth." Foods last 20% longer. EatFood runs on the player's own
    // client (m_foods is local) → MP-correct. We overfill the just-eaten food's timer to 1.2x its burn time.
    [HarmonyPatch(typeof(Player), "EatFood")]
    internal static class Nostalgia_FoodDuration
    {
        static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || !BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (!BoonHud.Owns(KeystoneId.Nostalgia) || item == null) return;
            var foods = Traverse.Create(__instance).Field("m_foods").GetValue<List<Player.Food>>();
            if (foods == null) return;
            foreach (var f in foods)
                if (f?.m_item != null && f.m_item.m_shared.m_name == item.m_shared.m_name)
                { f.m_time = f.m_item.m_shared.m_foodBurnTime * 1.2f; break; }
        }
    }

    // Avarice — "You are the hand in every pocket." When the local Avarice-holder picks a valuable, they get 3x.
    // Runs in Pickable.Interact on the PICKER's own client → MP-correct, and once per pickable (guarded by !m_picked).
    internal static class Avarice
    {
        static readonly HashSet<string> Valuables = new HashSet<string> { "Ruby", "Amber", "AmberPearl", "SilverNecklace", "Coins" };
        internal static bool IsValuable(string prefab) => prefab != null && Valuables.Contains(prefab);
    }

    [HarmonyPatch(typeof(Pickable), "Interact")]
    internal static class Avarice_Pickup
    {
        static void Prefix(Pickable __instance, out bool __state) =>
            __state = __instance != null && !Traverse.Create(__instance).Field("m_picked").GetValue<bool>(); // was it still unpicked?
        static void Postfix(Pickable __instance, Humanoid character, bool __state)
        {
            if (!__state || !BoonCfg.Enabled || __instance == null) return;                 // only a fresh pick
            if (character == null || character != Player.m_localPlayer) return;             // only this player's pickup
            if (!BoonHud.Owns(KeystoneId.Avarice)) return;
            var prefab = __instance.m_itemPrefab;
            if (prefab == null || !Avarice.IsValuable(prefab.name)) return;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) return;
            int extra = Mathf.Max(1, __instance.m_amount) * 2;                               // native drops 1x; add 2x on the ground → 3x total
            ItemDrop.DropItem(drop.m_itemData, extra, __instance.transform.position + Vector3.up * 0.4f, Quaternion.identity);
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
        internal static bool Owns(KeystoneId id) => State != null && State.HasKeystone(id);   // used by keystone patches
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
