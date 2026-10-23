# Pilgrim · Boons — structure & roll logic

## 1. The roll (per card slot — a draw offers `draw_count` = 3 of these)

```
roll r ∈ [0,1)
│
├─ r < 0.20  (legendary_chance) ─────────▶ UNIQUE  (keystone)
│        pick uniformly from { enabled keystones, not owned, not already in this draw }
│        └─ pool empty? → fall through to COMMON/UNCOMMON
│
├─ r < 0.40  (+ rare_chance 0.20) ───────▶ RARE
│        numeric boon, forced rarity = Rare
│        = 2 GIVES + 1 TAKE   (give, give2, take all from the NUMERIC pool)
│
├─ r < 0.55  (+ resist_chance 0.15) ─────▶ RESISTANCE
│        give  = resist one damage type      ┐ both drawn from the 8
│        take  = weak to a different type     ┘ categorical resist axes
│        rarity = by magnitude (Common / Uncommon / Rare)
│
└─ else (0.45) ──────────────────────────▶ COMMON / UNCOMMON
         numeric boon, 1 GIVE + 1 TAKE
         rarity = by magnitude score (see §2)

then: dedup against the other slots (same keystone, or same give/take axis pair) → reroll that slot
```

**Magnitude roll (numeric give/take):** each is a uniform int **0–10**.
- same axis for give & take → collapse to one net gift of `give − take` (min 1).
- different axes → give ≥ 1; **take may exceed give** (a gamble — some boons net negative).
- **rarity score** = `give − 0.5·take + (pure ? 1.5 : 0)` → **≥ 5 = Uncommon**, else **Common**.
- a **general** (umbrella) gift is rarity-floored: it **can't roll Common** (always ≥ Uncommon).

## 2. Rarity tiers (D2R colors)

| Tier | Color | Source |
|---|---|---|
| Common | tan | low numeric roll |
| Uncommon | blue | higher numeric roll / any general |
| Rare | yellow | the Rare branch (2 gives) or a big resist roll |
| **Unique** | orange | keystones only |

## 3. Axis hierarchy — Domain ▸ general ▸ leaves

Numeric axes (give/take, 0–10 → % or flat):

```
CORE RESOURCES
  Health ───────── Health regen
  Stamina ──┬───── Stamina regen
            └───── Stamina use ★general          (reduces ALL costs below)
                     ├ attack  ├ run   ├ jump   ├ block
                     ├ dodge   ├ swim  ├ sneak   └ build (hoe/hammer)
  Eitr ─────────── Eitr regen

OFFENSE
  Damage ───────── Damage (all)

DEFENSE
  Defense ★general                               (bumps the 4 below together)
     ├ armor %      ├ parry      ├ stagger      └ fall damage
  (flat armor is a separate leaf, not under the general)

MOBILITY
  Movement ─┬───── Move speed
            └───── Swim speed
  Sneak ────────── Sneak

UTILITY
  Carry ────────── Carry weight
```

Categorical axes (resistance — give = resist, take = weak; levels, not %):

```
RESISTANCE (its own roll branch, paired give/take)
  blunt · slash · pierce · fire · frost · poison · lightning · spirit
  level ladder:  Slightly resistant ▸ Resistant ▸ Very resistant ▸ Immune
                 Slightly weak      ▸ Weak      ▸ Very weak
  (vanilla "best wins" — resistances don't linearly stack)
```

★ = **general**: rarity-floored, shown as ONE card line, broken out into components on the char sheet.

## 4. Unique keystones (orange tier, 12) — flip a switch, feel it constantly

| Keystone | Card (flowery) | Effect |
|---|---|---|
| Grief | You are the wound that never heals | Damage you inflict can't be healed |
| Grace | You are the gasp of a dying breath | Mortal blow → 1 HP, once / min |
| Conviction | You are the shield that never yields | No stagger while shield-blocking |
| Sonder | You are the wisdom of perspective | Triple XP |
| Dread | You are the premonition of danger | Sneak +30 |
| Avarice | You are the hand in every pocket | 3× on valuable pickups |
| Regret | You'll bear anything but the weight of what you did | 2× carry weight |
| Resent | You lash out and don't remember why anymore | Excess block force reflects to attacker |
| Wrath | Your anger shall not be mitigated | 2× slash damage |
| Betrayal | You let them put their trust in you | 10× knife backstab |
| Panic | Your hands tremble with intent | Bow draws in 25% the time |
| Nostalgia | You long for the fellowship of youth | Foods last 20% longer |

## 5. XP / leveling

```
per-kill XP = round( xp_per_kill · maxHP/xp_health_ref · star )      star = 1/2/3 (0/1/2-star)
cost L→L+1  = round( xp_curve_base · xp_curve_growth^L )              gentle exponential
```
Scales with enemy HP so you outgrow a biome by fighting tougher things, and the exponential cost
keeps time-per-level roughly regular toward ~level 100 on a clean run.
