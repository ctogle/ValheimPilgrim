using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Level-up VFX: spawns one or more game VFX prefabs on the player, with an optional
// particle-density multiplier. Prefab name(s) + density are configurable (BoonCfg), so
// candidates can be auditioned live (e.g. vfx_frostfoundary_transform, fx_Adrenaline1,
// fx_frozenking_aspectspawn — comma-separate to mix).
namespace EnvReporter
{
    internal static class BoonFx
    {
        static readonly string[] NetTypes = { "ZNetView", "ZSyncTransform", "ZSyncAnimation", "TimedDestruction" };

        static bool _dumped;

        // Config format: entries separated by ';', each "name [key=val ...]".
        // keys: density, scale, lifetime, delay, tint=r,g,b   e.g.
        //   fx_frozenking_aspectspawn density=3; vfx_WishbonePing density=3 tint=0.5,0.8,1; sfx_x delay=1.5
        internal static void PlayLevelUp(Player p)
        {
            if (p == null || ZNetScene.instance == null || string.IsNullOrEmpty(BoonCfg.LevelUpVfx)) return;
            bool any = false;
            foreach (var seg in BoonCfg.LevelUpVfx.Split(';'))
            {
                var tok = seg.Trim().Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length == 0) continue;
                string name = tok[0];
                float density = BoonCfg.LevelUpVfxDensity, scale = 1f, life = 1f, delay = 0f;
                Color? tint = null;
                for (int i = 1; i < tok.Length; i++)
                {
                    var kv = tok[i].Split('='); if (kv.Length != 2) continue;
                    var v = kv[1];
                    switch (kv[0].ToLowerInvariant())
                    {
                        case "density": float.TryParse(v, out density); break;
                        case "scale": float.TryParse(v, out scale); break;
                        case "life": case "lifetime": float.TryParse(v, out life); break;
                        case "delay": float.TryParse(v, out delay); break;
                        case "tint":
                            var c = v.Split(',');
                            if (c.Length >= 3 && float.TryParse(c[0], out var r) && float.TryParse(c[1], out var g) && float.TryParse(c[2], out var b))
                                tint = new Color(r, g, b);
                            break;
                    }
                }
                if (delay <= 0f) { if (SpawnByName(name, density, scale, life, tint)) any = true; }
                else { string n = name; float d = density, s = scale, l = life; Color? t = tint; Plugin.Scheduler?.RunDelayed(delay, () => SpawnByName(n, d, s, l, t)); any = true; }
            }
            if (!any) DumpCandidates();
        }

        // the game's own inventory open/close sound (EffectList on InventoryGui)
        internal static void PlayUi(bool open)
        {
            var gui = InventoryGui.instance; if (gui == null) return;
            var fx = open ? gui.m_openInventoryEffects : gui.m_closeInventoryEffects;
            var pos = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            fx?.Create(pos, Quaternion.identity);
        }

        // one-shot sound (ZSFX plays on spawn) — used when a boon is picked
        internal static void PlaySound(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var p = Player.m_localPlayer; var zs = ZNetScene.instance;
            if (p == null || zs == null) return;
            var prefab = zs.GetPrefab(name);
            if (prefab == null) { Plugin.Log?.LogWarning($"[Pilgrim] pick sfx not found: '{name}'"); return; }
            Spawn(prefab, p.transform, 1f, 1f, 1f, null);
        }

        static bool SpawnByName(string name, float density, float scale, float life, Color? tint)
        {
            var p = Player.m_localPlayer; var zs = ZNetScene.instance;
            if (p == null || zs == null) return false;
            var prefab = zs.GetPrefab(name);
            if (prefab == null) { Plugin.Log?.LogWarning($"[Pilgrim] level-up VFX not found: '{name}'"); return false; }
            Spawn(prefab, p.transform, density, scale, life, tint);
            Plugin.Log?.LogInfo($"[Pilgrim] level-up VFX: {name} density={density} scale={scale} life={life} tint={(tint.HasValue ? tint.Value.ToString() : "none")}");
            return true;
        }

        // One-time log of real vfx/fx prefab names so a valid one can be chosen in config.
        static void DumpCandidates()
        {
            if (_dumped) return; _dumped = true;
            var zs = ZNetScene.instance; if (zs == null) return;
            string[] keys = { "transform", "frost", "found", "adrenaline", "aspect", "wishbone",
                              "skill", "activat", "lvl", "upgrade", "guardian", "spawn", "glow", "burst" };
            var names = zs.m_prefabs.Where(g => g != null).Select(g => g.name)
                .Where(n => { var l = n.ToLowerInvariant(); return l.StartsWith("vfx") || l.StartsWith("fx"); })
                .Where(n => keys.Any(k => n.ToLowerInvariant().Contains(k)))
                .OrderBy(n => n).Take(80).ToList();
            Plugin.Log?.LogWarning($"[Pilgrim] no level-up VFX resolved. Candidate prefabs ({names.Count}): {string.Join(", ", names)}");
        }

        // ---- console helpers: browse & preview VFX live ----
        internal static List<string> ListVfx(string kw)
        {
            var zs = ZNetScene.instance; if (zs == null) return new List<string>();
            kw = (kw ?? "").ToLowerInvariant();
            return zs.m_prefabs.Where(g => g != null).Select(g => g.name)
                .Where(n => { var l = n.ToLowerInvariant(); return (l.StartsWith("vfx") || l.StartsWith("fx") || l.StartsWith("sfx")) && (kw.Length == 0 || l.Contains(kw)); })
                .OrderBy(n => n).ToList();
        }

        internal static string Preview(string name)
        {
            var p = Player.m_localPlayer; if (p == null) return "no player";
            var zs = ZNetScene.instance; if (zs == null) return "scene not ready";
            var prefab = zs.GetPrefab(name); if (prefab == null) return $"not found: '{name}'";
            Spawn(prefab, p.transform, BoonCfg.LevelUpVfxDensity, 1f, 1f, null);
            return $"spawned '{name}' (density {BoonCfg.LevelUpVfxDensity})";
        }

        // live playground: spawn in front of the player with arbitrary params
        internal static string PreviewTuned(string name, float scale, float density, float lifetime, Color? tint)
        {
            var p = Player.m_localPlayer; if (p == null) return "no player";
            var zs = ZNetScene.instance; if (zs == null) return "scene not ready";
            var prefab = zs.GetPrefab(name); if (prefab == null) return $"not found: '{name}'";
            Spawn(prefab, p.transform, density, scale, lifetime, tint);
            return $"spawned '{name}'  scale={scale}  density={density}  lifetime={lifetime}  tint={(tint.HasValue ? tint.Value.ToString() : "none")}";
        }

        static void Spawn(GameObject prefab, Transform follow, float density, float scale, float lifetime, Color? tint)
        {
            // instantiate inactive so ZNetView.Awake never fires (same pattern as Plugin.SpawnVfxLocal)
            prefab.SetActive(false);
            var go = Object.Instantiate(prefab, follow.position + Vector3.up * 0.5f, follow.rotation); // lift ~1/2m
            prefab.SetActive(true);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && System.Array.IndexOf(NetTypes, mb.GetType().Name) >= 0)
                    Object.DestroyImmediate(mb);

            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (Mathf.Abs(density - 1f) > 0.001f) ScaleDensity(ps, density);
                var main = ps.main;
                if (tint.HasValue) main.startColor = new ParticleSystem.MinMaxGradient(tint.Value);
                if (Mathf.Abs(lifetime - 1f) > 0.001f)
                {
                    var sl = main.startLifetime; sl.constant *= lifetime; sl.constantMin *= lifetime; sl.constantMax *= lifetime;
                    main.startLifetime = sl;
                }
            }
            if (Mathf.Abs(scale - 1f) > 0.001f) go.transform.localScale = Vector3.one * scale;

            go.transform.SetParent(follow, worldPositionStays: true);  // ride along with the player
            go.SetActive(true);
            Object.Destroy(go, 8f);
        }

        internal static void ScaleDensity(GameObject go, float m)
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ScaleDensity(ps, m);
        }

        static void ScaleDensity(ParticleSystem ps, float m)
        {
            var em = ps.emission;
            var rate = em.rateOverTime;
            rate.constant *= m; rate.constantMin *= m; rate.constantMax *= m;
            em.rateOverTime = rate;

            int bc = em.burstCount;
            if (bc > 0)
            {
                var bursts = new ParticleSystem.Burst[bc];
                em.GetBursts(bursts);
                for (int i = 0; i < bc; i++)
                {
                    var cnt = bursts[i].count;
                    cnt.constant *= m; cnt.constantMin *= m; cnt.constantMax *= m;
                    bursts[i].count = cnt;
                }
                em.SetBursts(bursts);
            }

            var main = ps.main;
            main.maxParticles = Mathf.CeilToInt(main.maxParticles * Mathf.Max(1f, m));
        }
    }
}
