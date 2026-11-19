using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
//  Pilgrim · Boons — uGUI HUD (xp bar + diamond "XP" icon).
//
//  Real Unity UI on its own overlay canvas: crisp rendering, the game's TMP font,
//  a generated diamond sprite with a rim, and a smooth shimmer (not a harsh blink)
//  when a boon is waiting. Positioned each frame relative to the food-bar slots.
//
//  First uGUI piece — the chooser + character sheet port follow. Positions are
//  driven by BoonCfg nudges so they're easy to dial in-game.
// ─────────────────────────────────────────────────────────────────────────────
namespace EnvReporter
{
    internal sealed class BoonHudUI : MonoBehaviour
    {
        static BoonHudUI _inst;
        internal static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("PilgrimBoonCanvas");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<BoonHudUI>();
        }

        Canvas _canvas;
        CanvasGroup _group;
        TMP_FontAsset _font;
        Sprite _white, _grad, _diamond;
        RectTransform _barBg;
        Image _barFill;
        RectTransform _iconRt;
        Image _iconImg;
        Image _ringImg; RectTransform _ringRt;   // expanding "ping" ring
        TextMeshProUGUI _iconTxt, _count;
        bool _built;

        static readonly Color Gold   = new Color(0.90f, 0.75f, 0.40f);
        static readonly Color GoldHi = new Color(1.00f, 0.92f, 0.60f);
        static readonly Color Muted  = new Color(0.62f, 0.54f, 0.36f);

        TMP_FontAsset FindFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts.FirstOrDefault(f => f.name.Contains("Norse"))
                ?? fonts.FirstOrDefault(f => f.name.Contains("Averia"))
                ?? fonts.FirstOrDefault();
        }

        static Sprite SolidSprite(Color c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var px = new Color[4]; for (int i = 0; i < 4; i++) px[i] = c; t.SetPixels(px); t.Apply();
            return Sprite.Create(t, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));
        }

        static Sprite GradSprite()   // bright→dark left→right, mirrors the health bar
        {
            const int N = 64;
            var t = new Texture2D(N, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var hi = new Color(0.98f, 0.86f, 0.46f); var lo = new Color(0.52f, 0.38f, 0.14f);
            for (int i = 0; i < N; i++) t.SetPixel(i, 0, Color.Lerp(hi, lo, i / (float)(N - 1)));
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, N, 1), new Vector2(0.5f, 0.5f));
        }

        static Sprite DiamondSprite(int size = 48)   // filled gem with a robust rim
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) / 2f;
            var rim  = new Color(0.96f, 0.84f, 0.46f, 1f);
            var fill = new Color(0.28f, 0.21f, 0.09f, 1f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = (Mathf.Abs(x - c) + Mathf.Abs(y - c)) / c;  // 0 center → 1 at edge
                    Color col = d > 1f ? new Color(0, 0, 0, 0) : d > 0.78f ? rim : fill;
                    t.SetPixel(x, y, col);
                }
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        Image NewImage(string name, Transform parent, Sprite spr, Color col, bool filled = false)
        {
            var g = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            g.transform.SetParent(parent, false);
            var img = g.GetComponent<Image>();
            img.sprite = spr; img.color = col; img.raycastTarget = false;
            if (filled) { img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Vertical; img.fillOrigin = (int)Image.OriginVertical.Bottom; }
            return img;
        }

        TextMeshProUGUI NewText(string name, Transform parent, int size, Color col)
        {
            var g = new GameObject(name, typeof(RectTransform));
            g.transform.SetParent(parent, false);
            var t = g.AddComponent<TextMeshProUGUI>();
            t.font = _font; t.fontSize = size; t.color = col; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            return t;
        }

        void Build()
        {
            _font = FindFont();
            _white = SolidSprite(Color.white); _grad = GradSprite(); _diamond = DiamondSprite();

            var cgo = new GameObject("Canvas", typeof(RectTransform));
            cgo.transform.SetParent(transform, false);
            _canvas = cgo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 5000;
            _group = cgo.AddComponent<CanvasGroup>();   // alpha mirrors the HUD's death/teleport black-screen fade
            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; scaler.scaleFactor = 1f;
            cgo.AddComponent<GraphicRaycaster>();
            var root = cgo.transform;

            // bar: track + fill + quarter ticks
            _barBg = NewImage("XpTrack", root, _white, new Color(0, 0, 0, 0.85f)).rectTransform;
            _barBg.pivot = new Vector2(0, 0); _barBg.anchorMin = _barBg.anchorMax = new Vector2(0, 0);
            _barFill = NewImage("XpFill", _barBg, _grad, Color.white, filled: true);
            var fr = _barFill.rectTransform; fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = Vector2.zero; fr.offsetMax = Vector2.zero;
            for (int q = 1; q < 4; q++)
            {
                var tick = NewImage($"Tick{q}", _barBg, _white, new Color(0, 0, 0, 0.5f)).rectTransform;
                tick.anchorMin = new Vector2(0, q / 4f); tick.anchorMax = new Vector2(1, q / 4f);
                tick.pivot = new Vector2(0.5f, 0.5f); tick.sizeDelta = new Vector2(0, 1); tick.anchoredPosition = Vector2.zero;
            }

            // ping ring (behind the icon; created first so it renders underneath)
            _ringImg = NewImage("XpRing", root, _diamond, new Color(1, 1, 1, 0));
            _ringRt = _ringImg.rectTransform; _ringRt.pivot = new Vector2(0.5f, 0.5f);
            _ringRt.anchorMin = _ringRt.anchorMax = new Vector2(0, 0);
            _ringImg.gameObject.SetActive(false);

            // diamond icon + "XP" + pending count
            _iconImg = NewImage("XpIcon", root, _diamond, Muted);
            _iconImg.raycastTarget = true;                        // clickable: toggles the boon chooser
            var iconBtn = _iconImg.gameObject.AddComponent<Button>();
            iconBtn.transition = Selectable.Transition.None;
            // 0.6.1: click the XP diamond = open/close the boon chooser (ToggleDraw no-ops when Pending==0).
            // (Only reachable when a cursor is already up, e.g. inventory open — so keep it to the boon panel.)
            iconBtn.onClick.AddListener(() => BoonHud.ToggleDraw());
            _iconRt = _iconImg.rectTransform; _iconRt.pivot = new Vector2(0.5f, 0.5f);
            _iconRt.anchorMin = _iconRt.anchorMax = new Vector2(0, 0);
            _iconTxt = NewText("XpLabel", _iconRt, 14, new Color(0.14f, 0.10f, 0.04f));
            _iconTxt.text = "XP"; _iconTxt.fontStyle = FontStyles.Bold;
            var lt = _iconTxt.rectTransform; lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one; lt.offsetMin = Vector2.zero; lt.offsetMax = Vector2.zero;
            // 0.6.1: hover hint — symmetric, key-referenced; parented to the icon so it tracks layout
            var tip = NewText("XpTip", _iconRt, 12, GoldHi);
            tip.text = $"[B] boons   [{CharSheetCfg.ToggleKey}] sheet";
            tip.fontStyle = FontStyles.Bold; tip.enableWordWrapping = false; tip.alignment = TextAlignmentOptions.Left;
            tip.outlineColor = Color.black; tip.outlineWidth = 0.2f;
            // below-and-right of the diamond so it never runs off the top/left edge
            var trt = tip.rectTransform; trt.pivot = new Vector2(0f, 1f); trt.anchorMin = trt.anchorMax = new Vector2(1f, 0f);
            trt.sizeDelta = new Vector2(220f, 20f); trt.anchoredPosition = new Vector2(4f, -4f);
            tip.gameObject.SetActive(false);
            var et = _iconImg.gameObject.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var tEnter = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            tEnter.callback.AddListener(_ => { if (tip != null) tip.gameObject.SetActive(true); });
            et.triggers.Add(tEnter);
            var tExit = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            tExit.callback.AddListener(_ => { if (tip != null) tip.gameObject.SetActive(false); });
            et.triggers.Add(tExit);
            _count = NewText("XpCount", root, 12, GoldHi);
            _count.rectTransform.pivot = new Vector2(0, 0.5f); _count.rectTransform.anchorMin = _count.rectTransform.anchorMax = new Vector2(0, 0);

            _built = true;
        }

        // food-bar screen rect (y-up pixels)
        bool FoodRect(out float left, out float bottom, out float h)
        {
            left = bottom = h = 0f;
            var root = Hud.instance?.m_foodBarRoot;
            if (root == null) return false;
            var c = new Vector3[4]; root.GetWorldCorners(c);
            left = c[0].x; bottom = c[0].y; h = c[1].y - c[0].y;
            return h > 1f;
        }

        void Update()
        {
            var st = BoonHud.State; var p = Player.m_localPlayer;
            // 0.6.1: mirror the HEALTH bar's actual visibility. The game doesn't deactivate the HUD — Hud.SetVisible
            // MOVES m_rootObject off-screen — so activeInHierarchy stays true even when hidden (why the XP bar showed
            // during the intro/cutscene while other bars didn't). The real rule (Hud.Update) is:
            //   !Hud.IsUserHidden() && !localPlayer.InCutscene()   (+ our existing !teleporting)
            bool hudShown = Hud.instance != null && !Hud.IsUserHidden()
                            && p != null && !p.InCutscene();
            bool show = BoonCfg.Enabled && p != null && st != null && hudShown;
            if (!_built) { if (!show) return; Build(); }
            _canvas.gameObject.SetActive(show);
            if (!show) return;
            // Ride the SAME black-screen fade that occludes the health/food bars. Hud.m_loadingScreen is a full-
            // screen black CanvasGroup that fades IN (alpha 0→1) on death/teleport/sleep/shutdown; the real bars
            // don't hide, they get covered by it. Our canvas renders above it, so instead mirror its alpha — fade
            // our bar out in lockstep (1 - loadingAlpha) so it vanishes exactly as the screen goes black, not before.
            float blackAlpha = Hud.instance != null && Hud.instance.m_loadingScreen != null ? Hud.instance.m_loadingScreen.alpha : 0f;
            if (_group != null) _group.alpha = Mathf.Clamp01(1f - blackAlpha);

            float barW = BoonCfg.XpBarWidth, gap = 6f;
            float left, bottom, h;
            if (!FoodRect(out left, out bottom, out h)) { h = 120f; left = 80f; bottom = 80f; }
            float isz = Mathf.Clamp(h * 0.26f * 1.4f, 36f, 56f);  // ~matches a HUD icon
            // up by ~half an icon, left by ~an icon width, off the food bar
            float barX = left - barW - gap - isz * 0.9f + BoonCfg.XpBarNudgeX;
            float barY = bottom + isz * 0.55f + BoonCfg.XpBarNudgeY;
            _barBg.anchoredPosition = new Vector2(barX, barY);
            _barBg.sizeDelta = new Vector2(barW, h);
            _barFill.fillAmount = Mathf.Clamp01((float)st.Xp / BoonCfg.NeedForNext(st.Level));

            _iconRt.sizeDelta = new Vector2(isz, isz);
            Vector2 iconPos = new Vector2(barX + barW / 2f, barY - isz / 2f - 6f);

            bool ready = st.Pending > 0;
            float t = Time.unscaledTime;
            float s = Mathf.PingPong(t * 1.6f, 1f);
            Color col = ready ? Color.Lerp(Gold, GoldHi, s) : Gold;
            _iconRt.localScale = Vector3.one; _iconRt.localRotation = Quaternion.identity;
            bool ping = false;

            if (ready)
            {
                switch ((BoonCfg.BoonAlert ?? "pulse").ToLowerInvariant())
                {
                    case "throb": _iconRt.localScale = Vector3.one * (1f + 0.22f * s); break;
                    case "bob":   iconPos += new Vector2(0, Mathf.Sin(t * 5f) * 6f); _iconRt.localScale = Vector3.one * (1f + 0.07f * s); break;
                    case "spin":  _iconRt.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 4f) * 20f); _iconRt.localScale = Vector3.one * (1f + 0.07f * s); break;
                    case "shake": iconPos += new Vector2(Mathf.Sin(t * 40f) * 2f, 0); break;
                    case "ping":  ping = true; _iconRt.localScale = Vector3.one * (1f + 0.05f * s); break;
                    default:      _iconRt.localScale = Vector3.one * (1f + 0.07f * s); break;  // "pulse"
                }
            }
            _iconRt.anchoredPosition = iconPos;
            _iconImg.color = col;
            _iconTxt.color = col;
            _iconTxt.text = ready ? st.Pending.ToString() : "XP";   // count while waiting, else "XP"
            _count.gameObject.SetActive(false);

            // expanding ping ring
            if (ping)
            {
                float pt = (t * 1.2f) % 1f;                          // 0→1 loop
                _ringImg.gameObject.SetActive(true);
                _ringRt.anchoredPosition = iconPos;
                _ringRt.sizeDelta = Vector2.one * isz * (1f + pt * 1.2f);
                _ringImg.color = new Color(GoldHi.r, GoldHi.g, GoldHi.b, (1f - pt) * 0.55f);
            }
            else if (_ringImg.gameObject.activeSelf) _ringImg.gameObject.SetActive(false);
        }
    }
}
