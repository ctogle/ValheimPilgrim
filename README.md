# Pilgrim

A ritual-based progression mod for Valheim. Throw items into a burning campfire to unlock and perform ancient rituals — each one guiding, transforming, or empowering the pilgrim on their journey.

---

## Rituals

Rituals are discovered naturally: pick up an offering item for the first time and the ritual is revealed. Once known, stand near a lit campfire and throw the item in (default: `Q`). The campfire erupts with ritual fire and the effect takes hold.

The campfire menu shows how many rituals you've unlocked out of the total.

| Offering | Ritual | Effect |
|---|---|---|
| Raw Meat | **Seek the next altar** | Marks the nearest undiscovered Forsaken altar on your map |
| Mushroom | **Restore your power** | Immediately resets your guardian power cooldown |
| Dandelion | **Seek your bed** | Marks your spawn point on the map |
| Feathers | **Fall without fear** | Applies the Feather Fall status effect |
| Coins | **Seek a merchant** | Marks the nearest Haldor or Hildir on your map |
| Any Trophy | **Seek the nearest dungeon** | Marks the nearest undiscovered dungeon on your map |
| Greydwarf Eye | **Clear the skies** | Forces clear weather for 15 minutes |
| Stone | **Walk on water** | Lets you walk on the surface of water for 60 seconds |
| Ancient Seed | **Bless your crops** | Fully grows all nearby crops overnight |
| Flint | **Seek a fellow pilgrim** | Marks a random connected player on your map |
| Resin | **Kindle nearby fires** | Lights all campfires and torches within range |
| Bone Fragments | **Tame the flock** | Tames all nearby tameable creatures overnight |
| Ymir Flesh | **Become the mountain** | Transforms you into a giant for 60 seconds |

### The Giant

Ymir Flesh is the rarest and most dramatic ritual. For 60 seconds:

- You grow to **3× your normal size** (gradually over the effect window)
- Your **fists become weapons** — 200 blunt/chop/pickaxe damage, high tool tier; you can punch trees and rocks apart
- **Walk 1.5×, jog/sprint 2.5×, jump 1.2×** faster and higher
- **Carry weight +600**
- **Incoming damage reduced to 10%** — you are the mountain
- The sky shifts to **GoldenAscent**

On expiry you return to mortal scale. The effect does not stack.

---

## Trophy Shrines

Place a boss trophy on an item stand and interact with it (`Shift+E`) to claim that boss's guardian power — without consuming the trophy. The shrine remains. Guardian powers claimed this way behave identically to the vanilla altar system.

Supported: Eikthyr, The Elder, Bonemass, Moder, Yagluth, The Seeker Queen, Fader.

---

## Cart Upgrades

Reinforce a cart at the cart itself (`Shift+E`) to expand its inventory and change its appearance. Four upgrade tiers, each requiring materials from the next biome.

| Tier | Materials | Inventory |
|---|---|---|
| Base | — | 3 rows |
| Bronze | 10 Fine Wood + 20 Bronze Nails | 4 rows |
| Iron | 10 Elder Bark + 20 Iron Nails | 5 rows |
| Silver | 5 Silver + 5 Wolf Pelt | 6 rows |
| Black Metal | 5 Black Metal + 3 Lox Pelt | 7 rows |

Cart tint updates to reflect the upgrade tier. Upgrade materials drop on destruction.

---

## Configuration

Settings are stored in `BepInEx/config/Pilgrim.yaml` and hot-reload on save (no restart needed). Each ritual can be enabled/disabled, and its offering item, effect duration, hover text, and trigger message can be customized.

---

## Commands

| Command | Description |
|---|---|
| `ath_env` | Show current environment and time |
| `ath_learn <key\|all>` | Unlock a ritual by key |
| `ath_forget <key\|all>` | Remove a ritual from your knowledge |
| `ath_giant` | Trigger the giant ritual directly |
| `ath_trophies` | List known trophy-to-power mappings |
| `ath_cart upgrade` | Upgrade the nearest cart |
| `ath_birdparams` | Show current bird flock parameters |

---

## Requirements

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [YamlDotNet](https://thunderstore.io/c/valheim/p/ValheimModding/YamlDotNet/)
- Expand World (optional, required for custom environments like GoldenAscent)

---

## Multiplayer

Rituals are performed per-player. The giant scale is synced to all clients via ZDO. Cart upgrades sync via ZDO and apply on load for all players. Water walk, feather fall, and speed effects are local to the casting player.
