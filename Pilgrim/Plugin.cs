using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace EnvReporter
{
    [BepInPlugin("com.ctogle.pilgrim", "Pilgrim", "0.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static BepInEx.Logging.ManualLogSource Log = null!;
        internal static EnvScheduler?  Scheduler;
        internal static SE_GuidingWind? GuidingWindSE;
        internal static SE_WaterWalk?   WaterWalkSE;
        internal static SE_Giant?       GiantSE;
        internal static SE_FlamingSword? FlamingSwordSE;

        // ── Config ──────────────────────────────────────────────────────────
        internal static PilgrimConfig Cfg = PilgrimConfig.Default();
        static bool _cfgDirty = false;
        static FileSystemWatcher? _cfgWatcher;

        internal static string ConfigPath =>
            Path.Combine(Paths.ConfigPath, "Pilgrim.yaml");

        internal static void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    var yaml = new SerializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build()
                        .Serialize(PilgrimConfig.Default());
                    File.WriteAllText(ConfigPath, yaml);
                    Log.LogInfo($"[Pilgrim] Wrote default config to {ConfigPath}");
                }
                var des = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                Cfg = des.Deserialize<PilgrimConfig>(File.ReadAllText(ConfigPath));

                // Backfill any ritual keys missing from the on-disk config (e.g. added in a later version)
                var defaults = PilgrimConfig.Default();
                bool backfilled = false;
                foreach (var (key, val) in defaults.Rituals.Items)
                {
                    if (Cfg.Rituals.Items.ContainsKey(key)) continue;
                    Cfg.Rituals.Items[key] = val;
                    backfilled = true;
                }
                if (backfilled)
                {
                    var yaml = new SerializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .Build()
                        .Serialize(Cfg);
                    File.WriteAllText(ConfigPath, yaml);
                    Log.LogInfo("[Pilgrim] Backfilled missing ritual config keys.");
                }

                Log.LogInfo("[Pilgrim] Config loaded.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[Pilgrim] Config load failed: {ex.Message} — using defaults.");
                Cfg = PilgrimConfig.Default();
            }
        }

        // Ritual item accessors — item prefab names come from config
        internal static string SeekFood      => Cfg.Rituals.Items.GetValueOrDefault("seek_altar")?.Item    ?? "RawMeat";
        internal static string HomeFood      => Cfg.Rituals.Items.GetValueOrDefault("seek_bed")?.Item      ?? "Dandelion";
        internal static string FeatherFood   => Cfg.Rituals.Items.GetValueOrDefault("feather_fall")?.Item  ?? "Feathers";
        internal static string TraderFood    => Cfg.Rituals.Items.GetValueOrDefault("seek_trader")?.Item   ?? "Coins";
        internal static string GrowthFood    => Cfg.Rituals.Items.GetValueOrDefault("growth")?.Item        ?? "AncientSeed";
        internal static string PlayerSeekFood => Cfg.Rituals.Items.GetValueOrDefault("seek_player")?.Item  ?? "Flint";
        internal static string KindleFood    => Cfg.Rituals.Items.GetValueOrDefault("kindle")?.Item        ?? "Resin";
        internal static string TameFood     => Cfg.Rituals.Items.GetValueOrDefault("tame_flock")?.Item    ?? "BoneFragments";
        internal static string GiantFood        => Cfg.Rituals.Items.GetValueOrDefault("giant")?.Item          ?? "YmirRemains";
        internal static string FlamingSwordFood => Cfg.Rituals.Items.GetValueOrDefault("flaming_sword")?.Item ?? "SurtlingCore";
        // Wildcards: Trophy* → dungeon seek, Mushroom* → restore power
        // (prefix matching stays hardcoded; hover text and messages come from config)

        // Seek target override — when set, SE_GuidingWind tracks this position instead of next boss altar
        internal static Vector3? SeekOverrideTarget = null;

        // ── Ritual discovery ────────────────────────────────────────────────────
        internal static bool IsRitualKnown(Player player, string key) =>
            player.m_customData.ContainsKey($"ath_known_{key}");

        internal static void LearnRitual(Player player, string key, string itemDisplayName)
        {
            if (IsRitualKnown(player, key)) return;
            player.m_customData[$"ath_known_{key}"] = "1";
            if (!Cfg.Rituals.Items.TryGetValue(key, out var r)) return;
            player.Message(MessageHud.MessageType.TopLeft,
                $"<color=orange>Ritual discovered:</color> {r.HoverText}");
            var centerMsg = $"<color=orange>Ritual discovered</color>\n{r.HoverText}\nOffer <color=yellow>{itemDisplayName}</color> at a burning campfire.";
            MessageHudTimerPatch.ExtendNextCenter = true;
            player.Message(MessageHud.MessageType.Center, centerMsg);
        }

        // (prefab, isPrefix, ritualKey, displayName) — order matters for wildcard checks
        internal static readonly (string Match, bool Prefix, string Key, string Display)[] RitualItemMap =
        {
            ("RawMeat",       false, "seek_altar",    "Boar Meat"),
            ("Mushroom",      true,  "restore_power", "Mushroom"),
            ("Dandelion",     false, "seek_bed",      "Dandelion"),
            ("Feathers",      false, "feather_fall",  "Feathers"),
            ("Coins",         false, "seek_trader",   "Coins"),
            ("Trophy",        true,  "seek_dungeon",  "a Trophy"),
            ("GreydwarfEye",  false, "clear_skies",   "Greydwarf Eye"),
            ("Stone",         false, "water_walk",    "Stone"),
            ("AncientSeed",   false, "growth",        "Ancient Seed"),
            ("BoneFragments", false, "tame_flock",    "Bone Fragments"),
            ("Flint",         false, "seek_player",   "Flint"),
            ("Resin",         false, "kindle",        "Resin"),
            ("YmirRemains",   false, "giant",         "Ymir Flesh"),
            ("SurtlingCore",  false, "flaming_sword", "Surtling Core"),
        };

        // Global ritual cooldown
        internal static float RitualCooldownRemaining = 0f;
        internal static float RitualCooldownDuration  => Cfg.Rituals.Cooldown;

        // Duration accessors — fall back to hardcoded defaults if config entry missing
        internal static float ClearSkiesDuration => Cfg.Rituals.Items.GetValueOrDefault("clear_skies")?.Duration  ?? 900f;
        internal static float RainDuration        => Cfg.Rituals.Items.GetValueOrDefault("restore_power")?.Duration ?? 600f;
        internal static float WaterWalkDuration   => Cfg.Rituals.Items.GetValueOrDefault("water_walk")?.Duration   ?? 60f;
        internal static float SeekEnvDuration     => 60f; // matches SE TTL, not user-facing
        internal static float DungeonEnvDuration  => 60f;
        internal static float HomeEnvDuration     => 60f;

        // Expiry timestamps (Time.time)
        internal static float FeatherRitualExpiry  = 0f;
        internal static float ClearSkiesExpiry     = 0f;
        internal static float RainExpiry           = 0f;
        internal static float GiantRainExpiry      = 0f;
        internal static float WaterWalkExpiry      = 0f;
        internal static float SeekEnvExpiry        = 0f;
        internal static float DungeonEnvExpiry     = 0f;
        internal static float HomeEnvExpiry        = 0f;
        internal static bool  GrowthBlessingActive = false;
        internal static bool  ShowHintsEnabled     = true;
        internal static bool  TameBlessingActive   = false;
        internal static float FlamingSwordExpiry   = 0f;
        static ItemDrop.ItemData? _flamingSwordItem = null;
        static ItemDrop.ItemData? _origRightItem    = null;
        internal const string FlamingSwordPrefab = "SwordIronFire";
        internal static float GiantExpiry          = 0f;
        internal static float GiantTargetScale     = 1f;
        internal const  float GiantCarryBonus      = 300f;
        internal const  float GiantScale           = 3f;
        internal const  float GiantWalkMult        = 1.5f;
        internal const  float GiantRunMult         = 2.5f;
        internal const  float GiantJumpMult        = 1.2f;
        internal static bool  GiantSpeedApplied    = false;
        static HitData.DamageTypes _origUnarmedDmg;
        static short               _origUnarmedTier;
        static float               _origAttackRange;
        static float               _origAttackHeight;
        static float               _origAttackOffset;
        static float               _origAutoPickupRange;
        static float               _origSwimDepth;

        // Boss trophy → guardian power
        internal static readonly Dictionary<string, string> TrophyToPower = new Dictionary<string, string>
        {
            { "TrophyEikthyr",    "GP_Eikthyr"  },
            { "TrophyTheElder",   "GP_TheElder"  },
            { "TrophyBonemass",   "GP_Bonemass"  },
            { "TrophyDragonQueen","GP_Moder"     },
            { "TrophyGoblinKing", "GP_Yagluth"   },
            { "TrophySeekerQueen","GP_Queen"     },
            { "TrophyFader",      "GP_Fader"     },
            // TODO: DeepNorth boss trophy + power — add when content ships
        };

        static readonly System.Random Rng = new System.Random();

        void Awake()
        {
            Log = Logger;
            LoadConfig();
            _cfgWatcher = new FileSystemWatcher(Paths.ConfigPath, "Pilgrim.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _cfgWatcher.Changed += (_, __) => _cfgDirty = true;
            try { new Harmony("com.ctogle.pilgrim").PatchAll(); }
            catch (System.Exception ex) { Log.LogError($"[Pilgrim] Harmony patch failed: {ex.Message}"); }
            RegisterCommands();
            var go = new GameObject("AthScheduler");
            DontDestroyOnLoad(go);
            Scheduler = go.AddComponent<EnvScheduler>();
            Logger.LogInfo("Pilgrim loaded.");
        }

        // Broadcast a bird formation to all connected clients so everyone sees it.
        internal static void BroadcastBird(Vector3 direction, float speed = 10f)
        {
            Scheduler?.SendBird(direction, speed);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Pilgrim_SendBird",
                direction, speed);
        }

        // Spawn a VFX prefab at pos on every client (including self).
        internal static void BroadcastVfx(Vector3 pos, string prefabName, float destroyDelay = 4f, Quaternion? rot = null)
        {
            var r = rot ?? Quaternion.identity;
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab != null)
                SpawnVfxLocal(prefab, pos, r, destroyDelay);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Pilgrim_SpawnVfx",
                pos, r, prefabName, destroyDelay);
        }

        // Safe local VFX spawn: instantiate inactive so ZNetView.Awake never fires and never
        // registers in m_instances. Without this, Object.Destroy(go, delay) would bypass
        // ZNetScene cleanup and leave a stale ZDO entry → NullRef in RemoveObjects.
        internal static void SpawnVfxLocal(GameObject prefab, Vector3 pos, Quaternion rot, float destroyDelay)
        {
            prefab.SetActive(false);
            var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            prefab.SetActive(true);
            // Destroy all components that call GetComponent<ZNetView> in Awake before activating.
            string[] netTypes = { "ZNetView", "ZSyncTransform", "ZSyncAnimation", "ZSFX", "TimedDestruction" };
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && System.Array.IndexOf(netTypes, mb.GetType().Name) >= 0)
                    UnityEngine.Object.DestroyImmediate(mb);
            go.SetActive(true);
            if (destroyDelay > 0f) UnityEngine.Object.Destroy(go, destroyDelay);
        }

        // Force a debug environment on every client.
        internal static void BroadcastEnv(string name, string vanillaFallback = "")
        {
            SetDebugEnvSafe(name, vanillaFallback);
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Pilgrim_SetEnv",
                name, vanillaFallback);
        }

        void Update()
        {
            if (_cfgDirty) { _cfgDirty = false; LoadConfig(); }
        }

        void RegisterCommands()
        {
            // ── ath_envstate ────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_envstate", "world env, TOD, raid, wind", args =>
            {
                var em = EnvMan.instance;
                if (em == null) { args.Context.AddString("EnvMan not ready."); return; }

                float  tod       = GetTod();
                string todLabel  = GetTodLabel(tod);
                string curEnv    = GetEnvSetupName(em, "m_currentEnv") ?? "unknown";
                string nextEnv   = GetEnvSetupName(em, "m_nextEnv")    ?? "unknown";
                string debugEnv  = em.m_debugEnv ?? "";
                string schedInfo = Scheduler != null && Scheduler.Active
                    ? $"ON (prob {Scheduler.Probability:P0})"
                    : "OFF";

                double rawSec = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
                args.Context.AddString("=== World State ===");
                args.Context.AddString($"TOD         : {tod:F2}  ({todLabel})  [ZNet raw: {rawSec:F0}s  mod1800={rawSec % 1800:F0}]");
                args.Context.AddString($"Current env : {curEnv}{(debugEnv.Length > 0 ? $"  [forced: {debugEnv}]" : "")}");
                args.Context.AddString($"Next env    : {nextEnv}");
                args.Context.AddString($"Is wet      : {EnvMan.IsWet()}");
                args.Context.AddString($"Is cold     : {EnvMan.IsCold()}");
                args.Context.AddString($"Is freezing : {EnvMan.IsFreezing()}");
                args.Context.AddString($"Wind        : {em.GetWindIntensity():F2}  dir {em.GetWindDir()}");
                args.Context.AddString($"Scheduler   : {schedInfo}");

                var res = RandEventSystem.instance;
                if (res != null)
                {
                    object? ev = AnyField(res, "m_activeEvent", "m_currentEvent");
                    bool have  = ev != null && (AnyField(res, "m_haveActiveEvent") is bool b ? b : true);
                    if (have && ev != null)
                    {
                        string evName  = AnyField(ev, "m_name") as string ?? "?";
                        float  evTimer = AnyField(res, "m_eventTimer", "m_timer") is float f ? f : 0f;
                        object? posObj = AnyField(res, "m_eventPos", "m_pos");
                        string  posStr = posObj is Vector3 v ? $"({v.x:F0},{v.z:F0})" : "?";
                        args.Context.AddString($"Active raid : {evName}  |  {evTimer:F0}s  |  @ {posStr}");
                    }
                    else args.Context.AddString("Active raid : none");
                }
            });

            // ── ath_playerstats ─────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_playerstats", "all players stats + party spread", args =>
            {
                var players = Player.GetAllPlayers();
                if (players == null || players.Count == 0) { args.Context.AddString("No players found."); return; }

                args.Context.AddString($"=== Players ({players.Count}) ===");
                var positions = new List<Vector3>();
                foreach (var p in players)
                {
                    string pname = p.GetPlayerName();
                    float hp = p.GetHealth(), maxHp = p.GetMaxHealth();
                    float st = p.GetStamina(), maxSt = p.GetMaxStamina();
                    float ei = p.GetEitr(), maxEi = p.GetMaxEitr();
                    var pos = p.transform.position;
                    positions.Add(pos);

                    string biome  = p.GetCurrentBiome().ToString();
                    var    seman  = p.GetSEMan();
                    var    fxList = seman?.GetStatusEffects();
                    string fx = fxList != null && fxList.Count > 0
                        ? string.Join(", ", fxList.Select(e => string.IsNullOrEmpty(e.m_name) ? e.name : e.m_name))
                        : "none";

                    args.Context.AddString($"--- {pname} ---");
                    args.Context.AddString($"  HP {hp:F0}/{maxHp:F0}  |  Stamina {st:F0}/{maxSt:F0}  |  Eitr {ei:F0}/{maxEi:F0}");
                    args.Context.AddString($"  Biome: {biome}  |  Pos: ({pos.x:F0},{pos.z:F0})");
                    args.Context.AddString($"  Effects: {fx}");
                }

                if (positions.Count > 1)
                {
                    float maxDist = 0f;
                    for (int i = 0; i < positions.Count; i++)
                        for (int j = i + 1; j < positions.Count; j++)
                        {
                            float d = Vector3.Distance(positions[i], positions[j]);
                            if (d > maxDist) maxDist = d;
                        }
                    args.Context.AddString($"Party spread: {maxDist:F0}m");
                }
            });

            // ── ath_clearenv ────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_clearenv", "cancel all Pilgrim weather overrides", args =>
            {
                ClearSkiesExpiry = 0f;
                RainExpiry       = 0f;
                GiantRainExpiry  = 0f;
                WaterWalkExpiry  = 0f;
                SeekEnvExpiry    = 0f;
                DungeonEnvExpiry = 0f;
                HomeEnvExpiry    = 0f;
                if (GiantExpiry > 0f) { var gp = Player.m_localPlayer; if (gp != null) DeactivateGiant(gp); else GiantExpiry = 0f; }
                if (EnvMan.instance != null) EnvMan.instance.m_debugEnv = "";
                EnvMan.instance?.ResetDebugWind();
                args.Context.AddString("Weather overrides cleared.");
            });

            // ── ath_seek ────────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_seek", "point wind toward next boss altar", args =>
            {
                ActivateSeek(args.Context);
            });

            // ── ath_schedule ────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_schedule",
                "ath_schedule [on|off|prob <0-1>|night <e1,e2>|noon <e1,e2>]", args =>
            {
                var s = Scheduler;
                if (s == null) { args.Context.AddString("Scheduler not ready."); return; }
                if (args.Length < 2) { PrintSchedule(args, s); return; }
                switch (args[1].ToLower())
                {
                    case "on":  s.Active = true;  args.Context.AddString("Scheduler ON");  break;
                    case "off": s.Active = false; args.Context.AddString("Scheduler OFF"); break;
                    case "prob":
                        if (args.Length >= 3 && float.TryParse(args[2], out float p))
                        { s.Probability = Mathf.Clamp01(p); args.Context.AddString($"Probability → {s.Probability:P0}"); }
                        break;
                    case "night":
                        if (args.Length >= 3)
                        { s.NightPool = args[2].Split(',').Select(x => x.Trim()).ToList(); args.Context.AddString($"Night pool → {string.Join(", ", s.NightPool)}"); }
                        break;
                    case "noon":
                        if (args.Length >= 3)
                        { s.NoonPool = args[2].Split(',').Select(x => x.Trim()).ToList(); args.Context.AddString($"Noon pool → {string.Join(", ", s.NoonPool)}"); }
                        break;
                    default: PrintSchedule(args, s); break;
                }
            });

            // ── ath_waterwalk ────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_waterwalk", "walk on water for 2 minutes", args =>
            {
                var p = Player.m_localPlayer;
                if (p == null) { args.Context.AddString("No local player."); return; }
                Plugin.ActivateWaterWalk(p);
                args.Context.AddString("Water walk active.");
            });

            // ── ath_bird ─────────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_bird",
                "ath_bird [n|s|e|w] — send a crow flying in a direction (default: north)", args =>
            {
                if (Scheduler == null) { args.Context.AddString("Scheduler not ready."); return; }
                string dir = args.Length > 1 ? args[1].ToLower() : "n";
                Vector3 direction = dir switch {
                    "s" => new Vector3(0, 0, -1),
                    "e" => new Vector3(1, 0, 0),
                    "w" => new Vector3(-1, 0, 0),
                    _   => new Vector3(0, 0, 1),  // north = +Z
                };
                BroadcastBird(direction);
                args.Context.AddString($"Bird away ({dir}).");
            });

            // ── ath_traders ─────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_traders",
                "show trader met status and distances", args =>
            {
                var player = Player.m_localPlayer;
                if (player == null) { args.Context.AddString("No player."); return; }
                args.Context.AddString("=== Trader State ===");
                foreach (var loc in Plugin.TraderLocations)
                {
                    bool met = Plugin.HasMetTrader(loc);
                    bool found = Plugin.FindClosestLocation(loc, player.transform.position, out Vector3 pos);
                    string dist = found ? $"{Vector3.Distance(player.transform.position, pos):F0}m" : "not found";
                    args.Context.AddString($"  {loc}: met={met}  dist={dist}  pos={pos}");
                }
            });

            // ── ath_rituals ─────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_rituals",
                "list available campfire rituals", args =>
            {
                args.Context.AddString("=== Campfire Rituals ===");
                args.Context.AddString($"  {SeekFood} (Boar Meat)       → Guiding Wind — seek next boss altar");
                args.Context.AddString($"  Mushroom* (Yellow Mushroom)      → Reset forsaken power cooldown");
                args.Context.AddString($"  {HomeFood} (Dandelion)      → Guiding Wind — find your bed");
                args.Context.AddString($"  {FeatherFood} (Feathers)    → Feather Fall — no fall damage 60s");
                args.Context.AddString($"  Any Trophy                  → Dungeon Seeker — find nearest dungeon");
                args.Context.AddString($"Global cooldown: {RitualCooldownDuration}s. Hold item in slot 1, look at burning campfire, press 1.");
            });

            // ── ath_hints ───────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_hints",
                "ath_hints [on|off] — toggle campfire ritual hover hint text", args =>
            {
                bool newVal = args.Args.Length > 1
                    ? args.Args[1].ToLower() == "on"
                    : !ShowHintsEnabled;
                ShowHintsEnabled = newVal;
                args.Context.AddString($"Ritual hints: {(ShowHintsEnabled ? "ON" : "OFF")}");
            });

            // ── ath_learn / ath_forget ──────────────────────────────────────
            new Terminal.ConsoleCommand("ath_learn",
                "ath_learn [ritual|all] — unlock ritual discovery (for testing)", args =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                string target = args.Args.Length > 1 ? args.Args[1].ToLower() : "all";
                foreach (var (_, _, key, display) in Plugin.RitualItemMap)
                {
                    if (target != "all" && key != target) continue;
                    LearnRitual(player, key, display);
                }
                args.Context.AddString(target == "all" ? "All rituals learned." : $"Learned: {target}");
            });

            new Terminal.ConsoleCommand("ath_forget",
                "ath_forget [ritual|all] — reset ritual discovery", args =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                string target = args.Args.Length > 1 ? args.Args[1].ToLower() : "all";
                foreach (var (_, _, key, _) in Plugin.RitualItemMap)
                {
                    if (target != "all" && key != target) continue;
                    player.m_customData.Remove($"ath_known_{key}");
                }
                args.Context.AddString(target == "all" ? "All rituals forgotten." : $"Forgotten: {target}");
            });

            // ── ath_vfx ─────────────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_vfx",
                "ath_vfx <prefabName|list> — preview a VFX at your position, or list all vfx_ prefabs", args =>
            {
                var scene = ZNetScene.instance;
                if (scene == null) { args.Context.AddString("ZNetScene not ready."); return; }

                var rf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var dictField = typeof(ZNetScene).GetField("m_namedPrefabs", rf);
                var dict = dictField?.GetValue(scene) as Dictionary<int, GameObject>;

                if (args.Length < 2 || args[1] == "list")
                {
                    if (dict == null) { args.Context.AddString("Could not read prefab list."); return; }
                    var names = dict.Values.Where(g => g != null)
                        .Select(g => g.name)
                        .Where(n => n.StartsWith("vfx_") || n.StartsWith("fx_") || n.StartsWith("sfx_"))
                        .OrderBy(n => n).ToList();
                    args.Context.AddString($"=== {names.Count} VFX prefabs ===");
                    foreach (var n in names) args.Context.AddString($"  {n}");
                    return;
                }

                var player = Player.m_localPlayer;
                if (player == null) return;
                var prefab = scene.GetPrefab(args[1]);
                if (prefab == null) { args.Context.AddString($"Prefab '{args[1]}' not found."); return; }
                Object.Instantiate(prefab, player.transform.position, Quaternion.identity);
                args.Context.AddString($"Spawned {args[1]}");
            });

            // ── ath_upgradecart ─────────────────────────────────────────────
            new Terminal.ConsoleCommand("ath_upgradecart",
                "upgrade nearest cart capacity (or Shift+E on cart)", args =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                var cart = CartUpgrade.FindNearest(player, 10f);
                if (cart == null) { args.Context.AddString("No cart within 10m."); return; }
                CartUpgrade.Upgrade(player, cart, args);
            });

            // ── ath_envlist / ath_envdump ───────────────────────────────────
            new Terminal.ConsoleCommand("ath_envlist", "lists all registered environments", args =>
            {
                var em = EnvMan.instance;
                if (em == null) { args.Context.AddString("EnvMan not ready."); return; }
                args.Context.AddString($"=== Environments ({em.m_environments.Count}) ===");
                foreach (var e in em.m_environments) args.Context.AddString($"  {e.m_name}");
            });

            new Terminal.ConsoleCommand("ath_birdparams", "dump RandomFlyingBird fields and animator params on nearby birds", args =>
            {
                var player = Player.m_localPlayer;
                if (player == null) return;
                int found = 0;
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null || mb.GetType().Name != "RandomFlyingBird") continue;
                    if (Vector3.Distance(mb.transform.position, player.transform.position) > 60f) continue;
                    found++;
                    args.Context.AddString($"--- Bird {found} ({mb.gameObject.name}) ---");
                    var rf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                    foreach (var f in mb.GetType().GetFields(rf))
                    {
                        try { args.Context.AddString($"  {f.FieldType.Name} {f.Name} = {f.GetValue(mb)}"); }
                        catch { }
                    }
                    foreach (var comp in mb.GetComponentsInChildren<UnityEngine.Component>())
                    {
                        var getParams = comp?.GetType().GetProperty("parameters");
                        if (getParams == null) continue;
                        var ps = getParams.GetValue(comp) as System.Array;
                        if (ps == null) continue;
                        foreach (var p in ps)
                        {
                            var nameProp = p.GetType().GetProperty("name");
                            var typeProp = p.GetType().GetProperty("type");
                            args.Context.AddString($"  Anim param: {nameProp?.GetValue(p)} ({typeProp?.GetValue(p)})");
                        }
                    }
                }
                if (found == 0) args.Context.AddString("No birds within 60m.");
            });


            new Terminal.ConsoleCommand("ath_envdump", "dumps env-related EnvMan fields", args =>
            {
                var em = EnvMan.instance;
                if (em == null) { args.Context.AddString("EnvMan not ready."); return; }
                foreach (var f in typeof(EnvMan).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    string n = f.Name;
                    if (!n.ToLower().Contains("env") && !n.ToLower().Contains("current") && !n.ToLower().Contains("debug") && !n.ToLower().Contains("time")) continue;
                    var val = f.GetValue(em);
                    string display = val == null ? "null" : val.ToString()!;
                    if (val is EnvSetup es) display = $"EnvSetup({es.m_name})";
                    args.Context.AddString($"{f.FieldType.Name} {n} = {display}");
                }
            });
        }

        // ── Env helper: set debugEnv only if the name is registered, fall back to vanilla ──
        internal static void SetDebugEnvSafe(string name, string vanillaFallback = "")
        {
            if (EnvMan.instance == null) return;
            bool exists = EnvMan.instance.m_environments?.Exists(e => e.m_name == name) ?? false;
            EnvMan.instance.m_debugEnv = exists ? name : vanillaFallback;
        }

        // ── Seek logic (shared by command + food trigger) ───────────────────

        internal static void ActivateSeek(Terminal? ctx = null, string message = "The wind stirs.")
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            SeekOverrideTarget = null; // boss seek clears any dungeon override
            var (bossName, prefabName) = GetNextBoss();
            if (bossName == null)
            {
                Msg(ctx, "All known bosses defeated. The path is your own.");
                return;
            }

            if (!FindClosestLocation(prefabName!, player.transform.position, out Vector3 altarPos))
            {
                Msg(ctx, $"Next boss: {bossName} — altar not yet in loaded zones.");
                return;
            }

            Vector3 playerPos = player.transform.position;
            float dx   = altarPos.x - playerPos.x;
            float dz   = altarPos.z - playerPos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;

            // Apply SE — look it up from ObjectDB so Valheim can clone it properly
            var seTemplate = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_GuidingWind");
            if (seTemplate == null) seTemplate = GuidingWindSE; // fallback to our reference
            if (seTemplate != null)
            {
                player.GetSEMan().AddStatusEffect(seTemplate, true);
                Log.LogInfo($"[EnvR] Applied SE: {seTemplate.name} ttl={seTemplate.m_ttl}");
            }
            else
                Log.LogWarning("[EnvR] GuidingWindSE is null — ObjectDB patch may not have run");

            SeekEnvExpiry = Time.time + SeekEnvDuration;
            BroadcastEnv("LastLight", "Twilight_Clear");

            if (ctx != null)
            {
                Msg(ctx, $"=== Guiding Wind ===");
                Msg(ctx, $"Seek     : {bossName}");
                Msg(ctx, $"Distance : {dist:F0}m  ({dist / 1000f:F1}km)");
                Msg(ctx, $"Bearing  : {angle:F0}°");
                Msg(ctx, $"Follow the wind.");
            }
            else
            {
                player.Message(MessageHud.MessageType.Center, message.Replace("{boss}", bossName));
            }

        }

        // ── Ping logic ──────────────────────────────────────────────────────

        internal static void ActivateCooldownReset(Player player, string message = "The flame accepts your offering. {power} is ready.")
        {
            var rf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var powerField    = typeof(Player).GetField("m_guardianPower",         rf);
            var cooldownField = typeof(Player).GetField("m_guardianPowerCooldown", rf);

            string? powerName = powerField?.GetValue(player) as string;
            if (string.IsNullOrEmpty(powerName))
            {
                player.Message(MessageHud.MessageType.Center, "No forsaken power to reset.");
                return;
            }
            float currentCooldown = cooldownField?.GetValue(player) is float cd ? cd : 0f;
            if (currentCooldown <= 0f)
            {
                player.Message(MessageHud.MessageType.Center, "Your power is already ready.");
                return;
            }
            cooldownField?.SetValue(player, 0f);
            var se = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == powerName);
            string displayName = se?.m_name ?? powerName;
            player.Message(MessageHud.MessageType.Center, message.Replace("{power}", displayName));

            RainExpiry = Time.time + RainDuration;
            if (EnvMan.instance != null) EnvMan.instance.m_debugEnv = "Rain";
        }

        // ── Dungeon seeker ritual ───────────────────────────────────────────

        static readonly string[] DungeonLocations = {
            "Crypt2", "Crypt3", "Crypt4",     // burial chambers (Black Forest)
            "SunkenCrypt",                    // sunken crypts (Swamp)
            "MountainCave", "MountainCave02", // mountain caves
            "InfectedMine",                   // infested mines (Mistlands)
        };

        static readonly string[] TraderLocations = {
            "Vendor_BlackForest", // Haldor
            "Hildir_camp",        // Hildir
            "BogWitch_Camp",      // Bog Witch
        };

        const string TraderMetPrefix = "pilgrim_met_";

        internal static bool HasMetTrader(string locationName)
        {
            var key = TraderMetPrefix + locationName;
            return Player.m_localPlayer?.m_customData.ContainsKey(key) == true;
        }

        internal static void MarkTraderMet(Vector3 traderPos)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            // Find which of our known trader zones is closest to this NPC position
            string bestLoc = "";
            float bestDist = float.MaxValue;
            foreach (var loc in TraderLocations)
            {
                if (!FindClosestLocation(loc, traderPos, out Vector3 zonePos)) continue;
                float d = Vector3.Distance(traderPos, zonePos);
                if (d < bestDist) { bestDist = d; bestLoc = loc; }
            }
            if (bestLoc != "" && bestDist < 500f)
            {
                player.m_customData[TraderMetPrefix + bestLoc] = "1";
                Log.LogInfo($"[Trader] Marked met: {bestLoc} (dist={bestDist:F0}m)");
            }
            else Log.LogWarning($"[Trader] MarkTraderMet called but no match: pos={traderPos} bestLoc='{bestLoc}' bestDist={bestDist:F0}m");
        }

        static System.Reflection.MethodInfo? _isExploredMethod;
        static bool IsUnexplored(Vector3 pos)
        {
            if (Minimap.instance == null) return true; // assume unexplored if minimap not ready
            _isExploredMethod ??= typeof(Minimap).GetMethod("IsExplored",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (_isExploredMethod == null) return true;
            var result = _isExploredMethod.Invoke(Minimap.instance, new object[] { pos });
            return result is bool b && !b;
        }

        static bool FindNearestUnexplored(string[] locationNames, Vector3 from, out Vector3 bestPos, out string bestName, out float bestDist)
        {
            bestPos = Vector3.zero; bestName = ""; bestDist = float.MaxValue;
            foreach (var loc in locationNames)
            {
                if (!FindClosestLocation(loc, from, out Vector3 pos)) continue;
                if (!IsUnexplored(pos)) continue;
                float d = Vector3.Distance(from, pos);
                if (d < bestDist) { bestDist = d; bestPos = pos; bestName = loc; }
            }
            return bestDist < float.MaxValue;
        }

        static List<Minimap.PinData>? GetAllPins()
        {
            if (Minimap.instance == null) return null;
            var f = typeof(Minimap).GetField("m_pins",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return f?.GetValue(Minimap.instance) as List<Minimap.PinData>;
        }

        static bool HasPinNamed(string name)
        {
            var pins = GetAllPins();
            if (pins == null) return false;
            foreach (var pin in pins)
                if (pin.m_save && string.Equals(pin.m_name, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static void ActivateDungeonSeek(Player player, string trophyPrefab, string message = "The veil parts — something stirs nearby.")
        {
            if (!FindNearestUnexplored(DungeonLocations, player.transform.position, out Vector3 bestPos, out string bestName, out float bestDist))
            {
                player.Message(MessageHud.MessageType.Center, "The spirits do not answer. No unexplored dungeon found.");
                return;
            }

            SeekOverrideTarget = bestPos;
            DungeonEnvExpiry = Time.time + DungeonEnvDuration;
            BroadcastEnv("VoidWhisper", "ThunderStorm");

            var seTemplate = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_GuidingWind") ?? GuidingWindSE;
            if (seTemplate != null) player.GetSEMan().AddStatusEffect(seTemplate, true);

            player.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo($"[EnvR] Dungeon seek: {bestName} at {bestDist:F0}m");
        }

        internal static void ActivateTraderSeek(Player player)
        {
            // For traders, "discovered" = minimap pin with the trader's name exists.
            // Don't use IsUnexplored — the zone center may be far from where the trader
            // actually wandered, so map exploration around the zone center is unreliable.
            var bestPos = Vector3.zero;
            var bestName = "";
            float bestDist = float.MaxValue;

            float bestUnknownDist = float.MaxValue, bestKnownDist = float.MaxValue;
            Vector3 bestUnknownPos = Vector3.zero, bestKnownPos = Vector3.zero;
            string bestUnknownName = "", bestKnownName = "";

            foreach (var loc in TraderLocations)
            {
                if (!FindClosestLocation(loc, player.transform.position, out Vector3 pos)) continue;
                float d = Vector3.Distance(player.transform.position, pos);
                if (!HasMetTrader(loc)) { if (d < bestUnknownDist) { bestUnknownDist = d; bestUnknownPos = pos; bestUnknownName = loc; } }
                else                    { if (d < bestKnownDist)   { bestKnownDist = d;   bestKnownPos = pos;   bestKnownName = loc; } }
            }

            // Prefer nearest unmet; if all met fall back to nearest trader regardless of distance
            bestPos  = bestUnknownName != "" ? bestUnknownPos  : bestKnownPos;
            bestName = bestUnknownName != "" ? bestUnknownName : bestKnownName;
            bestDist = bestUnknownName != "" ? bestUnknownDist : bestKnownDist;

            if (bestDist == float.MaxValue)
            {
                player.Message(MessageHud.MessageType.Center, "The merchants are beyond reach.");
                return;
            }

            player.Message(MessageHud.MessageType.Center, Cfg.Rituals.Items.GetValueOrDefault("seek_trader")?.Message ?? "Gold calls to gold...");
            Log.LogInfo($"[EnvR] Trader seek: {bestName} at {bestDist:F0}m");

            var dir = (bestPos - player.transform.position);
            dir.y = 0f;
            if (dir != Vector3.zero && Scheduler != null)
                BroadcastBird(dir.normalized);
        }

        // ── Kindle ritual ───────────────────────────────────────────────────

        internal static void ActivateKindle(Player player, string message = "The darkness yields.")
        {
            const float radius = 20f;
            int count = 0;
            foreach (var fp in Object.FindObjectsOfType<Fireplace>())
            {
                if (Vector3.Distance(fp.transform.position, player.transform.position) > radius) continue;
                fp.SetFuel(fp.m_maxFuel);
                count++;
            }
            string msg = count > 0 ? message : "No fires nearby to kindle.";
            player.Message(MessageHud.MessageType.Center, msg);
            Log.LogInfo($"[Pilgrim] Kindle: lit {count} fires within {radius}m");
        }

        // ── Player seeker ritual ────────────────────────────────────────────

        internal static void ActivatePlayerSeek(Player player)
        {
            Player? nearest = null;
            float bestDist = float.MaxValue;
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == player) continue;
                float d = Vector3.Distance(player.transform.position, p.transform.position);
                if (d < bestDist) { bestDist = d; nearest = p; }
            }

            if (nearest == null)
            {
                player.Message(MessageHud.MessageType.Center, "You are alone...");
                return;
            }

            var dir = nearest.transform.position - player.transform.position;
            dir.y = 0f;
            if (dir != Vector3.zero && Scheduler != null)
                BroadcastBird(dir.normalized);

            player.Message(MessageHud.MessageType.Center, "Find fellowship.");
            Log.LogInfo($"[EnvR] Player seek: {nearest.GetPlayerName()} at {bestDist:F0}m");
        }

        // ── Home seeker ritual ──────────────────────────────────────────────

        internal static void ActivateHomeSeek(Player player, string message = "The flower carries you home...")
        {
            // Find a bed owned by this player in loaded zones
            Bed? myBed = null;
            float closestBed = float.MaxValue;
            foreach (var bed in Object.FindObjectsOfType<Bed>())
            {
                var isMine = typeof(Bed).GetMethod("IsMine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (isMine?.Invoke(bed, null) is not true) continue;
                float d = Vector3.Distance(player.transform.position, bed.transform.position);
                if (d < closestBed) { closestBed = d; myBed = bed; }
            }

            Vector3 spawnPos = myBed != null ? myBed.transform.position : Vector3.zero;
            Log.LogInfo($"[EnvR] HomeSeek: bed={myBed?.name} pos={spawnPos} dist={closestBed:F0}m");

            if (spawnPos == Vector3.zero)
            {
                player.Message(MessageHud.MessageType.Center, "No bed found in loaded area — travel closer to home.");
                return;
            }

            SeekOverrideTarget = spawnPos;
            HomeEnvExpiry = Time.time + HomeEnvDuration;
            BroadcastEnv("DreamWalk", "Meadows_Clouds");

            var seTemplate = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_GuidingWind")
                             ?? GuidingWindSE;
            if (seTemplate != null)
                player.GetSEMan().AddStatusEffect(seTemplate, true);

            float dist = Vector3.Distance(player.transform.position, spawnPos);
            player.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo($"[EnvR] Home seek: bed at {spawnPos}, dist {dist:F0}m");
        }

        // ── Feather ritual — no fall damage for 60s ─────────────────────────

        internal static void ActivateFeatherRitual(Player player, string message = "Light as a feather — fall without fear.")
        {
            // Grab the equip SE from the Feather Cape item prefab
            var capePrefab = ZNetScene.instance?.GetPrefab("CapeFeather");
            var itemDrop = capePrefab?.GetComponent<ItemDrop>();
            var featherSE = itemDrop?.m_itemData?.m_shared?.m_equipStatusEffect;

            if (featherSE == null)
            {
                player.Message(MessageHud.MessageType.Center, "The feathers scatter in the wind... (feather SE not found)");
                Log.LogWarning("[EnvR] CapeFeather SE not found — check prefab name");
                return;
            }

            // Clone it with a custom TTL so it expires after 60s
            var se = Object.Instantiate(featherSE);
            se.m_ttl          = 60f;
            se.m_startMessage = "";
            se.m_stopMessage  = "";

            player.GetSEMan().AddStatusEffect(se, true);
            FeatherRitualExpiry = Time.time + 60f; // fallback patch also active
            player.Message(MessageHud.MessageType.Center, message);
        }

        // ── Clear skies ritual ──────────────────────────────────────────────

        internal static void ActivateClearSkies(Player player, string message = "The clouds part.")
        {
            ClearSkiesExpiry = Time.time + ClearSkiesDuration;
            if (EnvMan.instance != null) EnvMan.instance.m_debugEnv = "Clear";
            // Set wind blowing in the direction the player is facing
            float windAngle = player.transform.eulerAngles.y;
            EnvMan.instance?.SetDebugWind(windAngle, 0.8f);
            player.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo($"[Pilgrim] Clear skies active for {ClearSkiesDuration}s, wind angle {windAngle:F0}°");
        }

        // ── Water walk ritual ───────────────────────────────────────────────

        internal static void ActivateWaterWalk(Player player, string message = "The sea grows still beneath your feet.")
        {
            WaterWalkExpiry = Time.time + WaterWalkDuration;
            if (EnvMan.instance != null) EnvMan.instance.SetDebugWind(0f, 0f);
            var se = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_WaterWalk") ?? WaterWalkSE;
            if (se != null) player.GetSEMan().AddStatusEffect(se, true);
            player.Message(MessageHud.MessageType.Center, message);
        }

        // ── Giant ritual ────────────────────────────────────────────────────

        internal static void ActivateGiant(Player player, string message = "The mountain answers. You are vast.")
        {
            float duration = Cfg.Rituals.Items.GetValueOrDefault("giant")?.Duration ?? 180f;
            GiantExpiry = Time.time + duration;
            GiantTargetScale = GiantScale;
            player.GetComponent<ZNetView>()?.GetZDO()?.Set("ath_scale", GiantScale);
            player.m_maxCarryWeight += GiantCarryBonus;
            var se = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_Giant") ?? GiantSE;
            if (se != null) { se.m_ttl = duration; player.GetSEMan().AddStatusEffect(se, true); }
            ApplyGiantSpeed(player);
            var unarmed = player.m_unarmedWeapon?.m_itemData?.m_shared;
            if (unarmed != null)
            {
                _origUnarmedDmg    = unarmed.m_damages;
                _origUnarmedTier   = (short)unarmed.m_toolTier;
                _origAttackRange   = unarmed.m_attack.m_attackRange;
                _origAttackHeight  = unarmed.m_attack.m_attackHeight;
                _origAttackOffset  = unarmed.m_attack.m_attackOffset;
                unarmed.m_damages.m_blunt   = 200f;
                unarmed.m_damages.m_chop    = 200f;
                unarmed.m_damages.m_pickaxe = 200f;
                unarmed.m_toolTier          = (short)100;
                unarmed.m_attack.m_attackRange  = _origAttackRange  * GiantScale;
                unarmed.m_attack.m_attackHeight = _origAttackHeight * GiantScale;
                unarmed.m_attack.m_attackOffset = _origAttackOffset * GiantScale;
            }
            VdsSwimSuppressor.Suppress();
            BroadcastEnv("GoldenAscent", "Twilight_Clear");
            player.Message(MessageHud.MessageType.Center, message);
        }

        internal static void ApplyGiantSpeed(Player player)
        {
            if (GiantSpeedApplied) return;
            player.m_walkSpeed   *= GiantWalkMult;
            player.m_runSpeed    *= GiantRunMult;
            player.m_swimSpeed   *= GiantWalkMult;
            player.m_jumpForce   *= GiantJumpMult;
            _origAutoPickupRange      = player.m_autoPickupRange;
            player.m_autoPickupRange  = 0f;
            _origSwimDepth            = player.m_swimDepth;
            player.m_swimDepth        = _origSwimDepth * GiantScale;
            GiantSpeedApplied = true;
            player.Message(MessageHud.MessageType.TopLeft, "Auto-pickup disabled while giant.");
        }

        internal static void RemoveGiantSpeed(Player player)
        {
            if (!GiantSpeedApplied) return;
            player.m_walkSpeed   /= GiantWalkMult;
            player.m_runSpeed    /= GiantRunMult;
            player.m_swimSpeed   /= GiantWalkMult;
            player.m_jumpForce   /= GiantJumpMult;
            player.m_autoPickupRange  = _origAutoPickupRange;
            player.m_swimDepth        = _origSwimDepth;
            GiantSpeedApplied = false;
        }

        internal static void DeactivateGiant(Player player)
        {
            GiantExpiry = 0f;
            GiantTargetScale = 1f;
            player.GetComponent<ZNetView>()?.GetZDO()?.Set("ath_scale", 1f);
            RemoveGiantSpeed(player);
            var unarmedShared = player.m_unarmedWeapon?.m_itemData?.m_shared;
            if (unarmedShared != null)
            {
                unarmedShared.m_damages  = _origUnarmedDmg;
                unarmedShared.m_toolTier = _origUnarmedTier;
                unarmedShared.m_attack.m_attackRange  = _origAttackRange;
                unarmedShared.m_attack.m_attackHeight = _origAttackHeight;
                unarmedShared.m_attack.m_attackOffset = _origAttackOffset;
            }
            player.m_maxCarryWeight = Mathf.Max(player.m_maxCarryWeight - GiantCarryBonus, 300f);
            player.GetSEMan().RemoveStatusEffect(GiantSE?.NameHash() ?? 0);
            VdsSwimSuppressor.Restore();
            if (EnvMan.instance?.m_debugEnv == "GoldenAscent") EnvMan.instance.m_debugEnv = "Rain";
            GiantRainExpiry = Time.time + 600f;
            player.Message(MessageHud.MessageType.TopLeft, "You return to mortal scale.");
        }

        // ── Flaming sword ritual ────────────────────────────────────────────

        internal static void ActivateFlamingSword(Player player, string message = "Dyrnwyn answers. Let it burn.")
        {
            // Deactivate any existing imbue before applying a new one
            if (FlamingSwordExpiry > 0f) DeactivateFlamingSword(player, immediate: true);

            var rf2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var currentRight = typeof(Humanoid).GetField("m_rightItem", rf2)?.GetValue(player) as ItemDrop.ItemData;

            if (currentRight?.m_shared?.m_skillType != Skills.SkillType.Swords)
            {
                player.Message(MessageHud.MessageType.Center, "You must hold a sword to summon Dyrnwyn.");
                return;
            }

            // Pull the held weapon out of the inventory list (kept by reference) to free its slot
            if (currentRight != null)
            {
                player.UnequipItem(currentRight);
                player.GetInventory().RemoveItem(currentRight);
            }

            var newItem = player.GetInventory().AddItem(FlamingSwordPrefab, 1, 1, 0, 0L, "");
            if (newItem == null)
            {
                player.Message(MessageHud.MessageType.Center, "No room in your pack for the flame.");
                if (currentRight != null) { player.GetInventory().AddItem(currentRight); player.EquipItem(currentRight); }
                return;
            }

            _origRightItem    = currentRight;
            _flamingSwordItem = newItem;
            player.EquipItem(newItem);
            SpawnSmokePuff(player);

            float duration = Cfg.Rituals.Items.GetValueOrDefault("flaming_sword")?.Duration ?? 60f;
            FlamingSwordExpiry = Time.time + duration;

            var se = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "SE_FlamingSword") ?? FlamingSwordSE;
            if (se != null) { se.m_ttl = duration; player.GetSEMan().AddStatusEffect(se, true); }

            TryPlayEmote(player, "cheer");

            // One-shot lightning strike on the sword (right-hand attach point), delayed to land on the cheer's peak
            Transform? handPtCapture = player.transform.Find("Visual/Armature/Hips/Spine/Spine1/Spine2/RightShoulder/RightArm/RightForeArm/RightHand/RightHandMiddle1")
                                    ?? player.transform;
            Vector3 strikeCapture = handPtCapture.position + Vector3.up * 0.5f + player.transform.forward * 0.5f;
            Scheduler?.RunDelayed(0.5f, () =>
            {
                BroadcastVfx(strikeCapture, "fx_chainlightning_hit", 4f);
                Log.LogInfo("[Pilgrim] FlamingSword VFX: fx_chainlightning_hit");
            });

            player.Message(MessageHud.MessageType.Center, message);
            Log.LogInfo($"[Pilgrim] FlamingSword: swapped in {FlamingSwordPrefab} for {duration}s");
        }

        static void TryPlayEmote(Player player, string emoteName)
        {
            try
            {
                if (player != Player.m_localPlayer) return;
                player.StartEmote(emoteName, true);
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[Pilgrim] TryPlayEmote failed: {ex.Message}");
            }
        }

        internal static void DeactivateFlamingSword(Player player, bool immediate = false)
        {
            FlamingSwordExpiry = 0f;

            BroadcastVfx(player.transform.position, "fx_fireskeleton_nova", 0f);

            if (immediate)
                FinishSwordSwapBack(player);
            else
                Scheduler?.RunDelayed(0.5f, () => FinishSwordSwapBack(player));
        }

        static void FinishSwordSwapBack(Player player)
        {
            if (_flamingSwordItem != null)
            {
                player.UnequipItem(_flamingSwordItem);
                player.GetInventory().RemoveItem(_flamingSwordItem);
                if (_origRightItem != null)
                {
                    player.GetInventory().AddItem(_origRightItem);
                    player.EquipItem(_origRightItem);
                }
                _flamingSwordItem = null;
                _origRightItem    = null;
            }
            SpawnSmokePuff(player);
            player.GetSEMan().RemoveStatusEffect(FlamingSwordSE?.NameHash() ?? 0);
            player.Message(MessageHud.MessageType.TopLeft, "The flame fades.");
        }

        static void SpawnSmokePuff(Player player)
        {
            Transform? handPt = player.transform.Find("Visual/Armature/Hips/Spine/Spine1/Spine2/RightShoulder/RightArm/RightForeArm/RightHand/RightHandMiddle1")
                             ?? player.transform;
            BroadcastVfx(handPt.position, "vfx_Smoked", 3f);
        }

        // ── Trophy shrine logic ─────────────────────────────────────────────

        internal static void GrantTrophyPower(Player player, string trophyPrefab, Terminal? ctx = null, UnityEngine.Vector3? vfxPos = null)
        {
            if (!TrophyToPower.TryGetValue(trophyPrefab, out string power)) return;

            player.SetGuardianPower(power);

            string bossName = trophyPrefab.Replace("Trophy", "").Replace("TheElder", "The Elder")
                                          .Replace("DragonQueen", "Moder").Replace("GoblinKing", "Yagluth")
                                          .Replace("SeekerQueen", "The Queen");

            // Play VFX at the stand position — search ZNetScene for boss/power effects
            UnityEngine.Vector3 pos = vfxPos ?? player.transform.position;
            SpawnTrophyVFX(pos);

            player.Message(MessageHud.MessageType.TopLeft, "You claim the power of the forsaken.");
        }

        // ── Boss chain ──────────────────────────────────────────────────────

        internal static void SpawnRitualVFX(Vector3 firePos, Vector3 playerPos)
        {
            var awayDir = firePos - playerPos;
            awayDir.y = 0f;
            if (awayDir == Vector3.zero) awayDir = Vector3.forward;
            BroadcastVfx(firePos, "fx_batteringram_fire", 4f, Quaternion.LookRotation(awayDir.normalized));
            BroadcastVfx(firePos, "fx_fireskeleton_nova", 0f);
        }

        static void SpawnTrophyVFX(UnityEngine.Vector3 pos)
        {
            string[] candidates = { Plugin.Cfg.Trophies.Vfx, "vfx_guardianpower_activate", "vfx_offering", "vfx_lootspawn" };
            foreach (var name in candidates)
            {
                if (ZNetScene.instance?.GetPrefab(name) != null)
                {
                    BroadcastVfx(pos, name, 0f);
                    Plugin.Log.LogInfo($"[Pilgrim] Trophy VFX: {name}");
                    return;
                }
            }
        }

        static readonly (string boss, string prefab)[] BossChain =
        {
            ("Eikthyr",    "Eikthyrpower"),
            ("The Elder",  "GDKing"),
            ("Bonemass",   "Bonemass"),
            ("Moder",      "Dragonqueen"),
            ("Yagluth",    "GoblinKing"),
            ("The Queen",  "SeekerQueen"),
            ("Fader",      "Fader"),
            // TODO: DeepNorth boss — prefab name unknown, add when content ships
        };

        static readonly string[] BossKeys =
        {
            "defeated_eikthyr",
            "defeated_gdking",
            "defeated_bonemass",
            "defeated_dragon",
            "defeated_goblinking",
            "defeated_queen",
            "defeated_fader",
        };

        internal static (string? boss, string? prefab) GetNextBoss()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return (null, null);
            for (int i = 0; i < BossChain.Length; i++)
                if (i >= BossKeys.Length || !zs.GetGlobalKey(BossKeys[i]))
                    return (BossChain[i].boss, BossChain[i].prefab);
            return (null, null);
        }

        internal static bool FindClosestLocation(string prefabName, Vector3 refPos, out Vector3 result)
        {
            result = Vector3.zero;
            var zs = ZoneSystem.instance;
            if (zs == null) return false;
            if (zs.FindClosestLocation(prefabName, refPos, out var loc))
            {
                result = loc.m_position;
                return true;
            }
            return false;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        static void PrintSchedule(Terminal.ConsoleEventArgs args, EnvScheduler s)
        {
            args.Context.AddString($"Scheduler : {(s.Active ? "ON" : "OFF")}  |  prob {s.Probability:P0}");
            args.Context.AddString($"Night pool: {string.Join(", ", s.NightPool)}");
            args.Context.AddString($"Noon pool : {string.Join(", ", s.NoonPool)}");
        }

        static string? GetEnvSetupName(EnvMan em, string fieldName)
        {
            var f = typeof(EnvMan).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (f?.GetValue(em) is EnvSetup es && !string.IsNullOrEmpty(es.m_name)) return es.m_name;
            return null;
        }

        static object? AnyField(object obj, params string[] names)
        {
            var type = obj.GetType();
            foreach (var name in names)
            {
                var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var v = f?.GetValue(obj);
                if (v != null) return v;
            }
            return null;
        }

        static void Msg(Terminal? ctx, string text)
        {
            if (ctx != null) ctx.AddString(text);
            else MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }

        static void Announce(string text)
        {
            if (Chat.instance != null)
                Chat.instance.SendText(Talker.Type.Normal, text);
        }

        internal static float GetTod()
        {
            var em = EnvMan.instance;
            if (em != null)
            {
                var rf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                // Newer Valheim: m_debugTimeOfDay=bool, m_debugTime=float
                var enableField = typeof(EnvMan).GetField("m_debugTimeOfDay", rf);
                var timeField   = typeof(EnvMan).GetField("m_debugTime",      rf);
                if (enableField?.GetValue(em) is bool enabled && enabled && timeField?.GetValue(em) is float t)
                    return t;
            }
            if (ZNet.instance == null) return 0f;
            const float dayLen = 1800f;
            return (float)(ZNet.instance.GetTimeSeconds() % dayLen / dayLen);
        }

        static string GetTodLabel(float t)
        {
            if (t < 0.25f) return "night";
            if (t < 0.32f) return "dawn";
            if (t < 0.50f) return "morning";
            if (t < 0.68f) return "afternoon";
            if (t < 0.75f) return "dusk";
            return "night";
        }

        internal static System.Random GetRng() => Rng;
    }

    // ── Harmony: feather fall — zero fall damage while ritual active ─────────

    [HarmonyPatch(typeof(Character), "ApplyDamage")]
    class FeatherFallPatch
    {
        static void Prefix(Character __instance, ref HitData hit)
        {
            if (__instance != Player.m_localPlayer) return;
            if (Time.time > Plugin.FeatherRitualExpiry) return;
            // Zero out fall damage specifically
            if (hit.m_hitType == HitData.HitType.Fall)
                hit.m_damage.m_damage = 0f;
        }
    }

    // ── Harmony: water walk suppresses swimming ──────────────────────────────

    [HarmonyPatch(typeof(Character), "IsOnGround")]
    class WaterWalkGroundPatch
    {
        static bool Prefix(Character __instance, ref bool __result)
        {
            if (__instance != Player.m_localPlayer) return true;
            if (Plugin.WaterWalkExpiry <= 0f || Time.time >= Plugin.WaterWalkExpiry) return true;
            // Only treat as grounded when within 0.5m of water surface — prevents mid-air re-jumping
            WaterVolume? vol = null;
            float wl = Floating.GetWaterLevel(__instance.transform.position, ref vol);
            if (__instance.transform.position.y <= wl + 0.02f)
            { __result = true; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(Character), "IsSwimming")]
    class WaterWalkSwimPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (Plugin.WaterWalkExpiry > 0f && Time.time < Plugin.WaterWalkExpiry)
            { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(Character), "UpdateWater")]
    class WaterWalkUpdatePatch
    {
        static bool Prefix()
        {
            return !(Plugin.WaterWalkExpiry > 0f && Time.time < Plugin.WaterWalkExpiry);
        }
    }

    [HarmonyPatch(typeof(Character), "Jump")]
    class WaterWalkJumpPatch
    {
        static void Postfix(Character __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (Plugin.WaterWalkExpiry <= 0f || Time.time >= Plugin.WaterWalkExpiry) return;
            WaterVolume? vol = null;
            float wl = Floating.GetWaterLevel(__instance.transform.position, ref vol);
            if (__instance.transform.position.y > wl + 0.5f) return; // already airborne
            var rf = BindingFlags.Instance | BindingFlags.NonPublic;
            if (typeof(Character).GetField("m_body", rf)?.GetValue(__instance) is Rigidbody rb)
                rb.AddForce(Vector3.up * 4f, ForceMode.VelocityChange);
        }
    }

    // ── Harmony: campfire hover text shows available rituals ────────────────

    [HarmonyPatch(typeof(Fireplace), "GetHoverText")]
    class FireplaceHoverPatch
    {
        static readonly string[] CampfirePrefabs = { "fire_pit", "fire_pit_iron", "hearth" };

        static void Postfix(Fireplace __instance, ref string __result)
        {
            if (!__instance.IsBurning()) return;
            string goName = __instance.gameObject.name.ToLower().Replace("(clone)", "").Trim();
            if (!System.Array.Exists(CampfirePrefabs, p => goName.StartsWith(p))) return;

            if (!Plugin.Cfg.Rituals.Enabled) return;
            var player = Player.m_localPlayer;

            var items = Plugin.Cfg.Rituals.Items;
            bool KnownAndEnabled(string key) =>
                items.TryGetValue(key, out var r) && r.Enabled &&
                (player == null || Plugin.IsRitualKnown(player, key));

            // Deduplicate keys so clear_skies (two items) only shows once
            var seen = new System.Collections.Generic.HashSet<string>();
            var rows = new System.Text.StringBuilder();
            foreach (var (_, _, key, _) in Plugin.RitualItemMap)
            {
                if (!seen.Add(key)) continue;
                if (!KnownAndEnabled(key)) continue;
                if (!items.TryGetValue(key, out var r)) continue;
                // Map key → friendly item name shown in hover
                string itemLabel = key switch {
                    "seek_altar"    => "Boar Meat",
                    "restore_power" => "Mushroom",
                    "seek_bed"      => "Dandelion",
                    "feather_fall"  => "Feathers",
                    "seek_trader"   => "Coins",
                    "seek_dungeon"  => "Trophy",
                    "clear_skies"   => "Greydwarf Eye",
                    "water_walk"    => "Stone",
                    "growth"        => "Ancient Seed",
                    "tame_flock"    => "Bone Fragments",
                    "seek_player"   => "Flint",
                    "kindle"        => "Resin",
                    "giant"         => "Ymir Flesh",
                    "flaming_sword" => "Surtling Core",
                    _               => key,
                };
                rows.Append($"\n  <color=yellow>{itemLabel}</color> — {r.HoverText}");
            }

            // Count totals (deduplicated by key)
            var allKeys = new System.Collections.Generic.HashSet<string>();
            var knownKeys = new System.Collections.Generic.HashSet<string>();
            foreach (var (_, _, k, _) in Plugin.RitualItemMap)
            {
                if (!items.TryGetValue(k, out var rc) || !rc.Enabled) continue;
                allKeys.Add(k);
                if (player != null && Plugin.IsRitualKnown(player, k)) knownKeys.Add(k);
            }

            if (rows.Length == 0) return; // no known rituals — fire is just a fire

            string cdStr = Plugin.RitualCooldownRemaining > 0f
                ? $" <color=red>({Plugin.RitualCooldownRemaining:F0}s)</color>" : "";
            string countStr = $" <color={(knownKeys.Count < allKeys.Count ? "yellow" : "green")}>{knownKeys.Count}/{allKeys.Count}</color>";

            __result += $"\n<size=14><color=orange>── Offerings{countStr}{cdStr} ──</color>";
            if (Plugin.ShowHintsEnabled)
            {
                __result += rows.ToString();
                __result += $"\n<color=grey>[H] Hide</color></size>";
            }
            else
            {
                __result += $"\n<color=grey>[H] Show</color></size>";
            }
        }
    }

    // ── Harmony: campfire ritual via hotbar number key ───────────────────────

    [HarmonyPatch(typeof(Player), "UseHotbarItem")]
    class UseHotbarItemPatch
    {
        static readonly string[] CampfirePrefabs = { "fire_pit", "fire_pit_iron", "hearth" };

        static bool Prefix(Player __instance, int index)
        {
            if (__instance != Player.m_localPlayer) return true;

            // Check if looking at a burning campfire
            var hoverObj = __instance.GetHoverObject();
            if (hoverObj == null) return true;

            var fp = hoverObj.GetComponentInParent<Fireplace>();
            if (fp == null || !fp.IsBurning()) return true;

            string goName = fp.gameObject.name.ToLower().Replace("(clone)", "").Trim();
            if (!System.Array.Exists(CampfirePrefabs, p => goName.StartsWith(p))) return true;
            if (!Plugin.Cfg.Rituals.Enabled) return true;

            // Check hotbar slot (index is 1-based in Valheim)
            var item = __instance.GetInventory().GetItemAt(index - 1, 0);
            if (item?.m_dropPrefab == null) return true;

            string prefab = item.m_dropPrefab.name;
            var ritualItems = Plugin.Cfg.Rituals.Items;
            bool RitualEnabled(string key) =>
                ritualItems.TryGetValue(key, out var r) && r.Enabled &&
                Plugin.IsRitualKnown(__instance, key);
            string RitualMsg(string key, string fallback) =>
                ritualItems.TryGetValue(key, out var r) ? r.Message : fallback;

            bool isRitual = (prefab == Plugin.SeekFood      && RitualEnabled("seek_altar"))
                         || (prefab.StartsWith("Mushroom")  && RitualEnabled("restore_power"))
                         || (prefab == Plugin.HomeFood       && RitualEnabled("seek_bed"))
                         || (prefab == Plugin.FeatherFood    && RitualEnabled("feather_fall"))
                         || (prefab == Plugin.TraderFood     && RitualEnabled("seek_trader"))
                         || (prefab.StartsWith("Trophy")    && RitualEnabled("seek_dungeon"))
                         || (prefab == "GreydwarfEye"        && RitualEnabled("clear_skies"))
                         || (prefab == "Stone"               && RitualEnabled("water_walk"))
                         || (prefab == Plugin.GrowthFood     && RitualEnabled("growth"))
                         || (prefab == Plugin.PlayerSeekFood && RitualEnabled("seek_player"))
                         || (prefab == Plugin.KindleFood     && RitualEnabled("kindle"))
                         || (prefab == Plugin.TameFood       && RitualEnabled("tame_flock"))
                         || (prefab == Plugin.GiantFood       && RitualEnabled("giant"))
                         || (prefab == Plugin.FlamingSwordFood && RitualEnabled("flaming_sword"));
            if (!isRitual) return true;

            // Global cooldown check
            if (Plugin.RitualCooldownRemaining > 0f)
            {
                __instance.Message(MessageHud.MessageType.Center,
                    "The fire is not ready...");
                return false;
            }

            Plugin.SpawnRitualVFX(fp.transform.position, __instance.transform.position);
            void Consume() { CartUpgrade.RemoveByPrefab(__instance.GetInventory(), prefab, 1); Plugin.RitualCooldownRemaining = Plugin.RitualCooldownDuration; }

            if (prefab == Plugin.SeekFood)
            {
                if (__instance.GetSEMan().HaveStatusEffect(Plugin.GuidingWindSE?.NameHash() ?? 0))
                { __instance.Message(MessageHud.MessageType.Center, "The wind already guides you."); return false; }
                Consume(); Plugin.ActivateSeek(message: RitualMsg("seek_altar", "The wind stirs.")); return false;
            }
            if (prefab.StartsWith("Mushroom"))
            {
                var rf0 = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var cdField = typeof(Player).GetField("m_guardianPowerCooldown", rf0);
                float cd0 = cdField?.GetValue(__instance) is float v ? v : 0f;
                if (cd0 <= 0f) { __instance.Message(MessageHud.MessageType.Center, "Your power is already ready."); return false; }
                Consume(); Plugin.ActivateCooldownReset(__instance, RitualMsg("restore_power", "The storm answers.")); return false;
            }
            if (prefab == Plugin.HomeFood)
            {
                Consume(); Plugin.ActivateHomeSeek(__instance, RitualMsg("seek_bed", "The flower carries you home...")); return false;
            }
            if (prefab == Plugin.FeatherFood)
            {
                Consume(); Plugin.ActivateFeatherRitual(__instance, RitualMsg("feather_fall", "The feathers catch the wind.")); return false;
            }
            if (prefab == Plugin.TraderFood)
            {
                Consume(); Plugin.ActivateTraderSeek(__instance); return false;
            }
            if (prefab.StartsWith("Trophy"))
            {
                Consume(); Plugin.ActivateDungeonSeek(__instance, prefab, RitualMsg("seek_dungeon", "The veil parts — something stirs nearby.")); return false;
            }
            if (prefab == "GreydwarfEye")
            {
                Consume(); Plugin.ActivateClearSkies(__instance, RitualMsg("clear_skies", "The clouds part.")); return false;
            }
            if (prefab == "Stone")
            {
                Consume(); Plugin.ActivateWaterWalk(__instance, RitualMsg("water_walk", "The sea grows still beneath your feet.")); return false;
            }
            if (prefab == Plugin.GrowthFood)
            {
                if (Plugin.GrowthBlessingActive)
                { __instance.Message(MessageHud.MessageType.Center, "The seed's blessing already waits in your dreams."); return false; }
                Consume(); Plugin.GrowthBlessingActive = true;
                __instance.Message(MessageHud.MessageType.Center, RitualMsg("growth", "The seed remembers the earth. Sleep, and your crops will answer."));
                return false;
            }
            if (prefab == Plugin.PlayerSeekFood)
            {
                Consume(); Plugin.ActivatePlayerSeek(__instance); return false;
            }
            if (prefab == Plugin.KindleFood)
            {
                Consume(); Plugin.ActivateKindle(__instance, RitualMsg("kindle", "The darkness yields.")); return false;
            }
            if (prefab == Plugin.TameFood)
            {
                if (Plugin.TameBlessingActive)
                { __instance.Message(MessageHud.MessageType.Center, "The bond already waits in your dreams."); return false; }
                Consume(); Plugin.TameBlessingActive = true;
                __instance.Message(MessageHud.MessageType.Center, RitualMsg("tame_flock", "The bones remember loyalty. Sleep, and your flock will answer."));
                return false;
            }
            if (prefab == Plugin.GiantFood)
            {
                if (RitualEnabled("giant"))
                { Consume(); Plugin.ActivateGiant(__instance, RitualMsg("giant", "The mountain answers. You are vast.")); return false; }
            }
            if (prefab == Plugin.FlamingSwordFood && RitualEnabled("flaming_sword"))
            {
                var rf3 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var held = typeof(Humanoid).GetField("m_rightItem", rf3)?.GetValue(__instance) as ItemDrop.ItemData;
                if (held?.m_shared?.m_skillType != Skills.SkillType.Swords)
                { __instance.Message(MessageHud.MessageType.Center, "You must hold a sword to summon Dyrnwyn."); return false; }
                Consume(); Plugin.ActivateFlamingSword(__instance, RitualMsg("flaming_sword", "Dyrnwyn answers. Let it burn.")); return false;
            }

            // Catch-all: suppress vanilla for any ritual item so we never see game rejection messages
            foreach (var (match, isPrefix, key, _) in Plugin.RitualItemMap)
            {
                bool matches = isPrefix ? prefab.StartsWith(match) : prefab == match;
                if (!matches) continue;
                // Known but config missing "giant" entry — execute if we can, else hint
                if (ritualItems.TryGetValue(key, out var r) && r.Enabled && Plugin.IsRitualKnown(__instance, key))
                    Plugin.Log.LogWarning($"[Pilgrim] Ritual '{key}' matched but not handled above — check UseHotbarItemPatch");
                else
                    __instance.Message(MessageHud.MessageType.Center, "You sense a ritual here, but don't yet understand this offering.");
                return false;
            }

            return true;
        }
    }

    // ── Harmony: ItemStand trophy shrine ────────────────────────────────────

    [HarmonyPatch(typeof(Player), "SetSleeping")]
    class GrowthBlessingPatch
    {
        static void Postfix(Player __instance, bool sleep)
        {
            if (!sleep || __instance != Player.m_localPlayer) return;
            if (!Plugin.GrowthBlessingActive) return;
            Plugin.GrowthBlessingActive = false;

            int count = 0;
            const float radius = 30f;
            var pos = __instance.transform.position;
            foreach (var plant in UnityEngine.Object.FindObjectsOfType<Plant>())
            {
                if (Vector3.Distance(plant.transform.position, pos) > radius) continue;
                var nview = plant.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) continue;
                // Backdate plant time far enough to exceed m_growTimeMax
                long ancientTicks = (ZNet.instance.GetTime() - System.TimeSpan.FromSeconds(10000)).Ticks;
                nview.GetZDO().Set(ZDOVars.s_plantTime, ancientTicks);
                plant.Grow();
                count++;
            }

            if (count > 0)
                __instance.Message(MessageHud.MessageType.TopLeft, $"{count} crops answered the blessing.");

            // Tame blessing
            if (!Plugin.TameBlessingActive) return;
            Plugin.TameBlessingActive = false;
            int tameCount = 0;
            foreach (var tameable in UnityEngine.Object.FindObjectsOfType<Tameable>())
            {
                if (Vector3.Distance(tameable.transform.position, pos) > radius) continue;
                var nview2 = tameable.GetComponent<ZNetView>();
                if (nview2 == null || !nview2.IsValid() || !nview2.IsOwner()) continue;
                float tame = nview2.GetZDO().GetFloat(ZDOVars.s_tameTimeLeft, -1f);
                // Only tame creatures already started (tameTimeLeft has been set = tameness > 0)
                if (tame < 0f) continue;
                var ch = tameable.GetComponent<Character>();
                if (ch != null) ch.SetTamed(true);
                tameCount++;
            }
            if (tameCount > 0)
                __instance.Message(MessageHud.MessageType.TopLeft, $"{tameCount} creatures answered the bond.");
        }
    }

    [HarmonyPatch(typeof(ItemStand), "Interact")]
    class ItemStandPatch
    {
        static bool Prefix(ItemStand __instance, Humanoid user, bool hold, bool alt)
        {
            if (!Plugin.Cfg.Trophies.Enabled) return true;
            // only intercept Shift+E (alt); regular E = vanilla (open/take)
            if (hold || !alt) return true;
            if (!__instance.HaveAttachment()) return true;

            // GetAttachedItem returns the item prefab name as a string
            string prefab = __instance.GetAttachedItem() ?? "";
            // Debug: always print what we see so we can fix the lookup key
            Console.instance?.AddString($"[EnvR] ItemStand attached='{prefab}' hasAttach={__instance.HaveAttachment()}");
            if (string.IsNullOrEmpty(prefab)) return true;
            if (!Plugin.TrophyToPower.ContainsKey(prefab))
            {
                Console.instance?.AddString($"[EnvR] '{prefab}' not in TrophyToPower map");
                return true;
            }

            var player = user as Player;
            if (player == null) return true;

            Plugin.GrantTrophyPower(player, prefab, vfxPos: player.transform.position);
            return false; // trophy stays on stand — shrine remains
        }
    }

    // ── SE_GuidingWind ───────────────────────────────────────────────────────

    public class SE_GuidingWind : StatusEffect
    {
        float _windTimer = 0f; // fire immediately on first tick

        public override void Setup(Character character)
        {
            base.Setup(character);
            // Try SE_Finder icon (compass/seeker) then fall through other options
            string[] iconCandidates = { "Rested" };
            foreach (var candidate in iconCandidates)
            {
                var src = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == candidate);
                if (src?.m_icon != null) { m_icon = src.m_icon; Plugin.Log.LogInfo($"[Pilgrim] GuidingWind icon from {candidate}"); break; }
            }
            if (m_icon == null) Plugin.Log.LogWarning("[Pilgrim] GuidingWind: no icon found from any candidate");
            RefreshWind();
        }

        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            _windTimer -= dt;
            if (_windTimer <= 0f)
            {
                _windTimer = 30f;
                RefreshWind();
            }
        }

        public override void Stop()
        {
            base.Stop();
            var em = EnvMan.instance;
            if (em != null)
            {
                em.ResetDebugWind();
                if (em.m_debugEnv == "GoldenAscent")
                    em.m_debugEnv = "";
                if (em.m_debugEnv == "DreamWalk" && Plugin.HomeEnvExpiry <= 0f)
                    em.m_debugEnv = "";
                if (em.m_debugEnv == "LastLight" && Plugin.SeekEnvExpiry <= 0f)
                    em.m_debugEnv = "";
                if (em.m_debugEnv == "VoidWhisper" && Plugin.DungeonEnvExpiry <= 0f)
                    em.m_debugEnv = "";
                Plugin.SeekOverrideTarget = null;
            }
        }

        public override string GetIconText()
        {
            float remaining = Mathf.Max(0f, m_ttl - m_time);
            int mins = (int)(remaining / 60);
            int secs = (int)(remaining % 60);
            return mins > 0 ? $"{mins}m {secs:D2}s" : $"{secs}s";
        }

        void RefreshWind()
        {
            var player = m_character as Player;
            if (player == null || EnvMan.instance == null) return;

            Vector3 target;

            if (Plugin.SeekOverrideTarget.HasValue)
            {
                // Dungeon seek mode — track the stored position
                target = Plugin.SeekOverrideTarget.Value;
                Plugin.BroadcastEnv("VoidWhisper", "ThunderStorm");
            }
            else
            {
                // Boss seek mode
                var (_, prefab) = Plugin.GetNextBoss();
                if (prefab == null) return;
                if (!Plugin.FindClosestLocation(prefab, player.transform.position, out target)) return;
                Plugin.BroadcastEnv("LastLight", "Twilight_Clear");
            }

            Vector3 pos = player.transform.position;
            float angle = Mathf.Atan2(target.x - pos.x, target.z - pos.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            EnvMan.instance.SetDebugWind(angle, 1.0f);
        }
    }

    // ── Harmony: register SE in ObjectDB ────────────────────────────────────

    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    class ObjectDBAwakePatch
    {
        static bool _done = false;

        static void Postfix(ObjectDB __instance)
        {
            if (_done) return;
            _done = true;

            var se = ScriptableObject.CreateInstance<SE_GuidingWind>();
            se.name      = "SE_GuidingWind";
            se.m_name    = "Guiding Wind";
            se.m_tooltip = "The gods guide your path. Follow the wind.";
            se.m_ttl     = 60f; // 1 minute
            se.m_startMessage = "";
            se.m_stopMessage  = "";

            // Leave icon null here — resolved lazily in SE_GuidingWind.Setup once ObjectDB is fully loaded
            __instance.m_StatusEffects.Add(se);
            Plugin.GuidingWindSE = se;
            Plugin.Log.LogInfo($"[EnvR] Registered SE_GuidingWind");

            var ww = ScriptableObject.CreateInstance<SE_WaterWalk>();
            ww.name           = "SE_WaterWalk";
            ww.m_name         = "Still Waters";
            ww.m_tooltip      = "The sea is glass beneath your feet.";
            ww.m_ttl          = 0f;
            ww.m_startMessage = "";
            ww.m_stopMessage  = "";
            __instance.m_StatusEffects.Add(ww);
            Plugin.WaterWalkSE = ww;

            var giant = ScriptableObject.CreateInstance<SE_Giant>();
            giant.name           = "SE_Giant";
            giant.m_name         = "Ymir's Fury";
            giant.m_tooltip      = "You walk as a mountain. Carry more, strike fear.";
            giant.m_ttl          = 180f;
            giant.m_startMessage = "";
            giant.m_stopMessage  = "";
            __instance.m_StatusEffects.Add(giant);
            Plugin.GiantSE = giant;

            var dyrnwyn = ScriptableObject.CreateInstance<SE_FlamingSword>();
            dyrnwyn.name           = "SE_FlamingSword";
            dyrnwyn.m_name         = "Dyrnwyn";
            dyrnwyn.m_tooltip      = "A blade of fire, summoned and bound to flicker out in time.";
            dyrnwyn.m_ttl          = 60f;
            dyrnwyn.m_startMessage = "";
            dyrnwyn.m_stopMessage  = "";
            __instance.m_StatusEffects.Add(dyrnwyn);
            Plugin.FlamingSwordSE = dyrnwyn;
        }
    }

    public class SE_WaterWalk : StatusEffect
    {
        public override void Setup(Character character)
        {
            base.Setup(character);
            var src = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == "Rested");
            if (src?.m_icon != null) m_icon = src.m_icon;
        }

        public override string GetIconText()
        {
            float remaining = Mathf.Max(0f, Plugin.WaterWalkExpiry - Time.time);
            int mins = (int)(remaining / 60);
            int secs = (int)(remaining % 60);
            return mins > 0 ? $"{mins}m {secs:D2}s" : $"{secs}s";
        }
    }

    public class SE_Giant : StatusEffect
    {
        public override void Setup(Character character)
        {
            base.Setup(character);
            // Try several icon candidates — something powerful/rage themed
            string[] candidates = { "GP_Yagluth", "GP_Bonemass", "GP_Eikthyr", "SE_Burning", "Rested" };
            foreach (var c in candidates)
            {
                var src = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == c);
                if (src?.m_icon != null) { m_icon = src.m_icon; break; }
            }
        }

        public override string GetIconText()
        {
            float remaining = Mathf.Max(0f, Plugin.GiantExpiry - Time.time);
            int mins = (int)(remaining / 60);
            int secs = (int)(remaining % 60);
            return mins > 0 ? $"{mins}m {secs:D2}s" : $"{secs}s";
        }
    }

    public class SE_FlamingSword : StatusEffect
    {
        public override void Setup(Character character)
        {
            base.Setup(character);
            var prefab = ZNetScene.instance?.GetPrefab(Plugin.FlamingSwordPrefab);
            var shared = prefab?.GetComponent<ItemDrop>()?.m_itemData?.m_shared;
            if (shared?.m_icons != null && shared.m_icons.Length > 0)
            {
                m_icon = shared.m_icons[0];
                return;
            }
            string[] candidates = { "SE_Burning", "GP_Eikthyr", "Rested" };
            foreach (var c in candidates)
            {
                var src = ObjectDB.instance?.m_StatusEffects?.Find(s => s.name == c);
                if (src?.m_icon != null) { m_icon = src.m_icon; break; }
            }
        }

        public override string GetIconText()
        {
            float remaining = Mathf.Max(0f, Plugin.FlamingSwordExpiry - Time.time);
            int mins = (int)(remaining / 60);
            int secs = (int)(remaining % 60);
            return mins > 0 ? $"{mins}m {secs:D2}s" : $"{secs}s";
        }
    }

    // Giant unarmed damage handled by boosting m_unarmedWeapon shared data in ActivateGiant/DeactivateGiant.

    // ── Giant: reduce incoming damage to 10% while active ───────────────────
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    class GiantDamageResistPatch
    {
        static void Prefix(Character __instance, HitData hit)
        {
            if (__instance != Player.m_localPlayer) return;
            if (Plugin.GiantExpiry <= 0f || Time.time > Plugin.GiantExpiry) return;
            hit.m_damage.m_blunt    *= 0.1f;
            hit.m_damage.m_slash    *= 0.1f;
            hit.m_damage.m_pierce   *= 0.1f;
            hit.m_damage.m_chop     *= 0.1f;
            hit.m_damage.m_pickaxe  *= 0.1f;
            hit.m_damage.m_fire     *= 0.1f;
            hit.m_damage.m_frost    *= 0.1f;
            hit.m_damage.m_lightning *= 0.1f;
            hit.m_damage.m_poison   *= 0.1f;
            hit.m_damage.m_spirit   *= 0.1f;
        }
    }

    // ── Giant: offset water surface so VikingsDoSwim positions character at correct depth ──
    [HarmonyPatch(typeof(WaterVolume), "GetWaterSurface")]
    class GiantWaterSurfacePatch
    {
        static void Postfix(ref float __result)
        {
            if (Plugin.GiantExpiry <= 0f || Time.time >= Plugin.GiantExpiry) return;
            __result -= (Plugin.GiantScale - 1f) * 1.2f;
        }
    }

    // ── Giant: suppress VikingsDoSwim's OnSwimming patch while giant ─────────
    static class VdsSwimSuppressor
    {
        const string VdsGuid = "blacks7ar.VikingsDoSwim";
        static readonly Harmony _h = new Harmony("com.ctogle.pilgrim.vdssuppressor");
        static readonly System.Reflection.MethodInfo _target =
            typeof(Character).GetMethod("OnSwimming", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        // Saved VDS patch methods so we can restore them
        static readonly List<(System.Reflection.MethodInfo method, HarmonyPatchType type)> _saved = new();

        internal static void Suppress()
        {
            if (_target == null) return;
            var info = Harmony.GetPatchInfo(_target);
            if (info == null) return;
            _saved.Clear();
            foreach (var p in info.Prefixes)
                if (p.owner == VdsGuid) { _h.Unpatch(_target, p.PatchMethod); _saved.Add((p.PatchMethod, HarmonyPatchType.Prefix)); }
            foreach (var p in info.Postfixes)
                if (p.owner == VdsGuid) { _h.Unpatch(_target, p.PatchMethod); _saved.Add((p.PatchMethod, HarmonyPatchType.Postfix)); }
            foreach (var p in info.Transpilers)
                if (p.owner == VdsGuid) { _h.Unpatch(_target, p.PatchMethod); _saved.Add((p.PatchMethod, HarmonyPatchType.Transpiler)); }
        }

        internal static void Restore()
        {
            if (_target == null) return;
            foreach (var (method, type) in _saved)
            {
                var hm = new HarmonyMethod(method);
                switch (type)
                {
                    case HarmonyPatchType.Prefix:     _h.Patch(_target, prefix: hm);     break;
                    case HarmonyPatchType.Postfix:    _h.Patch(_target, postfix: hm);    break;
                    case HarmonyPatchType.Transpiler: _h.Patch(_target, transpiler: hm); break;
                }
            }
            _saved.Clear();
        }
    }

    // ── Cart: quick release + ritual detection handled in EnvScheduler.Update ──
    // (see EnvScheduler.Update below)

    // ── Cart: Shift+E to upgrade ─────────────────────────────────────────────

    [HarmonyPatch(typeof(Vagon), "Interact")]
    class VagonInteractPatch
    {
        static bool Prefix(Vagon __instance, Humanoid character, bool hold, bool alt)
        {
            if (!Plugin.Cfg.Carts.Enabled) return true;
            if (!alt) return true; // regular E = vanilla (attach/detach)
            var player = character as Player;
            if (player == null) return true;
            CartUpgrade.Upgrade(player, __instance);
            return false;
        }
    }

    // ── Cart: hover text shows Shift+E hint ──────────────────────────────────

    [HarmonyPatch(typeof(Vagon), "GetHoverText")]
    class VagonHoverPatch
    {
        static void Postfix(Vagon __instance, ref string __result)
        {
            if (!Plugin.Cfg.Carts.Enabled) return;
            var nview = __instance.GetComponent<ZNetView>();
            int level = nview?.GetZDO()?.GetInt("ath_cart_level") ?? 0;
            __result = __result.Replace("Cart", level == 0 ? "Cart" : $"Cart - Tier {level}");
            int slots = CartUpgrade.BaseWidth * CartUpgrade.Heights[Mathf.Clamp(level, 0, CartUpgrade.Heights.Length - 1)];

            if (level >= CartUpgrade.Heights.Length - 1)
            {
                __result += $"\n<color=grey>Cart fully reinforced</color>";
                __result += $"\n[<color=yellow>G</color>] Release cart";
                return;
            }

            var cost = CartUpgrade.Costs[level];
            __result += $"\n[<color=yellow>Shift+E</color>] Reinforce cart";
            foreach (var (item, amount) in cost)
                __result += $"\n  {amount}x {CartUpgrade.DisplayName(item)}";
            __result += $"\n[<color=yellow>G</color>] Release cart";
        }
    }

    // ── Trophy stand: hover text shows Shift+E hint ──────────────────────────

    [HarmonyPatch(typeof(ItemStand), "GetHoverText")]
    class ItemStandHoverPatch
    {
        // Finalizer runs after ALL other mods' patches — guarantees our line survives
        static System.Exception Finalizer(System.Exception __exception, ItemStand __instance, ref string __result)
        {
            if (Plugin.Cfg.Trophies.Enabled && __instance.HaveAttachment())
            {
                string prefab = __instance.GetAttachedItem() ?? "";
                if (Plugin.TrophyToPower.ContainsKey(prefab) &&
                    !__result.Contains("Shift+E"))
                    __result += "\n[<color=yellow>Shift+E</color>] Claim Forsaken Power";
            }
            return __exception;
        }
    }

    // ── Water walk: keep attached cart at water surface ─────────────────────

    [HarmonyPatch(typeof(Vagon), "FixedUpdate")]
    static class VagonFixedUpdatePatch
    {
        static readonly BindingFlags RF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static readonly Dictionary<ZDOID, Minimap.PinData> _cartPins = new();
        static float _pinUpdateTimer = 0f;

        static System.Reflection.MethodInfo? _addPinMethod;

        internal static void EnsurePin(Vagon vagon)
        {
            if (Minimap.instance == null) return;
            var nview = vagon.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var uid = nview.GetZDO()!.m_uid;
            int level = nview.GetZDO()!.GetInt("ath_cart_level");
            string label = level > 0 ? $"Cart - Tier {level}" : "Cart";
            bool attached = vagon.IsAttached(Player.m_localPlayer);

            if (_cartPins.TryGetValue(uid, out var existing) && existing != null)
            {
                if (attached)
                {
                    // Remove pin while towing; it will be re-added when detached
                    Minimap.instance.RemovePin(existing);
                    _cartPins.Remove(uid);
                    return;
                }
                existing.m_pos = vagon.transform.position;
                existing.m_name = label;
                return;
            }

            if (attached) return; // don't pin while towing

            // Remove stale saved pin with same label from a prior session
            var pinsField = typeof(Minimap).GetField("m_pins", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (pinsField?.GetValue(Minimap.instance) is List<Minimap.PinData> pins)
            {
                var stale = pins.Find(p => p.m_name == label && p.m_save);
                if (stale != null) Minimap.instance.RemovePin(stale);
            }

            _addPinMethod ??= typeof(Minimap).GetMethod("AddPin",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_addPinMethod == null) return;
            var paramDefs = _addPinMethod.GetParameters();
            var args = new object[paramDefs.Length];
            args[0] = vagon.transform.position;
            args[1] = (Minimap.PinType)0;  // Icon0 = fire/campfire
            args[2] = label;
            args[3] = true;
            args[4] = false;
            for (int i = 5; i < args.Length; i++)
                args[i] = paramDefs[i].DefaultValue ?? System.Activator.CreateInstance(paramDefs[i].ParameterType);
            var pin = _addPinMethod.Invoke(Minimap.instance, args) as Minimap.PinData;
            if (pin != null) _cartPins[uid] = pin;
        }

        static void Postfix(Vagon __instance)
        {
            // Minimap pin — update every 2s to avoid per-frame overhead
            _pinUpdateTimer -= Time.fixedDeltaTime;
            if (_pinUpdateTimer > 0f) return;
            _pinUpdateTimer = 2f;
            EnsurePin(__instance);
        }
    }

    // ── Container window auto-resize ────────────────────────────────────────

    [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
    static class ContainerWindowResizePatch
    {
        static readonly BindingFlags RF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static int _lastRows = -1;
        static float _defaultH = 0f;
        // Original positions of center-anchored children that live near the panel top
        static readonly string[] _topCenterChildren = { "TakeAll", "StackAll", "createGroupBtn", "sunken", "ContainerScroll" };
        static readonly Dictionary<string, Vector2> _origPos  = new();
        static readonly Dictionary<string, Vector2> _origSize = new();

        static void Postfix(InventoryGui __instance)
        {
            try
            {
                var currentContainer = typeof(InventoryGui).GetField("m_currentContainer", RF)?.GetValue(__instance) as Container;
                if (currentContainer == null) return;
                bool isVagon = currentContainer.GetComponentInParent<Vagon>() != null;
                bool isShip = false;
                if (!isVagon)
                {
                    var ship = currentContainer.GetComponentInParent<Ship>();
                    if (ship != null)
                    {
                        string sn = ship.gameObject.name.ToLower().Replace("(clone)", "").Trim();
                        isShip = ShipStoragePatch.ShipLevels.ContainsKey(sn);
                    }
                }
                if (!isVagon && !isShip) return;

                int rows = currentContainer.GetInventory()?.GetHeight() ?? 3;
                if (rows == _lastRows) return;
                _lastRows = rows;

                var containerGrid = typeof(InventoryGui).GetField("m_containerGrid", RF)?.GetValue(__instance) as InventoryGrid;
                float cellH = 64f;
                var espaceField = typeof(InventoryGrid).GetField("m_elementSpace", RF);
                if (espaceField?.GetValue(containerGrid) is Vector2 es) cellH = es.y;

                RectTransform? panelRt = null;
                var containerFieldVal = typeof(InventoryGui).GetField("m_container", RF)?.GetValue(__instance);
                if (containerFieldVal is RectTransform rt2) panelRt = rt2;
                else if (containerFieldVal is GameObject go) panelRt = go.GetComponent<RectTransform>();
                if (panelRt == null) return;

                // Record original positions once (from first open, before any resize)
                if (_defaultH == 0f)
                {
                    _defaultH = panelRt.sizeDelta.y;
                    for (int i = 0; i < panelRt.childCount; i++)
                    {
                        var ch = panelRt.GetChild(i) as RectTransform;
                        if (ch != null && System.Array.IndexOf(_topCenterChildren, ch.name) >= 0)
                        {
                            _origPos[ch.name]  = ch.anchoredPosition;
                            _origSize[ch.name] = ch.sizeDelta;
                        }
                    }
                }

                float newH = rows * cellH + 110f;
                panelRt.sizeDelta = new Vector2(panelRt.sizeDelta.x, newH);

                // Shift center-anchored top elements so they stay at their original
                // distance from the panel top as the panel grows downward.
                float delta = (newH - _defaultH) / 2f;
                for (int i = 0; i < panelRt.childCount; i++)
                {
                    var ch = panelRt.GetChild(i) as RectTransform;
                    if (ch == null || !_origPos.TryGetValue(ch.name, out var orig)) continue;
                    ch.anchoredPosition = new Vector2(orig.x, orig.y + delta);
                    // Also stretch ContainerScroll height to match the new grid height
                    if (ch.name == "ContainerScroll" && _origSize.TryGetValue(ch.name, out var os))
                        ch.sizeDelta = new Vector2(os.x, os.y + delta * 2f);
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[Resize] {ex.Message}"); }
        }

        internal static void Reset() { _lastRows = -1; }
    }

    // MoveAll (Take All button) tries to place items at their original grid positions first.
    // If another mod expanded the player inventory height without rebuilding the grid's
    // m_elements array, items from our expanded rows (y>=4) can land at y=4/5 in the player
    // inventory and crash InventoryGrid.UpdateGui via out-of-bounds m_elements access.
    // Fix: invalidate source positions before the move so all items go through auto-place.
    [HarmonyPatch(typeof(Inventory), "MoveAll")]
    static class MoveAllExpandedPatch
    {
        static readonly BindingFlags RF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo _gpf = typeof(ItemDrop.ItemData).GetField("m_gridPos", RF);
        static readonly FieldInfo _gxf = _gpf?.FieldType.GetField("x", RF);
        static readonly FieldInfo _gyf = _gpf?.FieldType.GetField("y", RF);

        // item → saved (x, y) so we can restore for items that couldn't be moved
        static readonly Dictionary<ItemDrop.ItemData, (int x, int y)> _saved = new();

        static void Prefix(Inventory fromInventory)
        {
            if (!InventorySavePatch.CartLevels.ContainsKey(fromInventory)) return;
            if (_gxf == null || _gyf == null) return;
            _saved.Clear();
            foreach (var item in fromInventory.GetAllItems())
            {
                var gp = _gpf.GetValue(item);
                int ox = (int)_gxf.GetValue(gp), oy = (int)_gyf.GetValue(gp);
                _saved[item] = (ox, oy);
                // Setting x to -1 makes AddItem(item, stack, x, y) fail bounds check → auto-place
                _gxf.SetValue(gp, -1);
                _gpf.SetValue(item, gp);
            }
        }

        static void Postfix(Inventory fromInventory)
        {
            if (_saved.Count == 0) return;
            if (_gxf == null || _gyf == null) { _saved.Clear(); return; }
            // Restore positions only for items still in the source (couldn't be moved)
            foreach (var item in fromInventory.GetAllItems())
            {
                if (!_saved.TryGetValue(item, out var orig)) continue;
                var gp = _gpf.GetValue(item);
                _gxf.SetValue(gp, orig.x);
                _gyf.SetValue(gp, orig.y);
                _gpf.SetValue(item, gp);
            }
            _saved.Clear();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "Hide")]
    static class ContainerGuiHidePatch
    {
        static void Postfix() => ContainerWindowResizePatch.Reset();
    }

    // ── Cart: upgradeable inventory ──────────────────────────────────────────

    // Container.Awake loads inventory with default height (3). We must resize and reload
    // BEFORE items are considered "loaded" so items at y>=3 aren't dropped.
    [HarmonyPatch(typeof(Container), "Awake")]
    static class ContainerAwakePatch
    {
        static void Postfix(Container __instance)
        {
            if (!Plugin.Cfg.Carts.Enabled) return;
            var vagon = __instance.GetComponentInParent<Vagon>();
            if (vagon == null) return;
            var nview = __instance.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            int level = zdo.GetInt("ath_cart_level");
            if (level <= 0) return;
            var inv = __instance.GetInventory();
            if (inv == null) return;
            CartUpgrade.ResizeInventory(inv, level);
            // Reload items from ZDO — the first load used height=3 and dropped items at y>=3
            var bytes = zdo.GetByteArray("items");
            if (bytes != null && bytes.Length > 0)
            {
                var pkg = new ZPackage(bytes);
                inv.Load(pkg);
                Plugin.Log.LogWarning($"[Cart] ContainerAwake: reloaded items at tier {level}");
            }
            InventorySavePatch.CartLevels[inv] = level;
        }
    }

    [HarmonyPatch(typeof(Inventory), "Save")]
    static class InventorySavePatch
    {
        internal static readonly Dictionary<Inventory, int> CartLevels = new();

        static void Prefix(Inventory __instance)
        {
            if (!CartLevels.TryGetValue(__instance, out int level) || level <= 0) return;
            CartUpgrade.ResizeInventory(__instance, level);
        }
    }

    [HarmonyPatch(typeof(Inventory), "Load")]
    static class InventoryLoadPatch
    {
        static readonly BindingFlags _rf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo _itemsField    = typeof(Inventory).GetField("m_items",  BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _widthField    = typeof(Inventory).GetField("m_width",  BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _heightField   = typeof(Inventory).GetField("m_height", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo _gridPosField  = typeof(ItemDrop.ItemData).GetField("m_gridPos", _rf);
        static readonly FieldInfo _gridPosX      = _gridPosField?.FieldType.GetField("x", _rf);
        static readonly FieldInfo _gridPosY      = _gridPosField?.FieldType.GetField("y", _rf);

        static void Prefix(Inventory __instance)
        {
            if (!InventorySavePatch.CartLevels.TryGetValue(__instance, out int level)) return;
            CartUpgrade.ResizeInventory(__instance, level);
        }

        // After items are loaded, relocate any with out-of-bounds grid positions.
        // These can result from saves made before the CartLevels bug was fixed; if left
        // uncorrected they crash InventoryGrid.UpdateGui via out-of-bounds m_elements access.
        static void Postfix(Inventory __instance)
        {
            if (_itemsField?.GetValue(__instance) is not List<ItemDrop.ItemData> items) return;
            if (_widthField == null || _heightField == null || _gridPosField == null ||
                _gridPosX == null || _gridPosY == null) return;

            int w = (int)_widthField.GetValue(__instance);
            int h = (int)_heightField.GetValue(__instance);

            // Build a set of occupied (x,y) positions so we can find empty ones
            var occupied = new HashSet<(int, int)>();
            foreach (var item in items)
            {
                var pos = _gridPosField.GetValue(item);
                int px = (int)_gridPosX.GetValue(pos), py = (int)_gridPosY.GetValue(pos);
                if (px >= 0 && px < w && py >= 0 && py < h) occupied.Add((px, py));
            }

            foreach (var item in items)
            {
                var pos = _gridPosField.GetValue(item);
                int px = (int)_gridPosX.GetValue(pos), py = (int)_gridPosY.GetValue(pos);
                if (px >= 0 && px < w && py >= 0 && py < h) continue;

                // Find first unoccupied slot
                bool placed = false;
                for (int sy = 0; sy < h && !placed; sy++)
                for (int sx = 0; sx < w && !placed; sx++)
                {
                    if (occupied.Contains((sx, sy))) continue;
                    occupied.Add((sx, sy));
                    _gridPosX.SetValue(pos, sx);
                    _gridPosY.SetValue(pos, sy);
                    _gridPosField.SetValue(item, pos);
                    Plugin.Log.LogInfo($"[EnvR] Relocated '{item.m_shared.m_name}' from ({px},{py}) → ({sx},{sy})");
                    placed = true;
                }
                if (!placed)
                    Plugin.Log.LogWarning($"[EnvR] No empty slot for out-of-bounds item '{item.m_shared.m_name}' ({px},{py}) in {w}x{h} inv");
            }
        }
    }

    [HarmonyPatch(typeof(Vagon), "Awake")]
    class VagonAwakePatch
    {
        static void Postfix(Vagon __instance)
        {
            // ZDO data isn't populated yet in Awake — defer one frame
            __instance.StartCoroutine(RestoreAfterLoad(__instance));
        }

        static System.Collections.IEnumerator RestoreAfterLoad(Vagon vagon)
        {
            yield return null; // wait one frame for ZDO sync
            var nview = vagon.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) yield break;
            int level = nview.GetZDO()?.GetInt("ath_cart_level") ?? 0;
            if (level > 0) CartUpgrade.ApplySize(vagon, level);
            CartUpgrade.ApplyTint(vagon, level);
            var rf2 = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var cont = typeof(Vagon).GetField("m_container", rf2)?.GetValue(vagon) as Container;
            var inv = cont?.GetInventory();
            if (inv != null && level > 0) InventorySavePatch.CartLevels[inv] = level;
            VagonFixedUpdatePatch.EnsurePin(vagon);
        }
    }

    // ── Ship storage expansion ───────────────────────────────────────────────

    [HarmonyPatch(typeof(Container), "Awake")]
    static class ShipStoragePatch
    {
        // karve: 6×6 (tier-3 cart), vikingship: 6×6 (doubles default 6×3)
        internal static readonly Dictionary<string, int> ShipLevels = new()
        {
            { "karve",      3 },
            { "vikingship", 3 },
        };

        static void Postfix(Container __instance)
        {
            if (!Plugin.Cfg.Ships.Enabled) return;
            var ship = __instance.GetComponentInParent<Ship>();
            if (ship == null) return;
            string rawName = ship.gameObject.name.ToLower().Replace("(clone)", "").Trim();
            if (!ShipLevels.TryGetValue(rawName, out int level)) return;
            __instance.StartCoroutine(RestoreAfterLoad(__instance, level));
        }

        static System.Collections.IEnumerator RestoreAfterLoad(Container container, int level)
        {
            yield return null; // wait one frame for ZDO sync
            var nview = container.GetComponentInParent<ZNetView>();
            if (nview == null || !nview.IsValid()) yield break;
            var inv = container.GetInventory();
            if (inv == null) yield break;
            CartUpgrade.ResizeInventory(inv, level);
            InventorySavePatch.CartLevels[inv] = level;
            var bytes = nview.GetZDO()?.GetByteArray("items");
            if (bytes != null && bytes.Length > 0)
                inv.Load(new ZPackage(bytes));
        }
    }

    // ── Cart: drop upgrade materials on destruction ──────────────────────────

    [HarmonyPatch(typeof(WearNTear), "Awake")]
    class VagonDestroyPatch
    {
        static void Postfix(WearNTear __instance)
        {
            if (!Plugin.Cfg.Carts.Enabled) return;
            if (__instance.GetComponent<Vagon>() == null) return;
            __instance.m_onDestroyed += () =>
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                var uid = nview.GetZDO()!.m_uid;
                if (VagonFixedUpdatePatch._cartPins.TryGetValue(uid, out var pin) && pin != null)
                {
                    Minimap.instance?.RemovePin(pin);
                    VagonFixedUpdatePatch._cartPins.Remove(uid);
                }
                if (!nview.IsOwner()) return;
                int level = nview.GetZDO()?.GetInt("ath_cart_level") ?? 0;
                if (level <= 0) return;
                var scene = ZNetScene.instance;
                if (scene == null) return;
                var pos = __instance.transform.position;
                for (int i = 0; i < level && i < CartUpgrade.Costs.Length; i++)
                {
                    foreach (var (itemName, amount) in CartUpgrade.Costs[i])
                    {
                        var prefab = scene.GetPrefab(itemName);
                        if (prefab == null) continue;
                        for (int n = 0; n < amount; n++)
                            scene.SpawnObject(pos + UnityEngine.Random.insideUnitSphere * 0.5f, UnityEngine.Quaternion.identity, prefab);
                    }
                }
            };

        }
    }

    // ── Harmony: ritual discovery on first-time item pickup ──────────────────

    [HarmonyPatch(typeof(Player), "AddKnownItem")]
    [HarmonyPatch(typeof(Game), "Start")]
    [HarmonyPatch(typeof(StoreGui), nameof(StoreGui.Show))]
    static class TraderInteractPatch
    {
        static void Postfix(Trader trader)
        {
            if (trader == null) return;
            Plugin.MarkTraderMet(trader.transform.position);
        }
    }

    static class GameStartRpcPatch
    {
        static void Postfix()
        {
            ZRoutedRpc.instance.Register<Vector3, float>("Pilgrim_SendBird", (_, dir, speed) =>
                Plugin.Scheduler?.SendBird(dir, speed));

            ZRoutedRpc.instance.Register<Vector3, Quaternion, string, float>("Pilgrim_SpawnVfx", (_, pos, rot, prefabName, destroyDelay) =>
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab == null) return;
                Plugin.SpawnVfxLocal(prefab, pos, rot, destroyDelay);
            });

            ZRoutedRpc.instance.Register<string, string>("Pilgrim_SetEnv", (_, envName, fallback) =>
                Plugin.SetDebugEnvSafe(envName, fallback));
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ShowPickupMessage))]
    class RitualDiscoveryPatch
    {
        static void Postfix(Character __instance, ItemDrop.ItemData item)
        {
            var player = __instance as Player;
            if (player == null || player != Player.m_localPlayer) return;
            if (!Plugin.Cfg.Rituals.Enabled) return;
            string prefab = item.m_dropPrefab?.name ?? "";
            if (string.IsNullOrEmpty(prefab)) return;

            // Auto-grant Eikthyr power on first trophy pickup — no item stand available yet in early game
            if (prefab == "TrophyEikthyr" && string.IsNullOrEmpty(player.GetGuardianPowerName()))
            {
                player.SetGuardianPower("GP_Eikthyr");
                player.Message(MessageHud.MessageType.Center, "Eikthyr's power is yours.");
                Plugin.Log.LogInfo("[Pilgrim] Auto-granted GP_Eikthyr on TrophyEikthyr pickup.");
            }

            foreach (var (match, isPrefix, key, display) in Plugin.RitualItemMap)
            {
                bool matches = isPrefix ? prefab.StartsWith(match) : prefab == match;
                if (!matches) continue;
                if (!Plugin.Cfg.Rituals.Items.TryGetValue(key, out var r) || !r.Enabled) continue;
                if (Plugin.IsRitualKnown(player, key)) continue;
                Plugin.LearnRitual(player, key, display);
            }
        }
    }

    // ── Harmony: on load, silently unlock rituals for items already in inventory ──

    [HarmonyPatch(typeof(Player), "OnSpawned")]
    class RitualBackfillPatch
    {
        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!Plugin.Cfg.Rituals.Enabled) return;

            var inv = __instance.GetInventory();
            if (inv == null) return;

            foreach (var invItem in inv.GetAllItems())
            {
                string prefab = invItem.m_dropPrefab?.name ?? "";
                if (string.IsNullOrEmpty(prefab)) continue;
                foreach (var (match, isPrefix, key, _) in Plugin.RitualItemMap)
                {
                    if (Plugin.IsRitualKnown(__instance, key)) continue;
                    if (!Plugin.Cfg.Rituals.Items.TryGetValue(key, out var r) || !r.Enabled) continue;
                    bool matches = isPrefix ? prefab.StartsWith(match) : prefab == match;
                    if (matches)
                        __instance.m_customData[$"ath_known_{key}"] = "1"; // silent — no toast
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player), "Update")]
    static class PlayerHintsTogglePatch
    {
        static readonly string[] CampfirePrefabs = { "fire_pit", "fire_pit_iron", "hearth" };

        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!Input.GetKeyDown(KeyCode.H)) return;
            if (!Plugin.Cfg.Rituals.Enabled) return;

            var hoverObj = __instance.GetHoverObject();
            if (hoverObj == null) return;
            var fp = hoverObj.GetComponentInParent<Fireplace>();
            if (fp == null || !fp.IsBurning()) return;
            string goName = fp.gameObject.name.ToLower().Replace("(clone)", "").Trim();
            if (!System.Array.Exists(CampfirePrefabs, p => goName.StartsWith(p))) return;

            Plugin.ShowHintsEnabled = !Plugin.ShowHintsEnabled;
            __instance.Message(MessageHud.MessageType.TopLeft,
                Plugin.ShowHintsEnabled ? "Offerings shown." : "Offerings hidden.");
        }
    }

    [HarmonyPatch(typeof(MessageHud), nameof(MessageHud.ShowMessage))]
    static class MessageHudTimerPatch
    {
        internal static bool ExtendNextCenter = false;

        static void Postfix(MessageHud __instance, MessageHud.MessageType type)
        {
            if (!ExtendNextCenter) return;
            if (type != MessageHud.MessageType.Center) return;
            ExtendNextCenter = false;
            // Center messages use _crossFadeTextBuffer: two entries (fade-in at time=0, fade-out at time=4).
            // Extend the fade-out duration so the ritual discovery message stays readable longer.
            var bf = BindingFlags.Instance | BindingFlags.NonPublic;
            var bufField = typeof(MessageHud).GetField("_crossFadeTextBuffer", bf);
            if (bufField?.GetValue(__instance) is System.Collections.IList buf && buf.Count > 0)
            {
                var last = buf[buf.Count - 1];
                last?.GetType().GetField("time")?.SetValue(last, 8f);
            }
        }
    }

    static class ItemUtil
    {
        public static int CountByPrefab(Inventory inv, string prefab) =>
            inv.GetAllItems().Where(i => i.m_dropPrefab?.name == prefab).Sum(i => i.m_stack);

        public static void RemoveByPrefab(Inventory inv, string prefab, int amount)
        {
            foreach (var item in inv.GetAllItems().Where(i => i.m_dropPrefab?.name == prefab).ToList())
            {
                int take = Mathf.Min(amount, item.m_stack);
                item.m_stack -= take;
                amount -= take;
                if (item.m_stack <= 0) inv.RemoveItem(item);
                if (amount <= 0) break;
            }
        }
    }

    static class CartUpgrade
    {
        internal static string DisplayName(string prefab) => prefab switch {
            "RoundLog"     => "Core Wood",
            "BronzeNails"  => "Bronze Nails",
            "IronNails"    => "Iron Nails",
            "FineWood"     => "Fine Wood",
            "ElderBark"    => "Elder Bark",
            "LinenThread"  => "Linen Thread",
            "BlackMetal"   => "Black Metal",
            "YggdrasilWood"=> "Yggdrasil Wood",
            "AshWood"      => "Ash Wood",
            _              => prefab,
        };

        // Each level adds one row (6 columns wide)
        public const int BaseWidth = 6;
        public static readonly int[] Heights = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }; // levels 0-10

        internal static readonly (string item, int amount)[][] Costs = {
            new[] { ("Wood",           20), ("BronzeNails", 20) },  // → level 1
            new[] { ("RoundLog",       20), ("BronzeNails", 20) },  // → level 2  (CoreWood)
            new[] { ("FineWood",       10), ("BronzeNails", 20) },  // → level 3
            new[] { ("FineWood",       20), ("BronzeNails", 20) },  // → level 4
            new[] { ("ElderBark",      20), ("IronNails",   20) },  // → level 5
            new[] { ("Chain",           8), ("IronNails",   20) },  // → level 6
            new[] { ("Silver",         10), ("IronNails",   20) },  // → level 7
            new[] { ("LinenThread",    10), ("BlackMetal",  10), ("IronNails", 20) },  // → level 8
            new[] { ("YggdrasilWood",  20), ("Copper",       5), ("IronNails", 20) },  // → level 9
            new[] { ("AshWood",        20), ("Flametal",     5) },  // → level 10
        };

        // Level tints: wood → bronze → corewood → finewood → finewood+ → elder → iron/chain → silver → black metal → mistlands → ashlands
        static readonly Color[] LevelTints = {
            new Color(1.00f, 1.00f, 1.00f), // 0 — default
            new Color(1.00f, 0.82f, 0.55f), // 1 — bronze warmth
            new Color(0.55f, 0.38f, 0.22f), // 2 — corewood dark brown
            new Color(0.95f, 0.88f, 0.72f), // 3 — finewood light
            new Color(0.88f, 0.78f, 0.58f), // 4 — finewood aged
            new Color(0.40f, 0.45f, 0.35f), // 5 — elder bark dark green-grey
            new Color(0.55f, 0.57f, 0.60f), // 6 — iron/chain grey
            new Color(0.88f, 0.94f, 1.00f), // 7 — silver shimmer
            new Color(0.25f, 0.22f, 0.20f), // 8 — black metal
            new Color(0.45f, 0.55f, 0.75f), // 9 — mistlands blue-purple
            new Color(0.80f, 0.40f, 0.15f), // 10 — ashlands ember orange
        };

        public static void ApplyTint(Vagon vagon, int level)
        {
            level = Mathf.Clamp(level, 0, LevelTints.Length - 1);
            var tint = LevelTints[level];
            foreach (var r in vagon.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.materials)
                    if (mat.HasProperty("_Color"))
                        mat.color = tint;
        }

        static readonly BindingFlags _rf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        static System.Reflection.FieldInfo? _wf, _hf;

        // Silent resize — just sets dimensions, no VFX or panel close. Safe to call before inventory loads.
        public static bool ResizeInventory(Inventory inv, int level)
        {
            level = Mathf.Clamp(level, 0, Heights.Length - 1);
            _wf ??= typeof(Inventory).GetField("m_width",  _rf);
            _hf ??= typeof(Inventory).GetField("m_height", _rf);
            if (_wf == null || _hf == null) return false;
            _wf.SetValue(inv, BaseWidth);
            _hf.SetValue(inv, Heights[level]);
            return true;
        }

        public static void ApplySize(Vagon vagon, int level)
        {
            level = Mathf.Clamp(level, 0, Heights.Length - 1);

            var containerField = typeof(Vagon).GetField("m_container", _rf);
            var container = containerField?.GetValue(vagon) as Container;
            var inv = container?.GetInventory();
            if (inv == null) { Plugin.Log.LogWarning("[EnvR] Could not get cart inventory via m_container"); return; }

            if (!ResizeInventory(inv, level)) { Plugin.Log.LogWarning("[EnvR] Inventory m_width/m_height fields not found"); return; }
            InventorySavePatch.CartLevels[inv] = level;
            inv.m_onChanged?.Invoke();

            Plugin.BroadcastVfx(vagon.transform.position, "vfx_Place_cart", 0f);

            // Close the panel — player reopens to see new grid size
            var gui = InventoryGui.instance;
            if (gui != null && gui.IsContainerOpen())
                gui.Hide();
        }

        // Item counting/removal delegated to ItemUtil
        public static int CountByPrefab(Inventory inv, string prefab) => ItemUtil.CountByPrefab(inv, prefab);
        public static void RemoveByPrefab(Inventory inv, string prefab, int amount) => ItemUtil.RemoveByPrefab(inv, prefab, amount);

        public static void Upgrade(Player player, Vagon cart, Terminal.ConsoleEventArgs? args = null)
        {
            var nview = cart.GetComponent<ZNetView>();
            if (nview == null) { args?.Context.AddString("Cart has no network view."); return; }

            int level     = nview.GetZDO()?.GetInt("ath_cart_level") ?? 0;
            int nextLevel = level + 1;

            if (nextLevel >= Heights.Length)
            {
                string msg = "The cart is already fully reinforced.";
                if (args != null) args.Context.AddString(msg);
                else player.Message(MessageHud.MessageType.Center, msg);
                return;
            }

            var cost = Costs[level];
            var inv  = player.GetInventory();
            foreach (var (item, amount) in cost)
            {
                int have = CountByPrefab(inv, item);
                if (have < amount)
                {
                    string msg = $"You lack the {DisplayName(item)}.";
                    if (args != null) args.Context.AddString(msg);
                    else player.Message(MessageHud.MessageType.Center, msg);
                    return;
                }
            }
            foreach (var (item, amount) in cost)
                RemoveByPrefab(inv, item, amount);

            nview.ClaimOwnership();
            nview.GetZDO()!.Set("ath_cart_level", nextLevel);
            ApplySize(cart, nextLevel);
            ApplyTint(cart, nextLevel);

            int slots = BaseWidth * Heights[nextLevel];
            string done = "Cart reinforced.";
            if (args != null) args.Context.AddString(done);
            player.Message(MessageHud.MessageType.Center, done);
        }

        public static Vagon? FindNearest(Player player, float maxDist = 5f)
        {
            Vagon? nearest = null;
            float nearestDist = maxDist;
            foreach (var v in Object.FindObjectsOfType<Vagon>())
            {
                float d = Vector3.Distance(player.transform.position, v.transform.position);
                if (d < nearestDist) { nearestDist = d; nearest = v; }
            }
            return nearest;
        }
    }

    // ── EnvScheduler ────────────────────────────────────────────────────────

    public class EnvScheduler : MonoBehaviour
    {
        public bool   Active      = false;
        public float  Probability = 0.6f;
        public List<string> NightPool = new List<string> { "DreamWalk", "VoidWhisper", "MidnightVeil" };
        public List<string> NoonPool  = new List<string> { "GoldenAscent", "PilgrimDawn", "TropicalHaze" };

        bool    _lastNight;
        bool    _noonRolled;
        string? _setByUs;
        long    _lastNightPeriod = -1;
        long    _lastNoonPeriod  = -1;
        readonly System.Random _rng = new System.Random();

        static readonly KeyCode[] HotbarKeys =
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
            KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8
        };

        static readonly string[] CampfirePrefabs = { "fire_pit", "fire_pit_iron", "hearth" };

        public void RunDelayed(float delay, System.Action action) => StartCoroutine(DelayedAction(delay, action));

        IEnumerator DelayedAction(float delay, System.Action action)
        {
            yield return new WaitForSeconds(delay);
            action();
        }

        void Update()
        {
            // Tick global ritual cooldown
            if (Plugin.RitualCooldownRemaining > 0f)
                Plugin.RitualCooldownRemaining -= Time.deltaTime;

            // Clear skies: cancel if externally overridden or timer expired
            if (Plugin.ClearSkiesExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.ClearSkiesExpiry)
                {
                    if (em != null && em.m_debugEnv == "Clear") em.m_debugEnv = "";
                    Plugin.ClearSkiesExpiry = 0f;
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "The skies begin to darken again.");
                }
                else if (em != null && em.m_debugEnv != "Clear")
                {
                    // Console or another system changed it — respect that and cancel
                    Plugin.ClearSkiesExpiry = 0f;
                }
            }

            // Giant aftermath rain
            if (Plugin.GiantRainExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.GiantRainExpiry)
                {
                    if (em != null && em.m_debugEnv == "Rain") em.m_debugEnv = "";
                    Plugin.GiantRainExpiry = 0f;
                }
                else if (em != null && em.m_debugEnv != "Rain")
                {
                    Plugin.GiantRainExpiry = 0f;
                }
            }

            // Rain: cancel if externally overridden or timer expired
            if (Plugin.RainExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.RainExpiry)
                {
                    if (em != null && em.m_debugEnv == "Rain") em.m_debugEnv = "";
                    Plugin.RainExpiry = 0f;
                    Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, "The rain passes.");
                }
                else if (em != null && em.m_debugEnv != "Rain")
                {
                    Plugin.RainExpiry = 0f;
                }
            }

            // Altar seek env: LastLight
            if (Plugin.SeekEnvExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.SeekEnvExpiry)
                {
                    if (em != null && em.m_debugEnv == "LastLight") em.m_debugEnv = "";
                    Plugin.SeekEnvExpiry = 0f;
                }
                else if (em != null && em.m_debugEnv != "LastLight")
                    Plugin.SeekEnvExpiry = 0f;
            }

            // Dungeon seek env: VoidWhisper
            if (Plugin.DungeonEnvExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.DungeonEnvExpiry)
                {
                    if (em != null && em.m_debugEnv == "VoidWhisper") em.m_debugEnv = "";
                    Plugin.DungeonEnvExpiry = 0f;
                }
                else if (em != null && em.m_debugEnv != "VoidWhisper")
                    Plugin.DungeonEnvExpiry = 0f;
            }

            // Home seek env: DreamWalk
            if (Plugin.HomeEnvExpiry > 0f)
            {
                var em = EnvMan.instance;
                if (Time.time >= Plugin.HomeEnvExpiry)
                {
                    if (em != null && em.m_debugEnv == "DreamWalk") em.m_debugEnv = "";
                    Plugin.HomeEnvExpiry = 0f;
                }
                else if (em != null && em.m_debugEnv != "DreamWalk")
                    Plugin.HomeEnvExpiry = 0f;
            }


            if (Plugin.GiantExpiry > 0f && Time.time >= Plugin.GiantExpiry)
            {
                var gp = Player.m_localPlayer;
                if (gp != null) Plugin.DeactivateGiant(gp);
                else { Plugin.GiantExpiry = 0f; Plugin.GiantTargetScale = 1f; }
            }

            if (Plugin.FlamingSwordExpiry > 0f && Time.time >= Plugin.FlamingSwordExpiry)
            {
                var fp = Player.m_localPlayer;
                if (fp != null) Plugin.DeactivateFlamingSword(fp);
                else Plugin.FlamingSwordExpiry = 0f;
            }

            // Sync local player's target scale into their ZDO so other clients can read it
            var gPlayer = Player.m_localPlayer;
            if (gPlayer != null)
            {
                var localZdo = gPlayer.GetComponent<ZNetView>()?.GetZDO();
                if (localZdo != null)
                    localZdo.Set("ath_scale", Plugin.GiantTargetScale);
            }

            // Lerp ALL players toward their ZDO target scale (covers local + remote)
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == null) continue;
                float target = p.GetComponent<ZNetView>()?.GetZDO()?.GetFloat("ath_scale", 1f) ?? 1f;
                float cur    = p.transform.localScale.x;
                if (Mathf.Approximately(cur, target)) continue;
                float next = Mathf.MoveTowards(cur, target, Time.deltaTime * 0.4f);
                p.transform.localScale = Vector3.one * next;
            }

            if (Plugin.WaterWalkExpiry > 0f)
            {
                if (Time.time >= Plugin.WaterWalkExpiry)
                {
                    Plugin.WaterWalkExpiry = 0f;
                    EnvMan.instance?.ResetDebugWind();
                    var expPlayer = Player.m_localPlayer;
                    if (expPlayer != null)
                    {
                        expPlayer.GetSEMan().RemoveStatusEffect(Plugin.WaterWalkSE?.NameHash() ?? 0);
                        expPlayer.Message(MessageHud.MessageType.TopLeft, "The sea stirs once more.");
                    }
                }
                var wp = Player.m_localPlayer;
                if (wp != null && Plugin.WaterWalkExpiry > 0f)
                {
                    {
                        var rf2 = BindingFlags.Instance | BindingFlags.NonPublic;
                        typeof(Character).GetField("m_inWater",  rf2)?.SetValue(wp, false);
                        typeof(Character).GetField("m_swimming", rf2)?.SetValue(wp, false);

                        var bodyField = typeof(Character).GetField("m_body", rf2);
                        if (bodyField?.GetValue(wp) is Rigidbody rb)
                        {
                            WaterVolume? vol = null;
                            float wl = Floating.GetWaterLevel(wp.transform.position, ref vol);
                            float diff = wl - wp.transform.position.y;
                            bool nearSurface = diff > -0.02f;
                            if (diff > 0.05f && rb.velocity.y <= 0f)
                                rb.velocity = new Vector3(rb.velocity.x, Mathf.Min(diff * 10f, 5f), rb.velocity.z);
                            else if (nearSurface && rb.velocity.y < 0f)
                                rb.velocity = new Vector3(rb.velocity.x, 0f, rb.velocity.z);

                            // Tell the character controller it's on solid ground near the surface
                            if (nearSurface && rb.velocity.y <= 0f)
                            {
                                typeof(Character).GetField("m_groundContact",       rf2)?.SetValue(wp, true);
                                typeof(Character).GetField("m_groundContactNormal", rf2)?.SetValue(wp, Vector3.up);
                            }
                        }
                    }
                }
            }

            var player = Player.m_localPlayer;
            if (player == null) return;

            // G key: instant cart release
            if (Input.GetKeyDown(KeyCode.G))
            {
                // Find any nearby Vagon (cart drags behind player so check generous range)
                Vagon? wagon = null;
                float closest = 15f;
                foreach (var v in Object.FindObjectsOfType<Vagon>())
                {
                    float d = Vector3.Distance(player.transform.position, v.transform.position);
                    if (d < closest) { closest = d; wagon = v; }
                }

                if (wagon != null)
                {
                    wagon.Interact(player, false, false);
                    player.Message(MessageHud.MessageType.TopLeft, "Cart released.");
                }
                else
                {
                    player.Message(MessageHud.MessageType.TopLeft, "[G] No cart nearby.");
                }
            }

            // Number key ritual is handled by UseHotbarItemPatch
        }

        void Start() => StartCoroutine(Loop());

        public void SendBird(Vector3 direction, float speed = 10f)
        {
            var dir = direction.normalized;
            var player = Player.m_localPlayer;

            // Redirect any existing world birds within 150m
            if (player != null)
            {
                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mb == null || mb.GetType().Name != "RandomFlyingBird") continue;
                    if (Vector3.Distance(mb.transform.position, player.transform.position) > 150f) continue;
                    StartCoroutine(DelayedRedirect(mb.gameObject, dir, speed, 10f));
                }
            }

            // Crow sounds at ritual start
            var scene2 = ZNetScene.instance;
            if (scene2 != null && player != null)
            {
                var sfx = scene2.GetPrefab("sfx_crow_death");
                if (sfx != null)
                    for (int s = 0; s < 3; s++)
                        UnityEngine.Object.Instantiate(sfx, player.transform.position + UnityEngine.Random.insideUnitSphere * 3f, Quaternion.identity);
            }

            // Spawn V-formation — 3s base delay so player has time to look up
            const float baseDelay = 3f;
            var right = Vector3.Cross(Vector3.up, dir).normalized;
            Vector3[] offsets = {
                Vector3.zero,
                -right *  3f + dir *  -4f,
                 right *  3f + dir *  -4f,
                -right *  6f + dir *  -8f,
                 right *  6f + dir *  -8f,
                -right *  9f + dir * -12f,
                 right *  9f + dir * -12f,
                -right * 12f + dir * -16f,
                 right * 12f + dir * -16f,
                -right *  2f + dir *  -6f,
                 right *  2f + dir *  -6f,
                -right *  5f + dir * -10f,
                 right *  5f + dir * -10f,
                -right *  8f + dir * -14f,
                 right *  8f + dir * -14f,
                -right * 11f + dir * -18f,
                 right * 11f + dir * -18f,
                -right * 14f + dir * -20f,
                 right * 14f + dir * -20f,
                 Vector3.up  *  4f + dir *  -2f,
                -right *  4f + dir *  -5f + Vector3.up * 1.5f,
                 right *  4f + dir *  -5f + Vector3.up * 1.5f,
                -right *  7f + dir *  -9f + Vector3.up * 1f,
                 right *  7f + dir *  -9f + Vector3.up * 1f,
                -right * 10f + dir * -13f + Vector3.up * 2f,
                 right * 10f + dir * -13f + Vector3.up * 2f,
                -right * 13f + dir * -17f + Vector3.up * 1f,
                 right * 13f + dir * -17f + Vector3.up * 1f,
                -right *  1f + dir *  -3f + Vector3.up * 2.5f,
                 right *  1f + dir *  -3f + Vector3.up * 2.5f,
            };
            for (int i = 0; i < offsets.Length; i++)
                StartCoroutine(SingleBird(dir, speed, offsets[i], baseDelay + i * 0.25f));
        }

        IEnumerator DelayedRedirect(GameObject bird, Vector3 dir, float speed, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (bird != null) StartCoroutine(RedirectWorldBird(bird, dir, speed));
        }

        IEnumerator RedirectWorldBird(GameObject bird, Vector3 dir, float speed)
        {
            if (bird == null) yield break;

            // If bird is landed, swap to flying model before killing the script
            var rf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var mb in bird.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "RandomFlyingBird") continue;
                var flyModel    = mb.GetType().GetField("m_flyingModel", rf)?.GetValue(mb) as GameObject;
                var landModel   = mb.GetType().GetField("m_landedModel", rf)?.GetValue(mb) as GameObject;
                if (flyModel  != null) flyModel.SetActive(true);
                if (landModel != null) landModel.SetActive(false);
                break;
            }

            // Disable only the AI and animation-sync so they stop fighting our redirect.
            // ZSyncTransform must remain enabled — it feeds the bird's moving transform back into
            // the ZDO so the zone updates as the bird flies. If ZSyncTransform is off, the ZDO zone
            // freezes at spawn, RemoveObjects never sweeps it naturally, and when the player walks
            // away the entry becomes a null-gameObject NullRef every frame until they return.
            string[] killTypes = { "RandomFlyingBird", "ZSyncAnimation", "ZSFX" };
            foreach (var mb in bird.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && System.Array.IndexOf(killTypes, mb.GetType().Name) >= 0)
                    mb.enabled = false;
            foreach (var ai in bird.GetComponentsInChildren<BaseAI>(true))
                ai.enabled = false;

            // Set flapping on the now-uncontested animator
            foreach (var comp in bird.GetComponentsInChildren<UnityEngine.Component>())
            {
                var setbool = comp?.GetType().GetMethod("SetBool", new[] { typeof(string), typeof(bool) });
                if (setbool != null) { setbool.Invoke(comp, new object[] { "flapping", true }); break; }
            }
            yield return new WaitForSeconds(0.3f);
            if (bird == null) yield break;

            float elapsed = 0f;
            float wobbleOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            while (bird != null && elapsed < 200f / speed)
            {
                elapsed += Time.deltaTime;
                var right2 = Vector3.Cross(Vector3.up, dir).normalized;
                Vector3 wobble = right2 * Mathf.Sin(elapsed * 1.3f + wobbleOffset) * 0.4f
                               + Vector3.up * Mathf.Sin(elapsed * 0.9f + wobbleOffset + 1f) * 0.2f;
                bird.transform.position += (dir + wobble) * speed * Time.deltaTime;
                bird.transform.rotation = Quaternion.LookRotation(dir + wobble * 0.5f);
                yield return null;
            }
            if (bird != null)
            {
                if (ZNetScene.instance != null)
                {
                    // ZNetScene.Destroy(GameObject) removes from m_instances before destroying.
                    // Compiler resolves instance.Destroy() to inherited static Object.Destroy, so reflect.
                    // Use GetMethods() loop — exact-type GetMethod can return null due to overload resolution.
                    System.Reflection.MethodInfo? dm = null;
                    foreach (var m in typeof(ZNetScene).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "Destroy") continue;
                        var p = m.GetParameters();
                        if (p.Length == 1 && p[0].ParameterType == typeof(GameObject)) { dm = m; break; }
                    }
                    if (dm != null) dm.Invoke(ZNetScene.instance, new object[] { bird });
                    else Object.Destroy(bird);
                }
                else Object.Destroy(bird);
            }
        }

        IEnumerator SingleBird(Vector3 dir, float speed, Vector3 formationOffset, float delay)
        {
            var scene = ZNetScene.instance;
            if (scene == null) yield break;
            var prefab = scene.GetPrefab("Crow");
            if (prefab == null) yield break;
            var player = Player.m_localPlayer;
            if (player == null) yield break;

            // Initial delay for V stagger
            yield return new WaitForSeconds(delay);

            // Spawn 100m back from the player along the flight path, 15m up
            float altitude = UnityEngine.Random.Range(22f, 28f);
            Vector3 anchor = player.transform.position + Vector3.up * altitude;
            Vector3 spawnPos = anchor - dir * 100f + formationOffset;

            // Instantiate inactive so ZNetView.Awake never fires and ZNetScene never registers it
            prefab.SetActive(false);
            var bird = Object.Instantiate(prefab, spawnPos, Quaternion.LookRotation(dir));
            prefab.SetActive(true);
            // Destroy all network-aware components before activation so their Awake never fires
            string[] killTypes = { "ZNetView", "ZSyncAnimation", "ZSyncTransform", "RandomFlyingBird", "ZSFX" };
            foreach (var mb in bird.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && System.Array.IndexOf(killTypes, mb.GetType().Name) >= 0)
                    Object.DestroyImmediate(mb);
            foreach (var nv in bird.GetComponentsInChildren<ZNetView>(true))
                Object.DestroyImmediate(nv);
            foreach (var ai in bird.GetComponentsInChildren<BaseAI>(true))
                ai.enabled = false;
            bird.SetActive(true);

            // Crow animator has one param: flapping(Bool) — set true for flying
            var animComp = bird.GetComponentsInChildren<UnityEngine.Component>()
                .FirstOrDefault(c => c.GetType().Name == "Animator");
            animComp?.GetType().GetMethod("SetBool", new[] { typeof(string), typeof(bool) })
                     ?.Invoke(animComp, new object[] { "flapping", true });

            // Fly until 150m past the spawn origin (covers player + 50m beyond)
            float elapsed = 0f;
            float wobbleOffset = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            while (bird != null && elapsed < 250f / speed)
            {
                elapsed += Time.deltaTime;
                // Gentle sine wobble perpendicular to flight
                var right2 = Vector3.Cross(Vector3.up, dir).normalized;
                Vector3 wobble = right2 * Mathf.Sin(elapsed * 1.3f + wobbleOffset) * 0.4f
                               + Vector3.up * Mathf.Sin(elapsed * 0.9f + wobbleOffset + 1f) * 0.2f;
                bird.transform.position += (dir + wobble) * speed * Time.deltaTime;
                bird.transform.rotation = Quaternion.LookRotation(dir + wobble * 0.5f);
                yield return null;
            }

            if (bird != null) Object.Destroy(bird);
        }

        IEnumerator Loop()
        {
            yield return new WaitForSeconds(10f);
            while (true)
            {
                yield return new WaitForSeconds(5f);
                if (!Active || EnvMan.instance == null || ZNet.instance == null) continue;

                bool  isNight  = EnvMan.IsNight();
                float tod      = Plugin.GetTod();
                long  dayCount = (long)(ZNet.instance.GetTimeSeconds() / 1800f);

                if (isNight)
                {
                    if (_lastNightPeriod != dayCount)
                    {
                        _lastNightPeriod = dayCount;
                        ClearIfOurs();
                        TrySet(NightPool);
                    }
                }
                else
                {
                    if (_lastNight) ClearIfOurs();

                    if (tod >= 0.47f && tod <= 0.53f && _lastNoonPeriod != dayCount)
                    {
                        _lastNoonPeriod = dayCount;
                        ClearIfOurs();
                        TrySet(NoonPool);
                    }
                }

                _lastNight = isNight;
            }
        }

        void TrySet(List<string> pool)
        {
            if (pool.Count == 0 || _rng.NextDouble() > Probability) return;
            if (Plugin.ClearSkiesExpiry > 0f && Time.time < Plugin.ClearSkiesExpiry) return;
            if (Plugin.RainExpiry > 0f && Time.time < Plugin.RainExpiry) return;
            var pick = pool[_rng.Next(pool.Count)];
            EnvMan.instance.m_debugEnv = pick;
            _setByUs = pick;
        }

        void ClearIfOurs()
        {
            var em = EnvMan.instance;
            if (em != null && em.m_debugEnv == _setByUs)
                em.m_debugEnv = "";
            _setByUs = null;
        }

    }

    // ── Config classes ───────────────────────────────────────────────────────

    public class PilgrimConfig
    {
        public TrophiesConfig  Trophies { get; set; } = new TrophiesConfig();
        public CartsConfig     Carts    { get; set; } = new CartsConfig();
        public ShipsConfig     Ships    { get; set; } = new ShipsConfig();
        public RitualsConfig   Rituals  { get; set; } = new RitualsConfig();

        public static PilgrimConfig Default() => new PilgrimConfig
        {
            Trophies = new TrophiesConfig { Enabled = true, Vfx = "fx_fireskeleton_nova" },
            Carts    = new CartsConfig    { Enabled = true },
            Ships    = new ShipsConfig    { Enabled = true },
            Rituals  = new RitualsConfig
            {
                Enabled  = true,
                Cooldown = 60f,
                Items    = new Dictionary<string, RitualItemConfig>
                {
                    ["seek_altar"]    = new RitualItemConfig { Enabled = true, Item = "RawMeat",    HoverText = "Seek the next altar",       Message = "The wind stirs — {boss} awaits." },
                    ["restore_power"] = new RitualItemConfig { Enabled = true, Item = "Mushroom*",  HoverText = "Restore your power",         Message = "The flame accepts your offering. {power} is ready.", Duration = 600f },
                    ["seek_bed"]      = new RitualItemConfig { Enabled = true, Item = "Dandelion",  HoverText = "Seek your bed",              Message = "The flower carries you home..." },
                    ["feather_fall"]  = new RitualItemConfig { Enabled = true, Item = "Feathers",   HoverText = "Fall without fear",          Message = "Light as a feather — fall without fear." },
                    ["seek_trader"]   = new RitualItemConfig { Enabled = true, Item = "Coins",      HoverText = "Seek a merchant",            Message = "Gold calls to gold..." },
                    ["seek_dungeon"]  = new RitualItemConfig { Enabled = true, Item = "Trophy*",    HoverText = "Seek the nearest dungeon",   Message = "The veil parts — something stirs nearby." },
                    ["clear_skies"]   = new RitualItemConfig { Enabled = true, Item = "GreydwarfEye", HoverText = "Clear the skies",          Message = "The clouds part.", Duration = 900f },
                    ["water_walk"]    = new RitualItemConfig { Enabled = true, Item = "Stone",      HoverText = "Walk on water",              Message = "The sea grows still beneath your feet.", Duration = 60f },
                    ["growth"]        = new RitualItemConfig { Enabled = true, Item = "AncientSeed",HoverText = "Bless your crops",           Message = "The seed remembers the earth. Sleep, and your crops will answer." },
                    ["seek_player"]   = new RitualItemConfig { Enabled = true, Item = "Flint",      HoverText = "Seek a fellow pilgrim",      Message = "Find fellowship." },
                    ["kindle"]        = new RitualItemConfig { Enabled = true, Item = "Resin",      HoverText = "Kindle nearby fires",        Message = "The darkness yields." },
                    ["tame_flock"]    = new RitualItemConfig { Enabled = true, Item = "BoneFragments", HoverText = "Tame the flock",          Message = "The bones remember loyalty. Sleep, and your flock will answer." },
                    ["giant"]         = new RitualItemConfig { Enabled = true, Item = "YmirRemains",   HoverText = "Become the mountain",      Message = "The mountain answers. You are vast.", Duration = 60f },
                    ["flaming_sword"] = new RitualItemConfig { Enabled = true, Item = "SurtlingCore", HoverText = "Summon Dyrnwyn", Message = "Dyrnwyn answers. Let it burn.", Duration = 60f },
                },
            },
        };
    }

    public class TrophiesConfig
    {
        public bool   Enabled { get; set; } = true;
        public string Vfx     { get; set; } = "fx_fireskeleton_nova";
    }

    public class CartsConfig
    {
        public bool Enabled { get; set; } = true;
    }

    public class ShipsConfig
    {
        public bool Enabled { get; set; } = true;
    }

    public class RitualsConfig
    {
        public bool   Enabled   { get; set; } = true;
        public float  Cooldown  { get; set; } = 60f;
        public bool   ShowHints { get; set; } = true;
        public Dictionary<string, RitualItemConfig> Items { get; set; } = new Dictionary<string, RitualItemConfig>();
    }

    public class RitualItemConfig
    {
        public bool   Enabled   { get; set; } = true;
        public string Item      { get; set; } = "";
        public string HoverText { get; set; } = "";
        public string Message   { get; set; } = "";
        public float  Duration  { get; set; } = 0f;
    }
}
