# TODO

- **Goal ritual (build a tower)** — DM issues a ritual goal like "build a tower first"; mod checks for a nearby player-built structure that passes a height test (scan `Piece` instances near player, compute vertical bounding box from WearNTear/ZNetView cluster, check height >= threshold). Explore whether we can describe structures — piece count, bounding box dimensions, material tiers — to give the DM a picture of what exists near the player without being in-game. Use MCP `render_view` to let the DM see the build from above or side without being present. Gate progression rituals on structure inspection passing — e.g. no elder seek until a stone hall stands.

- **README ritual list scalability** — 21 rituals barely fits; move ritual list to a separate section, wiki page, or generated format

- **Track last ship driven** — add a minimap pin for the last ship driven, similar to the cart pin
- **Cart drag ritual** — ritual that temporarily lowers cart drag so it pulls more easily over terrain
- **Finish fermenting meads on sleep** — like the crop growth and tame flock sleep rituals, complete nearby fermenter progress when the player sleeps
- **Giant mode overexposure** — too bright/washed out on mountain terrain; investigate env/postprocessing overrides during giant ritual

- **Verify dungeon seeker list** — confirm `DungeonLocations` covers every enterable dungeon; use in-game `find <name> 5` to verify prefab names; check Deep North when content ships

- **Full YAML parameterization** — every hardcoded value (ship storage levels, cart costs, scale factors, carry bonuses, ritual durations, VFX prefab names, etc.) should be configurable via the YAML config so DMs and server admins can tune the mod without code changes
- **Remap ritual ingredients** — revisit which items trigger which rituals; current mapping may not feel thematic enough
- **Wolf ally ritual** — temporarily summon a wolf that fights for the player, then despawns after a duration
- **Thunder cracks** — use thunder SFX for dramatic ritual moments or as ambient omens (scheduled events, boss proximity, etc.)
- **More special weapon rituals** — expand the flaming sword pattern to other named/legendary weapons, or a ritual that temporarily grants a randomized mystery weapon
- **Deer stampede ritual** — spawn ~20 deer 100m behind the player, running past in the player's forward direction; any that survive and run out of view despawn after a delay
- **Burden ritual** — 1 minute of massively increased carry weight
- **Ritual skills** — a skill that levels through ritual use and unlocks improvements: longer duration, bigger radius, stronger effects, or additional secondary effects on certain rituals
- **Repair ritual** — repair all damaged build pieces within a radius around the campfire
