using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// uGUI boon chooser — one wood panel with a title, three boon buttons (native button bg +
// structured content: rarity, name, color band, give/take), and a reroll button.
namespace EnvReporter
{
    internal sealed class BoonChooserUI : MonoBehaviour
    {
        static BoonChooserUI _inst;
        internal static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("PilgrimBoonChooser");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<BoonChooserUI>();
        }

        // 5 tiers, D2-flavored colors: white/tan, blue, yellow, orange, deep-orange (legendary)
        static readonly Color[] RarityCol = {
            new Color(0.85f,0.82f,0.72f),  // common      — tan/white
            new Color(0.42f,0.62f,0.96f),  // uncommon    — blue
            new Color(0.96f,0.86f,0.32f),  // rare        — yellow
            new Color(0.96f,0.56f,0.22f),  // exceptional — orange
            new Color(1.00f,0.44f,0.12f),  // legendary   — deep orange
        };
        static readonly string[] RarityName = { "Common", "Uncommon", "Rare", "Exceptional", "Legendary" };

        const float BW = 244f, BH = 214f, BGAP = 16f, PAD = 24f, TITLE_H = 48f, REROLL_H = 46f;

        GameObject _root;
        bool _built;
        float _basePPU = 1f;
        Image _panel;
        TextMeshProUGUI _title;
        int _hoverIdx = -1;   // which card the cursor is over (-1 = none)
        Button _rerollBtn;
        TextMeshProUGUI _rerollLabel;

        class BoonBtn { public Button btn; public Image bg; public Image hi; public Image halo; public Image[] border; public TextMeshProUGUI rarity, name, give, take; }
        readonly BoonBtn[] _boons = new BoonBtn[3];

        void BuildUI()
        {
            var gameCanvas = (InventoryGui.instance != null ? InventoryGui.instance.GetComponentInParent<Canvas>() : null)
                          ?? (Hud.instance != null ? Hud.instance.GetComponentInParent<Canvas>() : null);
            Transform parent;
            if (gameCanvas != null)
            {
                _root = new GameObject("PilgrimBoonChooserRoot", typeof(RectTransform));
                _root.transform.SetParent(gameCanvas.transform, false);
                var rrt = (RectTransform)_root.transform;
                rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
                _root.transform.SetAsLastSibling();
                parent = _root.transform;
            }
            else
            {
                var cgo = new GameObject("Canvas", typeof(RectTransform));
                cgo.transform.SetParent(transform, false);
                var cv = cgo.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 5100;
                cgo.AddComponent<CanvasScaler>(); cgo.AddComponent<GraphicRaycaster>();
                _root = cgo; parent = cgo.transform;
            }

            var dim = UiKit.Img("Dim", parent, UiKit.Solid(), new Color(0, 0, 0, 0.6f));
            dim.raycastTarget = true; UiKit.Stretch(dim.rectTransform);
            // click anywhere off the panel (the dim backdrop) closes the chooser
            var dimEt = dim.gameObject.AddComponent<EventTrigger>();
            AddTrigger(dimEt, EventTriggerType.PointerClick, () => BoonHud.RequestClose());

            float panelW = 3 * BW + 2 * BGAP + 2 * PAD;
            float panelH = PAD + TITLE_H + 10f + BH + 16f + REROLL_H + PAD;
            _panel = UiKit.Img("ChooserPanel", parent, UiKit.Solid(), Color.white, Image.Type.Sliced);
            var panelSprite = string.IsNullOrEmpty(BoonCfg.ChooserPanelSprite) ? CharSheetCfg.PanelSprite : BoonCfg.ChooserPanelSprite;
            UiKit.StylePanel(_panel, panelSprite);
            _panel.type = Image.Type.Sliced;         // Sliced: border framed, center stretches (matches the other panels)
            _basePPU = _panel.pixelsPerUnitMultiplier; _panel.raycastTarget = true;
            Place(_panel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(panelW, panelH), Vector2.zero);

            _title = UiKit.Text("Header", _panel.transform, 28, UiKit.Gold, TextAlignmentOptions.Center);
            _title.fontStyle = FontStyles.Bold; _title.text = "The gods offer a choice";
            _title.outlineColor = Color.black; _title.outlineWidth = 0.2f;   // black lining like vanilla UI headings
            Place(_title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(panelW - 40, TITLE_H), new Vector2(0, panelH / 2f - PAD - TITLE_H / 2f));

            float rowY = panelH / 2f - PAD - TITLE_H - 10f - BH / 2f;
            for (int i = 0; i < 3; i++) _boons[i] = BuildBoon(i, rowY);

            _rerollBtn = UiKit.CloneButton(_panel.transform, "Reroll");
            if (_rerollBtn != null)
            {
                var rt = (RectTransform)_rerollBtn.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(180, REROLL_H); rt.anchoredPosition = new Vector2(0, -(panelH / 2f - PAD - REROLL_H / 2f));
                _rerollBtn.onClick.AddListener(() => BoonHud.DoReroll());
                _rerollLabel = _rerollBtn.GetComponentInChildren<TextMeshProUGUI>();
            }

            _built = true;
        }

        BoonBtn BuildBoon(int i, float rowY)
        {
            var b = UiKit.CloneButton(_panel.transform, "");
            var card = new BoonBtn { btn = b };
            if (b == null) return card;
            var rt = (RectTransform)b.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(BW, BH); rt.anchoredPosition = new Vector2((i - 1) * (BW + BGAP), rowY);

            // soft rarity-coloured halo behind the card (first sibling → renders under the cards), shown on hover
            card.halo = UiKit.Img($"Halo{i}", _panel.transform, UiKit.SoftBoxGlow(), UiKit.Gold);   // Simple stretch → smooth gradient
            var hrt = card.halo.rectTransform; hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f); hrt.pivot = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(BW + 84f, BH + 84f); hrt.anchoredPosition = new Vector2((i - 1) * (BW + BGAP), rowY);
            card.halo.raycastTarget = false; card.halo.transform.SetAsFirstSibling(); card.halo.gameObject.SetActive(false);

            var nt = b.GetComponentInChildren<TextMeshProUGUI>(true); if (nt != null) nt.gameObject.SetActive(false); // hide native label
            card.bg = (b.targetGraphic as Image) ?? b.GetComponent<Image>();
            // keep the native button look (you preferred buttons); F8 previews alternative sprites live.
            // optional configured override:
            if (card.bg != null && !string.IsNullOrEmpty(BoonCfg.ChooserCardSprite))
            {
                var spr = UiKit.FindSprite(BoonCfg.ChooserCardSprite);
                if (spr != null) { card.bg.sprite = spr; card.bg.material = null; card.bg.type = Image.Type.Sliced; }
            }
            if (card.bg != null) card.bg.color = new Color(0.70f, 0.70f, 0.70f);   // baseline darken (constant, no rarity tint)
            b.transition = Selectable.Transition.None;

            // hover overlay — a FLAT black veil (uniform darken, no left-to-right gradient from the bg sprite)
            var hi = UiKit.Img($"Hi{i}", b.transform, UiKit.Solid(), new Color(0f, 0f, 0f, 0.40f));
            UiKit.Stretch(hi.rectTransform); hi.raycastTarget = false; hi.gameObject.SetActive(false); card.hi = hi;

            int idx = i; b.onClick.AddListener(() => BoonHud.PickIndex(idx));
            var et = b.gameObject.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, () => _hoverIdx = idx);
            AddTrigger(et, EventTriggerType.PointerExit, () => { if (_hoverIdx == idx) _hoverIdx = -1; });

            card.border = MakeBorder(b.transform, 9f);   // soft rarity-coloured frame (feathered, not hard edges)
            card.rarity = Child(b.transform, 20, 62f, false);
            card.name = Child(b.transform, 24, 26f, false); card.name.enableWordWrapping = true; card.name.fontStyle = FontStyles.Bold;
            card.name.outlineColor = Color.black; card.name.outlineWidth = 0.25f;   // white text, black lining
            card.give = Child(b.transform, 19, -18f, true);
            card.take = Child(b.transform, 19, -46f, true);
            return card;
        }

        TextMeshProUGUI Child(Transform p, int size, float y, bool body)
        {
            var t = UiKit.Text("t", p, size, UiKit.Ink, TextAlignmentOptions.Center, body);
            var rt = t.rectTransform; rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(BW - 18, size + 10); rt.anchoredPosition = new Vector2(0, y);
            return t;
        }

        // soft rarity-coloured frame: one 9-sliced feathered-edge sprite stretched over the card
        Image[] MakeBorder(Transform p, float t)
        {
            var im = UiKit.Img("frame", p, UiKit.SoftFrame(), UiKit.Gold, Image.Type.Sliced);
            im.pixelsPerUnitMultiplier = 0.5f;    // ~2x thicker border
            UiKit.Stretch(im.rectTransform); im.raycastTarget = false;
            return new[] { im };
        }

        static void Place(RectTransform rt, Vector2 anchor, Vector2 size, Vector2 pos)
        {
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size; rt.anchoredPosition = pos;
        }

        // Which card is under the cursor right now (used on open, before any mouse-move event fires).
        void RecomputeHover()
        {
            _hoverIdx = -1;
            var cam = (_root != null) ? _root.GetComponentInParent<Canvas>()?.worldCamera : null;
            for (int i = 0; i < 3; i++)
            {
                var c = _boons[i];
                if (c?.btn == null || !c.btn.gameObject.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint((RectTransform)c.btn.transform, Input.mousePosition, cam))
                { _hoverIdx = i; return; }
            }
        }

        static void AddTrigger(EventTrigger et, EventTriggerType type, System.Action act)
        {
            var e = new EventTrigger.Entry { eventID = type };
            e.callback.AddListener(_ => act());
            et.triggers.Add(e);
        }

        bool _wasShown; int _hoverSettle;
        string _appliedSprite = " ";   // last panel sprite applied (for hot-reload)
        void Update()
        {
            bool show = BoonCfg.Enabled && BoonHud.Choosing && BoonHud.Offer != null;
            if (!_built) { if (!show) return; BuildUI(); }
            _root.SetActive(show);
            if (show && !_wasShown) _hoverSettle = 4;   // recheck hover for a few frames after open (layout/cursor settle)
            _wasShown = show;
            if (!show) return;
            if (_hoverSettle > 0) { _hoverSettle--; RecomputeHover(); }   // catch the card already under the cursor on open

            // hot-reload the panel sprite when the config value changes
            string wantSprite = string.IsNullOrEmpty(BoonCfg.ChooserPanelSprite) ? CharSheetCfg.PanelSprite : BoonCfg.ChooserPanelSprite;
            if (_appliedSprite != wantSprite)
            {
                _appliedSprite = wantSprite;
                if (UiKit.ApplyPanel(_panel, wantSprite)) _basePPU = _panel.pixelsPerUnitMultiplier;
            }
            _panel.pixelsPerUnitMultiplier = _basePPU / Mathf.Max(0.01f, BoonCfg.ChooserPanelZoom); // live zoom (hot-reloads)

            var offer = BoonHud.Offer;
            for (int i = 0; i < 3; i++)
            {
                var c = _boons[i]; if (c == null || c.btn == null) continue;
                bool on = i < offer.Count;
                c.btn.gameObject.SetActive(on);
                if (!on) continue;
                var b = offer[i];
                int r = Mathf.Clamp(b.Rarity, 0, RarityCol.Length - 1);
                var col = RarityCol[r];
                c.rarity.text = RarityName[r].ToUpper(); c.rarity.color = col;   // rarity shown by label + border
                c.name.text = b.Name; c.name.color = Color.white;                // white text with black outline
                c.give.text = b.GiveText; c.give.color = UiKit.Give;
                // pure boon: list only the benefit, no cost line
                if (b.HasTake) { c.take.gameObject.SetActive(true); c.take.text = b.TakeText; c.take.color = UiKit.Take; }
                else c.take.gameObject.SetActive(false);
                // hover: flat darken veil + a pulsing rarity-coloured halo behind the card; others untouched
                bool hovered = _hoverIdx == i;
                if (c.bg != null) c.bg.color = new Color(0.70f, 0.70f, 0.70f);     // constant for all cards
                if (c.hi != null) c.hi.gameObject.SetActive(hovered);
                if (c.border != null) foreach (var e in c.border) if (e != null) e.color = col;   // static rarity colour
                if (c.halo != null)
                {
                    c.halo.gameObject.SetActive(hovered);
                    if (hovered) { float a = 0.78f + 0.22f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f)); c.halo.color = new Color(col.r, col.g, col.b, a); }
                }
            }
            if (_rerollLabel != null) _rerollLabel.text = $"Reroll ({BoonHud.RerollsLeft})";
            if (_rerollBtn != null) _rerollBtn.interactable = BoonHud.RerollsLeft > 0;
        }
    }
}
