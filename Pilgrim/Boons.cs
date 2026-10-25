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
        internal static float  XpHealthRef       => C.XpHealthRef;
        internal static int    DrawCount         => C.DrawCount;
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
        internal static float AttrW1 => C.AttrW1;
        internal static float AttrW2 => C.AttrW2;
        internal static float AttrW3 => C.AttrW3;
        internal static float AttrW4 => C.AttrW4;
        internal static float WeightGeneral  => C.WeightGeneral;
        internal static float WeightResist   => C.WeightResist;
        internal static float WeightDmgType  => C.WeightDmgType;
        internal static float WeightStamLeaf => C.WeightStamLeaf;
        internal static float WeightBase     => C.WeightBase;
        internal static float PyrrhicBaseFactor => C.PyrrhicBaseFactor;
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
            KeystoneId.Temerity => C.TemerityEnabled,
            KeystoneId.Contempt => C.ContemptEnabled,
            KeystoneId.Pyrrhic => C.PyrrhicEnabled,
            _ => true,
        };
        // xp to go from level L -> L+1 (gentle exponential so leveling stays regular as per-kill XP grows with biome)
        internal static int NeedForNext(int level) =>
            Mathf.Max(1, Mathf.RoundToInt(C.XpCurveBase * Mathf.Pow(C.XpCurveGrowth, level)));
    }

    // ---- legendaries: "keystones" (the Unique tier). Each is a named on/off effect with its own Harmony hook. ----
    internal enum KeystoneId { None = 0, Grief = 1, Grace = 2, Conviction = 3, Sonder = 4, Dread = 5, Avarice = 6, Regret = 7, Resent = 8, Wrath = 9, Betrayal = 10, Panic = 11, Nostalgia = 12, Temerity = 13, Contempt = 14, Pyrrhic = 15 }   // append new ids at the END (persisted)

    // Desc = flowery card text; Sheet = plain effect text for the character sheet.
    internal sealed class KeystoneDef { public KeystoneId Id; public string Name, Desc, Sheet; }

    internal static class Keystones
    {
        internal static readonly Dictionary<KeystoneId, KeystoneDef> Def = new()
        {
            [KeystoneId.Grief]      = new KeystoneDef { Id = KeystoneId.Grief,      Name = "Grief",      Desc = "You are the wound that never heals",   Sheet = "Damage you inflict cannot be healed" },
            [KeystoneId.Grace]      = new KeystoneDef { Id = KeystoneId.Grace,      Name = "Grace",      Desc = "You are the gasp of a dying breath",    Sheet = "A mortal blow leaves you at 1 HP, once per minute" },
            [KeystoneId.Conviction] = new KeystoneDef { Id = KeystoneId.Conviction, Name = "Conviction", Desc = "You are the shield that never yields", Sheet = "Cannot be staggered while blocking with a shield" },
            [KeystoneId.Sonder]  = new KeystoneDef { Id = KeystoneId.Sonder,  Name = "Sonder",  Desc = "You are the wisdom of perspective", Sheet = "Grants triple experience" },
            [KeystoneId.Dread]   = new KeystoneDef { Id = KeystoneId.Dread,   Name = "Dread",   Desc = "You are the premonition of danger",  Sheet = "Grants sneak +30" },
            [KeystoneId.Avarice] = new KeystoneDef { Id = KeystoneId.Avarice, Name = "Avarice", Desc = "You are the hand in every pocket",    Sheet = "Grants 3x treasure" },
            [KeystoneId.Regret]  = new KeystoneDef { Id = KeystoneId.Regret,  Name = "Regret",  Desc = "You'll bear anything but the weight of what you did", Sheet = "Grants 2x carry weight" },
            [KeystoneId.Resent]  = new KeystoneDef { Id = KeystoneId.Resent,  Name = "Resent",  Desc = "You lash out and don't remember why anymore", Sheet = "Excess block force is reflected to the attacker" },
            [KeystoneId.Wrath]    = new KeystoneDef { Id = KeystoneId.Wrath,    Name = "Wrath",    Desc = "Your anger shall not be mitigated",  Sheet = "Grants 2x slash damage" },
            [KeystoneId.Betrayal] = new KeystoneDef { Id = KeystoneId.Betrayal, Name = "Betrayal", Desc = "You let them put their trust in you", Sheet = "Grants 10x backstab damage with knives" },
            [KeystoneId.Panic]     = new KeystoneDef { Id = KeystoneId.Panic,     Name = "Panic",     Desc = "Your hands tremble with intent",      Sheet = "Bow draws in 25% the time" },
            [KeystoneId.Nostalgia] = new KeystoneDef { Id = KeystoneId.Nostalgia, Name = "Nostalgia", Desc = "You long for the fellowship of youth", Sheet = "Foods last 20% longer" },
            [KeystoneId.Temerity]  = new KeystoneDef { Id = KeystoneId.Temerity,  Name = "Temerity",  Desc = "You ignore the warnings of danger",     Sheet = "Grants 2x blunt damage" },
            [KeystoneId.Contempt]  = new KeystoneDef { Id = KeystoneId.Contempt,  Name = "Contempt",  Desc = "Your voice is hatred transcending diction", Sheet = "Grants 2x pierce damage" },
            [KeystoneId.Pyrrhic]   = new KeystoneDef { Id = KeystoneId.Pyrrhic,   Name = "Pyrrhic",   Desc = "You'll stand at the table while blood fills your shoes", Sheet = "Grants favorable boon rolls" },
        };
        internal static readonly KeystoneId[] All = Def.Keys.ToArray();
        internal static bool Available(KeystoneId id) => Def.ContainsKey(id) && BoonCfg.LegendaryEnabled(id);
    }

    // Append new axes at the END — the int value is persisted in saved boons, so existing indices must not shift.
    internal enum BoonAxis
    {
        Damage, AtkStamina, MoveSpeed, HpRegen, StamRegen, FlatArmor, RunStamina, JumpStamina,
        BlockStamina, DodgeStamina, SwimStamina, SneakStamina, Stagger, FallDamage,
        ArmorPct, Parry, EitrRegen, SwimSpeed, Stealth, CarryWeight,
        BuildStamina, StaminaUse,  // BuildStamina = hoe/hammer/cultivator; StaminaUse = GENERAL umbrella over all 8 leaves
        Defense,                   // GENERAL umbrella over armor% + parry + stagger + fall damage
        ResBlunt, ResSlash, ResPierce, ResFire, ResFrost, ResPoison, ResLightning, ResSpirit,  // per-type resistance (categorical)
        // per-type OUTGOING damage % — leaves under the Damage domain (m_percentigeDamageModifiers)
        DmgBlunt, DmgSlash, DmgPierce, DmgFire, DmgFrost, DmgPoison, DmgLightning, DmgSpirit,
        SkillXp,                   // vanilla skill-XP gain %, all skills (SE m_raiseSkill = All)
        // hook axes (no SE field — Harmony patches): offense dealt + mobility
        AttackSpeed,               // swing speed % (local: animator speed during attack)
        StaggerDealt,              // stagger inflicted on enemies % (owner: hit.m_staggerMultiplier)
        BackstabDealt,             // backstab bonus dealt % (owner: hit.m_backstabBonus)
        CrouchSpeed,               // crouch move speed % (local: Character.m_crouchSpeed)
        JumpHeight,                // jump force % (local: Character.Jump)
        Survival,                  // GENERAL umbrella over food + rested duration
        WoodCutting,               // woodcutting skill levels (local: Skills.GetSkillLevel)
        Pickaxes,                  // pickaxe/mining skill levels (local: Skills.GetSkillLevel)
        FoodDuration,              // food duration % (local: EatFood)
        RestedDuration,            // rested buff duration % (local: SE_Rested.UpdateTTL)
        BlockForce                 // shield block power % — Defense leaf (local: ItemData.GetBlockPower)
    }

    internal sealed class AxisDef
    {
        // Each axis carries a positive form (for a gift) and a negative form (for a cost), and each form
        // has BOTH an adjective (prefix slot) and a noun (suffix slot) — so a name can be built either way.
        public string GiveAdj, GiveNoun, TakeAdj, TakeNoun, Noun;
        public bool Percent;             // display/semantics as % (else flat)
        public bool Cost;                // "cost" axis: a GIFT reduces the stat (shown as −), a COST raises it (+)
        public bool General;             // umbrella axis over a domain's leaves → rarity-floored (can't roll Common)
        public bool Categorical;         // per-type resistance: amount maps to a DamageModifier category, not a %
        public HitData.DamageType DType; // the damage type for a categorical (resistance) axis
    }

    internal static class Axes
    {
        // Magnitudes are a unified int 0–10 roll per axis (see BoonRoller); unit is % unless flat.
        internal static readonly Dictionary<BoonAxis, AxisDef> Def = new()
        {
            [BoonAxis.Damage]      = new AxisDef { GiveAdj="Keen",      GiveNoun="the Edge",   TakeAdj="Dull",       TakeNoun="the Dull",   Noun="damage",        Percent=true, General=true },
            [BoonAxis.AtkStamina]  = new AxisDef { GiveAdj="Deft",      GiveNoun="the Flurry", TakeAdj="Taxing",     TakeNoun="the Exertion",Noun="attack stamina use",Percent=true, Cost=true },
            [BoonAxis.MoveSpeed]   = new AxisDef { GiveAdj="Swift",     GiveNoun="Haste",      TakeAdj="Sluggish",   TakeNoun="Sloth",      Noun="move speed",    Percent=true },
            [BoonAxis.HpRegen]     = new AxisDef { GiveAdj="Mending",   GiveNoun="Recovery",   TakeAdj="Festering",  TakeNoun="the Wound",  Noun="health regen",  Percent=true },
            [BoonAxis.StamRegen]   = new AxisDef { GiveAdj="Tireless",  GiveNoun="Endurance",  TakeAdj="Weary",      TakeNoun="Weariness",  Noun="stamina regen", Percent=true },
            [BoonAxis.FlatArmor]   = new AxisDef { GiveAdj="Warded",    GiveNoun="the Bulwark",TakeAdj="Brittle",    TakeNoun="Brittleness",Noun="armor",         Percent=false },
            [BoonAxis.RunStamina]  = new AxisDef { GiveAdj="Bounding",  GiveNoun="Vigor",      TakeAdj="Heavy",      TakeNoun="the Burden", Noun="run stamina use",   Percent=true, Cost=true },
            [BoonAxis.JumpStamina] = new AxisDef { GiveAdj="Lofty",     GiveNoun="the Spring", TakeAdj="Leaden",     TakeNoun="the Anchor", Noun="jump stamina use",  Percent=true, Cost=true },
            [BoonAxis.BlockStamina]= new AxisDef { GiveAdj="Steady",    GiveNoun="the Guard",  TakeAdj="Strained",   TakeNoun="the Strain", Noun="block stamina use", Percent=true, Cost=true },
            [BoonAxis.DodgeStamina]= new AxisDef { GiveAdj="Nimble",    GiveNoun="Evasion",    TakeAdj="Clumsy",     TakeNoun="Clumsiness", Noun="dodge stamina use", Percent=true, Cost=true },
            [BoonAxis.SwimStamina] = new AxisDef { GiveAdj="Buoyant",   GiveNoun="the Tide",   TakeAdj="Sinking",    TakeNoun="the Depths", Noun="swim stamina use",  Percent=true, Cost=true },
            [BoonAxis.SneakStamina]= new AxisDef { GiveAdj="Quiet",     GiveNoun="Stillness",  TakeAdj="Restless",   TakeNoun="Restlessness",Noun="sneak stamina use",Percent=true, Cost=true },
            [BoonAxis.Stagger]     = new AxisDef { GiveAdj="Unshaken",  GiveNoun="Poise",      TakeAdj="Faltering",  TakeNoun="the Falter", Noun="stagger",       Percent=true, Cost=true },
            [BoonAxis.FallDamage]  = new AxisDef { GiveAdj="Feathered", GiveNoun="the Feather",TakeAdj="Plummeting", TakeNoun="the Plummet",Noun="fall damage",   Percent=true, Cost=true },
            [BoonAxis.ArmorPct]    = new AxisDef { GiveAdj="Ironclad",  GiveNoun="the Aegis",  TakeAdj="Exposed",    TakeNoun="Exposure",   Noun="armor",         Percent=true },
            [BoonAxis.Parry]       = new AxisDef { GiveAdj="Deflecting",GiveNoun="the Parry",  TakeAdj="Open",       TakeNoun="the Opening",Noun="parry bonus",   Percent=true },
            [BoonAxis.EitrRegen]   = new AxisDef { GiveAdj="Attuned",   GiveNoun="the Flow",   TakeAdj="Dim",        TakeNoun="the Dimming",Noun="eitr regen",    Percent=true },
            [BoonAxis.SwimSpeed]   = new AxisDef { GiveAdj="Gliding",   GiveNoun="the Current",TakeAdj="Dragging",   TakeNoun="the Drag",   Noun="swim speed",    Percent=true },
            [BoonAxis.Stealth]     = new AxisDef { GiveAdj="Unseen",    GiveNoun="the Veil",   TakeAdj="Conspicuous",TakeNoun="the Glare",  Noun="sneak",         Percent=false },
            [BoonAxis.CarryWeight] = new AxisDef { GiveAdj="Mighty",    GiveNoun="the Ox",     TakeAdj="Overladen",  TakeNoun="the Load",   Noun="carry weight",  Percent=false },
            [BoonAxis.BuildStamina]= new AxisDef { GiveAdj="Industrious",GiveNoun="the Craft",TakeAdj="Toilsome",   TakeNoun="the Labor",  Noun="build stamina use",Percent=true, Cost=true },
            [BoonAxis.StaminaUse]  = new AxisDef { GiveAdj="Fluid",     GiveNoun="Grace",      TakeAdj="Labored",    TakeNoun="Toil",       Noun="stamina use",   Percent=true, Cost=true, General=true },
            [BoonAxis.Defense]     = new AxisDef { GiveAdj="Stalwart",  GiveNoun="the Wall",   TakeAdj="Vulnerable", TakeNoun="the Breach", Noun="defense",       Percent=true, General=true },
            // per-type resistance (categorical): give = resist that type, take = weak to it. A bet on incoming damage.
            [BoonAxis.ResBlunt]    = new AxisDef { GiveAdj="Padded",    GiveNoun="the Cushion",TakeAdj="Bruised",    TakeNoun="the Bruise", Noun="blunt",     Categorical=true, DType=HitData.DamageType.Blunt },
            [BoonAxis.ResSlash]    = new AxisDef { GiveAdj="Toughhide", GiveNoun="the Hide",   TakeAdj="Lacerated",  TakeNoun="the Gash",   Noun="slash",     Categorical=true, DType=HitData.DamageType.Slash },
            [BoonAxis.ResPierce]   = new AxisDef { GiveAdj="Plated",    GiveNoun="the Plate",  TakeAdj="Punctured",  TakeNoun="the Puncture",Noun="pierce",   Categorical=true, DType=HitData.DamageType.Pierce },
            [BoonAxis.ResFire]     = new AxisDef { GiveAdj="Fireproof", GiveNoun="the Ember",  TakeAdj="Scorched",   TakeNoun="the Pyre",   Noun="fire",      Categorical=true, DType=HitData.DamageType.Fire },
            [BoonAxis.ResFrost]    = new AxisDef { GiveAdj="Frostward", GiveNoun="the Thaw",   TakeAdj="Chilled",    TakeNoun="the Frost",  Noun="frost",     Categorical=true, DType=HitData.DamageType.Frost },
            [BoonAxis.ResPoison]   = new AxisDef { GiveAdj="Purified",  GiveNoun="the Antidote",TakeAdj="Envenomed", TakeNoun="the Venom",  Noun="poison",    Categorical=true, DType=HitData.DamageType.Poison },
            [BoonAxis.ResLightning]= new AxisDef { GiveAdj="Grounded",  GiveNoun="the Earth",  TakeAdj="Shocked",    TakeNoun="the Storm",  Noun="lightning", Categorical=true, DType=HitData.DamageType.Lightning },
            [BoonAxis.ResSpirit]   = new AxisDef { GiveAdj="Hallowed",  GiveNoun="the Ward",   TakeAdj="Haunted",    TakeNoun="the Specter",Noun="spirit",    Categorical=true, DType=HitData.DamageType.Spirit },
            // per-type OUTGOING damage % (leaves under Damage): feed m_percentigeDamageModifiers
            [BoonAxis.DmgBlunt]    = new AxisDef { GiveAdj="Crushing",  GiveNoun="the Maul",   TakeAdj="Soft",       TakeNoun="the Softness",Noun="blunt damage",    Percent=true, DType=HitData.DamageType.Blunt },
            [BoonAxis.DmgSlash]    = new AxisDef { GiveAdj="Honed",     GiveNoun="the Cut",    TakeAdj="Blunted",    TakeNoun="the Nick",   Noun="slash damage",    Percent=true, DType=HitData.DamageType.Slash },
            [BoonAxis.DmgPierce]   = new AxisDef { GiveAdj="Piercing",  GiveNoun="the Point",  TakeAdj="Bent",       TakeNoun="the Bend",   Noun="pierce damage",   Percent=true, DType=HitData.DamageType.Pierce },
            [BoonAxis.DmgFire]     = new AxisDef { GiveAdj="Blazing",   GiveNoun="the Flame",  TakeAdj="Smoldering", TakeNoun="the Cinder", Noun="fire damage",     Percent=true, DType=HitData.DamageType.Fire },
            [BoonAxis.DmgFrost]    = new AxisDef { GiveAdj="Biting",    GiveNoun="the Rime",   TakeAdj="Melting",    TakeNoun="the Slush",  Noun="frost damage",    Percent=true, DType=HitData.DamageType.Frost },
            [BoonAxis.DmgPoison]   = new AxisDef { GiveAdj="Virulent",  GiveNoun="the Blight", TakeAdj="Diluted",    TakeNoun="the Dilution",Noun="poison damage",  Percent=true, DType=HitData.DamageType.Poison },
            [BoonAxis.DmgLightning]= new AxisDef { GiveAdj="Galvanic",  GiveNoun="the Bolt",   TakeAdj="Faded",      TakeNoun="the Flicker",Noun="lightning damage",Percent=true, DType=HitData.DamageType.Lightning },
            [BoonAxis.DmgSpirit]   = new AxisDef { GiveAdj="Consecrated",GiveNoun="the Smite", TakeAdj="Profane",    TakeNoun="the Profane",Noun="spirit damage",   Percent=true, DType=HitData.DamageType.Spirit },
            [BoonAxis.SkillXp]     = new AxisDef { GiveAdj="Apt",       GiveNoun="the Prodigy",TakeAdj="Dull-witted",TakeNoun="the Dullard",Noun="skill experience",Percent=true },
            [BoonAxis.AttackSpeed] = new AxisDef { GiveAdj="Quick",     GiveNoun="the Tempo",  TakeAdj="Ponderous",  TakeNoun="the Lag",    Noun="attack speed",    Percent=true },
            [BoonAxis.StaggerDealt]= new AxisDef { GiveAdj="Jarring",   GiveNoun="the Jolt",   TakeAdj="Gentle",      TakeNoun="the Lull",   Noun="stagger dealt",   Percent=true },
            [BoonAxis.BackstabDealt]=new AxisDef { GiveAdj="Insidious", GiveNoun="the Ambush", TakeAdj="Forthright",  TakeNoun="the Fair Fight",Noun="backstab damage",Percent=true },
            [BoonAxis.CrouchSpeed] = new AxisDef { GiveAdj="Prowling",  GiveNoun="the Prowl",  TakeAdj="Creeping",    TakeNoun="the Crawl",  Noun="crouch speed",    Percent=true },
            [BoonAxis.JumpHeight]  = new AxisDef { GiveAdj="Leaping",   GiveNoun="the Leap",   TakeAdj="Earthbound",  TakeNoun="the Weight", Noun="jump height",     Percent=true },
            [BoonAxis.Survival]    = new AxisDef { GiveAdj="Hardy",     GiveNoun="the Hearth", TakeAdj="Frail",       TakeNoun="the Hunger", Noun="food & rest duration",Percent=true, General=true },
            [BoonAxis.WoodCutting] = new AxisDef { GiveAdj="Timberborn",GiveNoun="the Axe",    TakeAdj="Splintered",  TakeNoun="the Splinter",Noun="woodcutting",     Percent=false },
            [BoonAxis.Pickaxes]    = new AxisDef { GiveAdj="Delving",   GiveNoun="the Vein",   TakeAdj="Blunt-tipped",TakeNoun="the Rubble", Noun="mining",          Percent=false },
            [BoonAxis.FoodDuration]  = new AxisDef { GiveAdj="Sated",   GiveNoun="the Feast",  TakeAdj="Starved",     TakeNoun="the Famine", Noun="food duration",   Percent=true },
            [BoonAxis.RestedDuration]= new AxisDef { GiveAdj="Restful", GiveNoun="Repose",     TakeAdj="Restless",    TakeNoun="the Vigil",  Noun="rested duration", Percent=true },
            [BoonAxis.BlockForce]  = new AxisDef { GiveAdj="Immovable", GiveNoun="the Rampart",TakeAdj="Yielding",    TakeNoun="the Give",   Noun="block force",     Percent=true },
        };

        // the 8 per-type outgoing-damage axes → the DamageTypes field they feed
        internal static readonly BoonAxis[] DamageAxes =
        {
            BoonAxis.DmgBlunt, BoonAxis.DmgSlash, BoonAxis.DmgPierce, BoonAxis.DmgFire,
            BoonAxis.DmgFrost, BoonAxis.DmgPoison, BoonAxis.DmgLightning, BoonAxis.DmgSpirit,
        };
        internal static readonly BoonAxis[] All = (BoonAxis[])Enum.GetValues(typeof(BoonAxis));

        // the leaves each general umbrella feeds (used by Apply and by the character-sheet breakout)
        internal static readonly HashSet<BoonAxis> DefenseParts = new()
        { BoonAxis.ArmorPct, BoonAxis.Parry, BoonAxis.Stagger, BoonAxis.FallDamage, BoonAxis.BlockForce };

        // the 8 stamina-use leaves the StaminaUse general feeds (also a roll-weight bucket)
        internal static readonly HashSet<BoonAxis> StaminaLeaves = new()
        {
            BoonAxis.AtkStamina, BoonAxis.RunStamina, BoonAxis.JumpStamina, BoonAxis.BlockStamina,
            BoonAxis.DodgeStamina, BoonAxis.SwimStamina, BoonAxis.SneakStamina, BoonAxis.BuildStamina,
        };
        static readonly HashSet<BoonAxis> DamageLeafSet = new(DamageAxes);

        // roll-weight for an axis, by bucket (generals rare, resistances/leaves damped, baseline = 1)
        internal static float Weight(BoonAxis a)
        {
            var d = Def[a];
            if (d.General)                 return BoonCfg.WeightGeneral;
            if (d.Categorical)             return BoonCfg.WeightResist;
            if (DamageLeafSet.Contains(a)) return BoonCfg.WeightDmgType;
            if (StaminaLeaves.Contains(a)) return BoonCfg.WeightStamLeaf;
            return BoonCfg.WeightBase;
        }

        // baseline bucket = none of the special buckets (Pyrrhic shrinks this so rarer, stronger axes win more often)
        internal static bool IsBaseline(BoonAxis a)
        {
            var d = Def[a];
            return !d.General && !d.Categorical && !DamageLeafSet.Contains(a) && !StaminaLeaves.Contains(a);
        }

        // numeric give/take/give2 draw from here (EXCLUDES the categorical resistance axes — those only come
        // from the dedicated resistance branch, always paired resist/weak).
        internal static readonly BoonAxis[] NumericAxes = All.Where(a => !Def[a].Categorical).ToArray();

        // the 8 per-type resistance axes
        internal static readonly BoonAxis[] ResistAxes =
        {
            BoonAxis.ResBlunt, BoonAxis.ResSlash, BoonAxis.ResPierce, BoonAxis.ResFire,
            BoonAxis.ResFrost, BoonAxis.ResPoison, BoonAxis.ResLightning, BoonAxis.ResSpirit,
        };

        // net points → damage category (give/positive = resist ladder, take/negative = weak ladder)
        // Boons cap at Very resistant — Immune (full negation) is intentionally out of reach.
        internal static HitData.DamageModifier Category(int net) =>
            net >= 7  ? HitData.DamageModifier.VeryResistant
          : net >= 4  ? HitData.DamageModifier.Resistant
          : net >= 1  ? HitData.DamageModifier.SlightlyResistant
          : net <= -7 ? HitData.DamageModifier.VeryWeak
          : net <= -4 ? HitData.DamageModifier.Weak
          : net <= -1 ? HitData.DamageModifier.SlightlyWeak
          :             HitData.DamageModifier.Normal;

        // human text for a category net (positive = resist, negative = weak)
        internal static string CatText(int net) =>
            net >= 7 ? "Very resistant" : net >= 4 ? "Resistant" : net >= 1 ? "Slightly resistant"
          : net <= -7 ? "Very weak" : net <= -4 ? "Weak" : net <= -1 ? "Slightly weak" : "";
    }

    // ---- one axis on a rolled boon (Net > 0 = give, Net < 0 = take) -----------
    internal struct AxisRoll { public BoonAxis Axis; public int Net; }

    // ---- one rolled boon: a list of axis attributes (or a keystone) ----------
    internal sealed class Boon
    {
        public int Rarity;    // 0 common, 1 magic, 2 rare, 3 unique (keystone)
        public KeystoneId Keystone = KeystoneId.None;   // set → legendary keystone (no axis rolls)
        public readonly List<AxisRoll> Rolls = new List<AxisRoll>();   // summed per axis; empty for keystones

        public bool IsKeystone => Keystone != KeystoneId.None;
        public string Desc => IsKeystone ? Keystones.Def[Keystone].Desc : "";

        // Name: biggest gift's adjective + biggest cost's noun (or the gift's own noun if there's no cost).
        public string Name
        {
            get
            {
                if (IsKeystone) return Keystones.Def[Keystone].Name;
                AxisRoll give = default, take = default; bool hasG = false, hasT = false;
                foreach (var r in Rolls)
                {
                    if (r.Net > 0 && (!hasG || r.Net > give.Net)) { give = r; hasG = true; }
                    if (r.Net < 0 && (!hasT || r.Net < take.Net)) { take = r; hasT = true; }
                }
                string adj = hasG ? Axes.Def[give.Axis].GiveAdj : "Hollow";
                string suf = hasT ? Axes.Def[take.Axis].TakeNoun
                           : hasG ? Axes.Def[give.Axis].GiveNoun : "Nothing";
                return $"{adj} Boon of {suf}";
            }
        }

        // One display line for an axis roll. Give = green, take = red (caller colours by Net sign).
        public static string Line(AxisRoll r)
        {
            var d = Axes.Def[r.Axis];
            if (d.Categorical) return $"{Axes.CatText(r.Net)} to {d.Noun}";   // "Resistant to fire" / "Weak to frost"
            bool give = r.Net > 0; int mag = Math.Abs(r.Net);
            string sign = (give ^ d.Cost) ? "+" : "-";   // cost axis: a gift REDUCES the stat (shown −)
            string unit = d.Percent ? "%" : "";
            return $"{sign}{mag}{unit} {d.Noun}";
        }

        // serialization:  keystone,rarity,axis:net;axis:net;...
        public string Serialize() =>
            $"{(int)Keystone},{Rarity}," + string.Join(";", Rolls.Select(r => $"{(int)r.Axis}:{r.Net}"));

        public static Boon Deserialize(string s)
        {
            var p = s.Split(',');
            var b = new Boon { Keystone = (KeystoneId)int.Parse(p[0]), Rarity = int.Parse(p[1]) };
            if (p.Length > 2 && p[2].Length > 0)
                foreach (var chunk in p[2].Split(';'))
                {
                    if (chunk.Length == 0) continue;
                    var kv = chunk.Split(':');
                    b.Rolls.Add(new AxisRoll { Axis = (BoonAxis)int.Parse(kv[0]), Net = int.Parse(kv[1]) });
                }
            return b;
        }
    }

    // ---- the roll engine -----------------------------------------------------
    internal static class BoonRoller
    {
        static readonly System.Random Rng = new System.Random();

        static int Roll010() => (int)Math.Round(Rng.NextDouble() * 10.0);   // uniform 0–10

        // number of attributes, weighted (default 40/30/20/10 for 1/2/3/4)
        static int RollCount()
        {
            float w1 = BoonCfg.AttrW1, w2 = BoonCfg.AttrW2, w3 = BoonCfg.AttrW3, w4 = BoonCfg.AttrW4;
            float tot = w1 + w2 + w3 + w4;
            if (tot <= 0f) return 1;
            double r = Rng.NextDouble() * tot;
            if (r < w1) return 1; r -= w1;
            if (r < w2) return 2; r -= w2;
            if (r < w3) return 3;
            return 4;
        }

        // A numeric boon: roll a count, attribute 1 is a give, the rest 50/50; roll axis + value per
        // attribute; sum by axis; rarity = max(count tier, value tier).
        // Pyrrhic (keystone): shrink the baseline bucket so generals/resist/damage/leaf axes win more often → better rolls
        static bool PyrrhicActive() =>
            BoonCfg.LegendaryEnabled(KeystoneId.Pyrrhic) && BoonHud.State != null && BoonHud.State.HasKeystone(KeystoneId.Pyrrhic);

        static float WeightOf(BoonAxis a, bool pyr)
        {
            float w = Axes.Weight(a);
            if (pyr && Axes.IsBaseline(a)) w *= BoonCfg.PyrrhicBaseFactor;
            return w;
        }

        // weighted axis pick (bucket weights: generals rare, resistances/leaves damped)
        static BoonAxis PickAxis()
        {
            bool pyr = PyrrhicActive();
            float tot = 0f;
            foreach (var a in Axes.All) tot += WeightOf(a, pyr);
            if (tot <= 0f) return Axes.All[Rng.Next(Axes.All.Length)];
            double r = Rng.NextDouble() * tot;
            foreach (var a in Axes.All) { r -= WeightOf(a, pyr); if (r < 0) return a; }
            return Axes.All[Axes.All.Length - 1];
        }

        static Boon RollNumeric()
        {
            int count = RollCount();
            var net = new Dictionary<BoonAxis, int>();
            for (int i = 0; i < count; i++)
            {
                bool give = i == 0 || Rng.NextDouble() < 0.5;
                var axis = PickAxis();
                int val = Roll010();
                net.TryGetValue(axis, out int cur);
                net[axis] = cur + (give ? val : -val);
            }
            var b = new Boon();
            foreach (var kv in net) if (kv.Value != 0) b.Rolls.Add(new AxisRoll { Axis = kv.Key, Net = kv.Value });
            // single-axis boons never net ≤ 0 (clamp the gift to beat the cost)
            if (b.Rolls.Count == 1 && b.Rolls[0].Net < 1) b.Rolls[0] = new AxisRoll { Axis = b.Rolls[0].Axis, Net = 1 };
            if (b.Rolls.Count == 0) b.Rolls.Add(new AxisRoll { Axis = PickAxis(), Net = 1 });
            b.Rarity = RarityOf(b);
            return b;
        }

        // Rarity = max(count tier, value tier), floored to Rare if any general attribute is present.
        // Count tier: 1–2 Common, 3 Magic, 4 Rare.  Value tier from (gives − takes) + (Σgive − Σtake)/4.
        // A general (give OR take) signals generality itself → always at least Rare.
        static int RarityOf(Boon b)
        {
            int count = b.Rolls.Count;
            int countTier = count <= 2 ? 0 : count == 3 ? 1 : 2;
            int g = 0, t = 0, sumG = 0, sumT = 0;
            foreach (var r in b.Rolls)
                if (r.Net > 0) { g++; sumG += r.Net; } else { t++; sumT += -r.Net; }
            float vs = (g - t) + (sumG - sumT) / 4f;
            int valueTier = vs >= 6f ? 2 : vs >= 3f ? 1 : 0;
            int rarity = Math.Max(countTier, valueTier);
            if (b.Rolls.Any(r => Axes.Def[r.Axis].General)) rarity = Math.Max(rarity, 2);   // any general → Rare
            return rarity;
        }

        // A keystone (Unique) from the pool: enabled, not owned, not already offered this draw.
        static Boon RollKeystone(HashSet<KeystoneId> owned, HashSet<KeystoneId> offered)
        {
            var pool = Keystones.All.Where(k => Keystones.Available(k) && !owned.Contains(k) && !offered.Contains(k)).ToArray();
            if (pool.Length == 0) return null;
            return new Boon { Keystone = pool[Rng.Next(pool.Length)], Rarity = 3 };
        }

        static string Signature(Boon b) => string.Join(";", b.Rolls.Select(r => (int)r.Axis).OrderBy(x => x));

        // Each card: LegendaryChance for a keystone, else a numeric boon. Dedup across the draw.
        internal static List<Boon> Draw()
        {
            var list = new List<Boon>();
            var seen = new HashSet<string>();
            var offeredKeys = new HashSet<KeystoneId>();
            var owned = BoonHud.State != null
                ? new HashSet<KeystoneId>(BoonHud.State.Boons.Where(b => b.IsKeystone).Select(b => b.Keystone))
                : new HashSet<KeystoneId>();
            int guard = 0;
            while (list.Count < BoonCfg.DrawCount && guard++ < 400)
            {
                Boon b = null;
                if (Rng.NextDouble() < BoonCfg.LegendaryChance) b = RollKeystone(owned, offeredKeys);
                if (b == null) b = RollNumeric();

                if (b.IsKeystone) { if (offeredKeys.Add(b.Keystone)) list.Add(b); }
                else { if (seen.Add(Signature(b))) list.Add(b); }
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
                    // boons are '|'-separated fields from index 3 on (';' is used WITHIN a boon for its axis rolls)
                    for (int i = 3; i < head.Length; i++)
                        if (head[i].Length > 0) st.Boons.Add(Boon.Deserialize(head[i]));
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"[Pilgrim] boon load failed: {e.Message}"); }
            }
            return st;
        }

        public void Save(Player p)
        {
            if (p == null || p.m_customData == null) return;
            // Level|Xp|Pending|boon|boon|...  — boons separated by '|' (never emitted by Boon.Serialize)
            var parts = new List<string> { Level.ToString(), Xp.ToString(), Pending.ToString() };
            parts.AddRange(Boons.Select(b => b.Serialize()));
            p.m_customData[Key] = string.Join("|", parts);
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
                foreach (var r in b.Rolls) t[r.Axis] += r.Net;
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

        // hook-axis caches the Harmony patches read (updated each Apply; local player only)
        internal static float AtkSpeedMul = 1f;   // 1 + attack-speed% (animator speed during attack)
        internal static float JumpMul     = 1f;   // 1 + jump-height%  (scales m_jumpForce)
        internal static float FoodMul     = 1f;   // 1 + (food + Survival general)%
        internal static float RestedMul   = 1f;   // 1 + (rested + Survival general)%
        internal static float BlockForceMul = 1f; // 1 + (block-force + Defense general)%
        internal static int   WoodLvl     = 0;    // woodcutting skill levels added
        internal static int   PickLvl     = 0;    // pickaxe/mining skill levels added
        static float _baseCrouch = -1f;           // vanilla m_crouchSpeed, captured fresh on spawn

        // capture the untouched crouch speed before any boon modifies it (once per session — the prefab base is constant)
        internal static void CaptureBase(Player p) { if (p != null && _baseCrouch < 0f) _baseCrouch = p.m_crouchSpeed; }

        // called on each fresh spawn: capture base at the guaranteed-clean moment, and reset the attack-speed flag
        // (it self-heals on the next tick anyway, but an explicit reset keeps the belief clean across a reload)
        internal static void OnSpawn(Player p) { CaptureBase(p); Boon_AttackSpeed.ResetFast(); }

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
            active.m_speedModifier            =  t[BoonAxis.MoveSpeed]     / 100f;
            active.m_healthRegenMultiplier    = 1f + t[BoonAxis.HpRegen]   / 100f;
            active.m_staminaRegenMultiplier   = 1f + t[BoonAxis.StamRegen] / 100f;
            active.m_addArmor                 =  t[BoonAxis.FlatArmor];
            active.m_modifyAttackSkill        = Skills.SkillType.All;

            // stamina-use axes (cost: negative = cheaper). The GENERAL "stamina use" adds to every leaf.
            float gStam = t[BoonAxis.StaminaUse];
            active.m_attackStaminaUseModifier   = -((t[BoonAxis.AtkStamina]   + gStam) / 100f);
            active.m_runStaminaUseModifier      = -((t[BoonAxis.RunStamina]   + gStam) / 100f);
            active.m_jumpStaminaUseModifier     = -((t[BoonAxis.JumpStamina]  + gStam) / 100f);
            active.m_blockStaminaUseModifier    = -((t[BoonAxis.BlockStamina] + gStam) / 100f);
            active.m_dodgeStaminaUseModifier    = -((t[BoonAxis.DodgeStamina] + gStam) / 100f);
            active.m_swimStaminaUseModifier     = -((t[BoonAxis.SwimStamina]  + gStam) / 100f);
            active.m_sneakStaminaUseModifier    = -((t[BoonAxis.SneakStamina] + gStam) / 100f);
            active.m_homeItemStaminaUseModifier = -((t[BoonAxis.BuildStamina] + gStam) / 100f);
            // defense: the GENERAL "Defense" umbrella adds to armor% + parry (+) and stagger + fall (−)
            float gDef = t[BoonAxis.Defense];
            active.m_staggerModifier          = -((t[BoonAxis.Stagger]    + gDef) / 100f);  // negative = less stagger taken
            active.m_fallDamageModifier       = -((t[BoonAxis.FallDamage] + gDef) / 100f);  // negative = less fall damage
            active.m_armorMultiplier          =  (t[BoonAxis.ArmorPct]    + gDef) / 100f;   // neutral 0 → ×(1+x)
            active.m_timedBlockBonus          =  (t[BoonAxis.Parry]       + gDef) / 100f;   // neutral 0 → parry ×(1+x)
            BlockForceMul                     = 1f + (t[BoonAxis.BlockForce] + gDef) / 100f;  // shield block power (hook: GetBlockPower)
            // utility
            active.m_eitrRegenMultiplier      = 1f + t[BoonAxis.EitrRegen] / 100f;
            active.m_swimSpeedModifier        =  t[BoonAxis.SwimSpeed]     / 100f;
            active.m_stealthModifier          = 0f;                                  // sneak is now a skill axis (below)
            float carry = t[BoonAxis.CarryWeight];                                   // flat axis bonus
            if (st.HasKeystone(KeystoneId.Regret)) carry += p.m_maxCarryWeight;      // Regret: +base = 2x carry weight
            active.m_addMaxCarryWeight        =  carry;

            // Sneak skill: the Stealth axis adds flat Sneak skill levels; Dread (keystone) adds +30 more.
            // SE_Stats has a single skill slot — both target Sneak, so they sum cleanly.
            float sneakSkill = t[BoonAxis.Stealth];
            if (st.HasKeystone(KeystoneId.Dread)) sneakSkill += 30f;
            active.m_skillLevel         = sneakSkill != 0f ? Skills.SkillType.Sneak : Skills.SkillType.None;
            active.m_skillLevelModifier = sneakSkill;

            // vanilla skill-XP gain (all skills): SE m_raiseSkillModifier is a multiplier on skill gain
            float sx = t[BoonAxis.SkillXp];
            active.m_raiseSkill         = sx != 0f ? Skills.SkillType.All : Skills.SkillType.None;
            active.m_raiseSkillModifier = 1f + sx / 100f;

            // per-type OUTGOING damage % via m_percentigeDamageModifiers (1f = +100%).
            // Damage-type leaves + Wrath (keystone, +100% slash) fold into the same struct.
            var pdm = default(HitData.DamageTypes);
            foreach (var da in Axes.DamageAxes)
            {
                float f = t[da] / 100f;
                if (f == 0f) continue;
                switch (Axes.Def[da].DType)
                {
                    case HitData.DamageType.Blunt:     pdm.m_blunt     += f; break;
                    case HitData.DamageType.Slash:     pdm.m_slash     += f; break;
                    case HitData.DamageType.Pierce:    pdm.m_pierce    += f; break;
                    case HitData.DamageType.Fire:      pdm.m_fire      += f; break;
                    case HitData.DamageType.Frost:     pdm.m_frost     += f; break;
                    case HitData.DamageType.Poison:    pdm.m_poison    += f; break;
                    case HitData.DamageType.Lightning: pdm.m_lightning += f; break;
                    case HitData.DamageType.Spirit:    pdm.m_spirit    += f; break;
                }
            }
            if (st.HasKeystone(KeystoneId.Wrath))    pdm.m_slash += 1f;   // Wrath: +100% slash (2x)
            if (st.HasKeystone(KeystoneId.Temerity)) pdm.m_blunt  += 1f;   // Temerity: +100% blunt (2x)
            if (st.HasKeystone(KeystoneId.Contempt)) pdm.m_pierce += 1f;   // Contempt: +100% pierce (2x)
            active.m_percentigeDamageModifiers = pdm;

            // per-type resistance (categorical): net points per type → DamageModifier category
            var mods = new List<HitData.DamageModPair>();
            foreach (var ra in Axes.ResistAxes)
            {
                var cat = Axes.Category(t[ra]);
                if (cat != HitData.DamageModifier.Normal)
                    mods.Add(new HitData.DamageModPair { m_type = Axes.Def[ra].DType, m_modifier = cat });
            }
            active.m_mods = mods;

            // ── hook axes (no SE field) ──
            // local: attack-speed + jump caches read by the Harmony patches; crouch speed sets the field directly
            AtkSpeedMul = 1f + t[BoonAxis.AttackSpeed] / 100f;
            JumpMul     = 1f + t[BoonAxis.JumpHeight]  / 100f;
            float gSurv = t[BoonAxis.Survival];                             // general adds to both leaves
            FoodMul     = 1f + (t[BoonAxis.FoodDuration]   + gSurv) / 100f;
            RestedMul   = 1f + (t[BoonAxis.RestedDuration] + gSurv) / 100f;
            WoodLvl     = t[BoonAxis.WoodCutting];
            PickLvl     = t[BoonAxis.Pickaxes];
            CaptureBase(p);
            if (_baseCrouch > 0f) p.m_crouchSpeed = _baseCrouch * (1f + t[BoonAxis.CrouchSpeed] / 100f);
            // enemy-side: publish stagger-dealt + backstab-dealt onto the player's ZDO (owner reads it in RPC_Damage)
            KeystoneNet.WriteOffense(p, t[BoonAxis.StaggerDealt], t[BoonAxis.BackstabDealt]);
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
            // scale by enemy max health (vs a reference) and star level (×1 base, ×2 one-star, ×3 two-star)
            float href = Mathf.Max(1f, BoonCfg.XpHealthRef);
            int star = Mathf.Max(1, __instance.GetLevel());
            int xp = Mathf.Max(1, Mathf.RoundToInt(BoonCfg.XpPerKill * (__instance.GetMaxHealth() / href) * star));
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
            BoonStats.OnSpawn(__instance);                   // capture base crouch at the fresh moment; reset self-healing flags
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
        static readonly int StaggerDealtHash  = "pilgrim_stagger_dealt".GetStableHashCode();
        static readonly int BackstabDealtHash = "pilgrim_backstab_dealt".GetStableHashCode();

        // publish the local player's offense-dealt %s to its ZDO (read by the enemy owner in RPC_Damage)
        internal static void WriteOffense(Player p, int staggerPct, int backstabPct)
        {
            if (p == null || ZDOMan.instance == null) return;
            var zdo = ZDOMan.instance.GetZDO(p.GetZDOID());
            if (zdo == null) return;
            zdo.Set(StaggerDealtHash, staggerPct);
            zdo.Set(BackstabDealtHash, backstabPct);
        }
        static int OffenseZ(ZDOID id, int hash)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            return zdo != null ? zdo.GetInt(hash) : 0;
        }
        internal static int StaggerDealtZ(ZDOID id)  => OffenseZ(id, StaggerDealtHash);
        internal static int BackstabDealtZ(ZDOID id) => OffenseZ(id, BackstabDealtHash);

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

    // Resent — "You lash out and don't remember why anymore." When your block force EXCEEDS the incoming hit, the
    // surplus (blockForce − hit) is dealt back to the attacker. Prefix (before BlockAttack reduces the hit) so we see
    // the incoming blockable damage. BlockAttack runs on the blocker's own client → MP-correct.
    [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
    internal static class Resent_Reflect
    {
        static void Prefix(Humanoid __instance, HitData hit, Character attacker)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (!BoonHud.Owns(KeystoneId.Resent) || attacker == null || hit == null || attacker == __instance) return;
            var blocker = Traverse.Create(__instance).Method("GetCurrentBlocker").GetValue<ItemDrop.ItemData>();
            if (blocker == null) return;
            float blockForce = blocker.GetBlockPower(__instance.GetSkillFactor(Skills.SkillType.Blocking));
            float excess = blockForce - hit.GetTotalBlockableDamage();   // how much your block overshot the hit
            if (excess <= 0f) return;                                    // block didn't exceed the hit → nothing reflected
            var rh = new HitData();
            rh.m_damage.m_blunt = excess;
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

    // ─── Hook axes (no SE field) ──────────────────────────────────────────────
    // Offense dealt (StaggerDealt, BackstabDealt): RPC_Damage runs on the TARGET's owner (server for monsters).
    // The attacker's published %s are read off their ZDO, so it's owner-authoritative like Grief/Betrayal.
    // Runs after Betrayal's prefix may have set m_backstabBonus=10, so the axis scales whatever bonus stands.
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    internal static class Boon_OffenseDealt
    {
        static void Prefix(HitData hit)
        {
            if (!BoonCfg.Enabled || hit == null || hit.m_attacker == ZDOID.None) return;
            int sd = KeystoneNet.StaggerDealtZ(hit.m_attacker);
            if (sd != 0) hit.m_staggerMultiplier *= 1f + sd / 100f;
            int bd = KeystoneNet.BackstabDealtZ(hit.m_attacker);
            if (bd != 0 && hit.m_backstabBonus > 1f) hit.m_backstabBonus *= 1f + bd / 100f;
        }
    }

    // Jump height (JumpHeight): scale the local player's jump force for the duration of the Jump call.
    // Jump runs on the controlling client and the resulting velocity/position syncs → MP-correct.
    [HarmonyPatch(typeof(Character), "Jump")]
    internal static class Boon_JumpHeight
    {
        static void Prefix(Character __instance, out float __state)
        {
            __state = 0f;
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            if (BoonStats.JumpMul == 1f) return;
            __state = __instance.m_jumpForce;              // stash the real value
            __instance.m_jumpForce *= BoonStats.JumpMul;
        }
        static void Postfix(Character __instance, float __state)
        {
            if (__state > 0f && __instance != null) __instance.m_jumpForce = __state;   // restore
        }
    }

    // Attack speed (AttackSpeed): drive the local player's animator speed while an attack is in progress.
    // ZSyncAnimation.SetSpeed syncs the speed via ZDO, so other clients see the faster swing too → MP-correct.
    [HarmonyPatch(typeof(Humanoid), "UpdateAttack")]
    internal static class Boon_AttackSpeed
    {
        static bool _fast;
        internal static void ResetFast() => _fast = false;
        static void Postfix(Humanoid __instance)
        {
            if (!BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer) return;
            bool want = BoonStats.AtkSpeedMul != 1f && __instance.InAttack();
            if (want == _fast) return;
            var zanim = Traverse.Create(__instance).Field("m_zanim").GetValue<ZSyncAnimation>();
            if (zanim == null) return;
            zanim.SetSpeed(want ? BoonStats.AtkSpeedMul : 1f);
            _fast = want;
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

    // Food duration — the Survival axis (food + rest %) and Nostalgia keystone (foods last 20% longer) both extend the
    // just-eaten food's timer. EatFood runs on the player's own client (m_foods is local) → MP-correct.
    [HarmonyPatch(typeof(Player), "EatFood")]
    internal static class Boon_FoodDuration
    {
        static void Postfix(Player __instance, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || !BoonCfg.Enabled || __instance == null || __instance != Player.m_localPlayer || item == null) return;
            float mult = BoonStats.FoodMul;                                   // Food duration leaf (+ Survival general)
            if (BoonHud.Owns(KeystoneId.Nostalgia)) mult *= 1.2f;            // Nostalgia keystone
            if (mult == 1f) return;
            var foods = Traverse.Create(__instance).Field("m_foods").GetValue<List<Player.Food>>();
            if (foods == null) return;
            foreach (var f in foods)
                if (f?.m_item != null && f.m_item.m_shared.m_name == item.m_shared.m_name)
                { f.m_time = f.m_item.m_shared.m_foodBurnTime * mult; break; }
        }
    }

    // Rested duration — the Survival axis scales the rested buff's TTL. UpdateTTL sets m_ttl (and zeroes m_time) only
    // when it freshly (re)computes the duration; we scale that fresh value. SE_Rested is a local player buff → MP-correct.
    [HarmonyPatch(typeof(SE_Rested), "UpdateTTL")]
    internal static class Boon_RestedDuration
    {
        static void Postfix(SE_Rested __instance)
        {
            if (!BoonCfg.Enabled || BoonStats.RestedMul == 1f || __instance == null) return;
            if (!(__instance.m_character is Player pl) || pl != Player.m_localPlayer) return;
            if (Traverse.Create(__instance).Field("m_time").GetValue<float>() != 0f) return;   // only when UpdateTTL freshly set m_ttl (it zeroes m_time)
            float baseTtl = __instance.m_baseTTL + (pl.GetComfortLevel() - 1) * __instance.m_TTLPerComfortLevel;
            __instance.m_ttl = baseTtl * BoonStats.RestedMul;
        }
    }

    // Block force (BlockForce axis + Defense general): scale a shield's block power. GetBlockPower is the single source
    // feeding BlockAttack, stagger, and tooltips. Block is resolved on the blocker's own client, and only the local
    // player's shield is queried for the local player's blocks on this client → scaling here is MP-correct.
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetBlockPower), new[] { typeof(int), typeof(float) })]
    internal static class Boon_BlockForce
    {
        static void Postfix(ref float __result)
        {
            if (!BoonCfg.Enabled || BoonStats.BlockForceMul == 1f || Player.m_localPlayer == null) return;
            __result *= BoonStats.BlockForceMul;
        }
    }

    // Per-skill level — WoodCutting + Pickaxes axes add flat skill levels (same idea as Sneak, but our single SE only
    // has one skill slot, so these ride a GetSkillLevel patch instead). Local player's Skills only → MP-correct.
    [HarmonyPatch(typeof(Skills), "GetSkillLevel")]
    internal static class Boon_GatherSkills
    {
        static void Postfix(Skills __instance, Skills.SkillType skillType, ref float __result)
        {
            if (!BoonCfg.Enabled) return;
            var pl = Traverse.Create(__instance).Field("m_player").GetValue<Player>();
            if (pl == null || pl != Player.m_localPlayer) return;
            int add = skillType == Skills.SkillType.WoodCutting ? BoonStats.WoodLvl
                    : skillType == Skills.SkillType.Pickaxes   ? BoonStats.PickLvl : 0;
            if (add != 0) __result = Mathf.Max(0f, __result + add);
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
