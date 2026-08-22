using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Static factory for creating standard UI elements in code.
/// All screens build themselves using these helpers.
/// Every element reads colors/fonts from UIManager.Theme.
/// </summary>
public static class UIFactory
{
    private static UITheme T => UIManager.Theme;

    // ── Anchoring helpers ─────────────────────────────────────────────────────

    public static void FillParent(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    public static void Anchor(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax,
                              Vector2 pivot, Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin       = anchorMin;
        rt.anchorMax       = anchorMax;
        rt.pivot           = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta       = size;
    }

    // ── Panel ─────────────────────────────────────────────────────────────────

    public static GameObject Panel(Transform parent, string name = "Panel",
                                    Color? color = null, bool fillParent = true)
    {
        var go  = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color ?? T.panelBg;
        if (fillParent) FillParent(go.GetComponent<RectTransform>());
        return go;
    }

    // ── Label ─────────────────────────────────────────────────────────────────

    public static TMP_Text Label(Transform parent, string text, float fontSize = 0,
                                  Color? color = null, TextAlignmentOptions align = TextAlignmentOptions.Left)
    {
        var go  = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text           = text;
        tmp.fontSize       = fontSize > 0 ? fontSize : T.fontSizeBody;
        tmp.color          = color ?? T.textPrimary;
        tmp.alignment      = align;
        tmp.raycastTarget  = false;
        if (T.font != null) tmp.font = T.font;
        return tmp;
    }

    // ── Button ────────────────────────────────────────────────────────────────

    public static Button Button(Transform parent, string label, Action onClick,
                                 float width = 200f, float height = 0f)
    {
        float h = height > 0 ? height : T.buttonHeight;
        var go  = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);

        var rt  = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, h);

        var img = go.GetComponent<Image>();
        img.color = T.buttonNormal;

        var btn = go.GetComponent<Button>();
        var colors = btn.colors;
        colors.normalColor      = T.buttonNormal;
        colors.highlightedColor = T.buttonHover;
        colors.pressedColor     = T.buttonPressed;
        btn.colors = colors;
        btn.onClick.AddListener(() =>
        {
            GameManager.Audio?.PlayClick();
            onClick?.Invoke();
        });

        // Label inside button
        var lbl = Label(go.transform, label, T.fontSizeBody, T.textPrimary, TextAlignmentOptions.Center);
        FillParent(lbl.GetComponent<RectTransform>());

        return btn;
    }

    // ── Progress bar ──────────────────────────────────────────────────────────

    public static (GameObject root, Image fill) ProgressBar(Transform parent, string name,
                                                              Color? fillColor = null,
                                                              float width = 200f, float height = 16f)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Image));
        root.transform.SetParent(parent, false);
        var rootRt  = root.GetComponent<RectTransform>();
        rootRt.sizeDelta = new Vector2(width, height);
        root.GetComponent<Image>().color = T.barBg;

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(root.transform, false);
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(1f, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        var fillImg = fillGo.GetComponent<Image>();
        fillImg.color = fillColor ?? T.xpFill;
        fillImg.type  = Image.Type.Filled;
        fillImg.fillMethod = Image.FillMethod.Horizontal;
        fillImg.fillAmount = 1f;

        return (root, fillImg);
    }

    // ── Placeholder icons ─────────────────────────────────────────────────────

    private static readonly System.Collections.Generic.Dictionary<string, Sprite> _placeholderCache = new();

    /// <summary>
    /// A deterministic coloured icon derived from an item id, used until real art
    /// is wired up (every iconAddress in item_data.json is currently empty).
    /// The same id always produces the same colour, so items stay visually
    /// distinguishable in the inventory and players can learn them by sight.
    /// Filling in iconAddress later replaces these with no code change.
    /// </summary>
    public static Sprite PlaceholderIcon(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) itemId = "unknown";
        if (_placeholderCache.TryGetValue(itemId, out var cached) && cached != null)
            return cached;

        // Stable hash → hue. Avoids string.GetHashCode, which is not guaranteed
        // stable across runtimes and would reshuffle colours between platforms.
        unchecked
        {
            int hash = 17;
            foreach (char c in itemId) hash = hash * 31 + c;

            float hue        = (Mathf.Abs(hash) % 360) / 360f;
            float saturation = 0.45f + ((Mathf.Abs(hash / 360) % 30) / 100f); // 0.45–0.75
            var   fillColor  = Color.HSVToRGB(hue, saturation, 0.85f);

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                name       = $"placeholder_{itemId}"
            };

            var border = Color.Lerp(fillColor, Color.black, 0.45f);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool isEdge = x < 3 || y < 3 || x >= size - 3 || y >= size - 3;
                    pixels[y * size + x] = isEdge ? border : fillColor;
                }

            tex.SetPixels(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            sprite.name = $"placeholder_{itemId}";
            _placeholderCache[itemId] = sprite;
            return sprite;
        }
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    public static Image Icon(Transform parent, Sprite sprite, float size = 0f, string name = "Icon")
    {
        float s = size > 0 ? size : T.iconSize;
        var go  = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt  = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(s, s);
        var img = go.GetComponent<Image>();
        img.sprite           = sprite;
        img.preserveAspect   = true;
        img.raycastTarget    = false;
        return img;
    }

    // ── Scroll view ───────────────────────────────────────────────────────────

    public static (ScrollRect scroll, RectTransform content) ScrollView(Transform parent,
                                                                         string name = "ScrollView",
                                                                         bool vertical = true,
                                                                         bool horizontal = false)
    {
        var root = Panel(parent, name, T.panelBg, false);
        var rootRt = root.GetComponent<RectTransform>();
        FillParent(rootRt);

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewport.transform.SetParent(root.transform, false);
        FillParent(viewport.GetComponent<RectTransform>());
        viewport.GetComponent<Image>().color = Color.clear;
        viewport.GetComponent<Mask>().showMaskGraphic = false;

        var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
        content.SetParent(viewport.transform, false);
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.pivot     = new Vector2(0.5f, 1f);
        content.offsetMin = Vector2.zero;
        content.offsetMax = Vector2.zero;

        var scroll = root.AddComponent<ScrollRect>();
        scroll.viewport    = viewport.GetComponent<RectTransform>();
        scroll.content     = content;
        scroll.vertical    = vertical;
        scroll.horizontal  = horizontal;
        scroll.scrollSensitivity = 30f;

        return (scroll, content);
    }

    // ── Grid layout ───────────────────────────────────────────────────────────

    public static GridLayoutGroup Grid(Transform parent, int cols, float cellSize = 0f,
                                        float spacing = 0f, string name = "Grid")
    {
        float s = cellSize > 0 ? cellSize : T.slotSize;
        float sp = spacing > 0 ? spacing : T.spacing;
        var go = new GameObject(name, typeof(RectTransform), typeof(GridLayoutGroup));
        go.transform.SetParent(parent, false);
        var grid = go.GetComponent<GridLayoutGroup>();
        grid.cellSize        = new Vector2(s, s);
        grid.spacing         = new Vector2(sp, sp);
        grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = cols;
        grid.childAlignment  = TextAnchor.UpperLeft;
        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return grid;
    }

    // ── Input field ───────────────────────────────────────────────────────────

    public static TMP_InputField InputField(Transform parent, string placeholder,
                                             Action<string> onChanged = null,
                                             float width = 300f, float height = 0f)
    {
        float h = height > 0 ? height : T.buttonHeight;
        var go = new GameObject("InputField", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, h);
        go.GetComponent<Image>().color = T.slotBg;

        // Text area
        var textAreaGo = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textAreaGo.transform.SetParent(go.transform, false);
        FillParent(textAreaGo.GetComponent<RectTransform>());

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(textAreaGo.transform, false);
        FillParent(textGo.GetComponent<RectTransform>());
        var textComp = textGo.GetComponent<TextMeshProUGUI>();
        textComp.color = T.textPrimary;
        textComp.fontSize = T.fontSizeBody;
        if (T.font != null) textComp.font = T.font;

        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
        phGo.transform.SetParent(textAreaGo.transform, false);
        FillParent(phGo.GetComponent<RectTransform>());
        var phComp = phGo.GetComponent<TextMeshProUGUI>();
        phComp.text = placeholder;
        phComp.color = T.textSecondary;
        phComp.fontSize = T.fontSizeBody;
        phComp.fontStyle = FontStyles.Italic;
        if (T.font != null) phComp.font = T.font;

        var field = go.GetComponent<TMP_InputField>();
        field.textComponent = textComp;
        field.placeholder   = phComp;
        field.textViewport  = textAreaGo.GetComponent<RectTransform>();
        if (onChanged != null) field.onValueChanged.AddListener(v => onChanged(v));

        return field;
    }

    // ── Inventory slot ────────────────────────────────────────────────────────

    public static GameObject Slot(Transform parent, string name = "Slot")
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(T.slotSize, T.slotSize);
        go.GetComponent<Image>().color = T.slotBg;

        // Border
        var border = new GameObject("Border", typeof(RectTransform), typeof(Image));
        border.transform.SetParent(go.transform, false);
        FillParent(border.GetComponent<RectTransform>());
        border.GetComponent<Image>().color = T.slotBorder;

        // Icon
        var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        icon.transform.SetParent(go.transform, false);
        var iconRt = icon.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0.1f, 0.1f);
        iconRt.anchorMax = new Vector2(0.9f, 0.9f);
        iconRt.offsetMin = Vector2.zero;
        iconRt.offsetMax = Vector2.zero;
        icon.GetComponent<Image>().color = Color.white;

        // Quantity label (bottom-right)
        var qty = new GameObject("Quantity", typeof(RectTransform), typeof(TextMeshProUGUI));
        qty.transform.SetParent(go.transform, false);
        var qtyRt = qty.GetComponent<RectTransform>();
        qtyRt.anchorMin = new Vector2(0f, 0f);
        qtyRt.anchorMax = new Vector2(1f, 0.35f);
        qtyRt.offsetMin = Vector2.zero;
        qtyRt.offsetMax = Vector2.zero;
        var qtyTmp = qty.GetComponent<TextMeshProUGUI>();
        qtyTmp.alignment   = TextAlignmentOptions.BottomRight;
        qtyTmp.fontSize    = T.fontSizeLabel;
        qtyTmp.color       = T.textPrimary;
        qtyTmp.raycastTarget = false;
        if (T.font != null) qtyTmp.font = T.font;

        return go;
    }

    // ── Divider ───────────────────────────────────────────────────────────────

    public static GameObject HorizontalDivider(Transform parent, float height = 1f)
    {
        var go = new GameObject("Divider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0.5f);
        rt.anchorMax = new Vector2(1, 0.5f);
        rt.sizeDelta = new Vector2(0, height);
        go.GetComponent<Image>().color = T.slotBorder;
        return go;
    }

    // ── Vertical layout group ─────────────────────────────────────────────────

    public static VerticalLayoutGroup VStack(Transform parent, float spacing = 0f,
                                              bool childForceWidth = true, string name = "VStack")
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
        go.transform.SetParent(parent, false);
        var vlg = go.GetComponent<VerticalLayoutGroup>();
        vlg.spacing             = spacing > 0 ? spacing : T.spacing;
        vlg.childForceExpandWidth  = childForceWidth;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth   = childForceWidth;
        vlg.childControlHeight  = false;
        return vlg;
    }

    // ── Horizontal layout group ───────────────────────────────────────────────

    public static HorizontalLayoutGroup HStack(Transform parent, float spacing = 0f,
                                                string name = "HStack")
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var hlg = go.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing              = spacing > 0 ? spacing : T.spacing;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth    = false;
        hlg.childControlHeight   = true;
        return hlg;
    }
}
