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

    // ── Sprite skinning ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies a themed 9-sliced sprite to an Image, if the theme supplies one.
    ///
    /// Doing nothing when the sprite is null is the whole safety property of the art
    /// pass: an unassigned theme field leaves the element exactly as it was, so
    /// swapping in art can never break a layout that already worked.
    ///
    /// The flat background colour is replaced by the theme's tint for that role — NOT
    /// by white. Drawing the art at white was what put bright khaki behind most of the
    /// UI; the tint multiplies the panel into the dark blue palette while keeping the
    /// bevels and grain that made the art worth using. See UITheme's tint block.
    /// </summary>
    private static void Skin(Image img, Sprite sprite, Color? tint = null)
    {
        if (img == null || sprite == null) return;

        img.sprite = sprite;
        img.type   = Image.Type.Sliced;

        // Sliced silently degrades to a stretched quad when the sprite has no border,
        // which looks like nothing happened rather than like a bug — worth saying so.
        if (sprite.border == Vector4.zero)
            img.type = Image.Type.Simple;

        if (tint.HasValue) img.color = tint.Value;
    }

    // ── Panel ─────────────────────────────────────────────────────────────────

    public static GameObject Panel(Transform parent, string name = "Panel",
                                    Color? color = null, bool fillParent = true,
                                    bool? raycastTarget = null)
    {
        var go  = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color ?? T.panelBg;

        // Panels are drawn in three weights, and which sprite a caller gets is
        // decided by the colour it asked for — that is already how the code
        // distinguishes a window from a card from a header bar.
        if (color == null || color.Value == T.panelBg)      Skin(img, T.panelSprite,  T.panelSpriteTint);
        else if (color.Value == T.cardBg)                   Skin(img, T.cardSprite,   T.cardSpriteTint);
        else if (color.Value == T.headerBg)                 Skin(img, T.headerSprite, T.headerSpriteTint);

        // A fully transparent panel is a spacer or a HUD root, never a click target.
        // Leaving raycastTarget on made GameHUD's invisible full-screen background
        // swallow every world click, which killed click-to-move entirely.
        img.raycastTarget = raycastTarget ?? (img.color.a > 0.01f);

        if (fillParent) FillParent(go.GetComponent<RectTransform>());
        return go;
    }

    /// <summary>
    /// Anchors a RectTransform to a normalized rect of its parent (0-1 in both axes).
    /// Most layout bugs so far came from elements created without any anchoring at
    /// all, which leaves them stretched over each other in the middle of the screen.
    /// </summary>
    public static T2 At<T2>(T2 component, float xMin, float yMin, float xMax, float yMax)
        where T2 : Component
    {
        var rt = component.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(xMin, yMin);
        rt.anchorMax = new Vector2(xMax, yMax);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
        return component;
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

        bool skinned = T.buttonSprite != null;
        Skin(img, T.buttonSprite);

        var btn = go.GetComponent<Button>();
        var colors = btn.colors;

        if (skinned)
        {
            // Selectable rewrites the Image tint on every state change, so the tint has
            // to live in the colour block or it is overwritten the first time the mouse
            // moves. States are shades OF the theme tint rather than fixed greys, which
            // is what keeps a retinted button consistent across all four states.
            Color baseTint = T.buttonSpriteTint;

            // Scale RGB only — multiplying the whole Color would take alpha with it,
            // so a highlighted button would go over-opaque and a dimmed one see-through.
            static Color Shade(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f, c.a);

            colors.normalColor      = baseTint;
            colors.highlightedColor = Shade(baseTint, 1.18f);
            colors.pressedColor     = Shade(baseTint, 0.78f);
            colors.disabledColor    = new Color(baseTint.r * 0.55f, baseTint.g * 0.55f,
                                                baseTint.b * 0.55f, 0.65f);
            img.color               = baseTint;
        }
        else
        {
            colors.normalColor      = T.buttonNormal;
            colors.highlightedColor = T.buttonHover;
            colors.pressedColor     = T.buttonPressed;
        }

        btn.colors = colors;

        // A real pressed sprite beats tinting the normal one, when the pack ships it.
        // Only the pressed state is swapped — leaving highlighted and disabled null
        // makes Unity fall back to the button's own sprite, which is what we want.
        if (skinned && T.buttonPressedSprite != null)
        {
            btn.transition = Selectable.Transition.SpriteSwap;
            var sprites = btn.spriteState;
            sprites.pressedSprite  = T.buttonPressedSprite;
            sprites.selectedSprite = T.buttonSprite;
            btn.spriteState = sprites;
        }
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

        var rootImg = root.GetComponent<Image>();
        rootImg.color = T.barBg;
        Skin(rootImg, T.barBgSprite, T.barBgSpriteTint);

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(root.transform, false);
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(1f, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        var fillImg = fillGo.GetComponent<Image>();
        fillImg.color         = fillColor ?? T.xpFill;
        // Filled REQUIRES a sprite — with none, Image.OnPopulateMesh returns a plain
        // quad and fillAmount is silently ignored, which is what left the HP bar
        // permanently full. A themed bar sprite is tinted rather than bleached so
        // hp/mp/xp stay visually distinct.
        fillImg.sprite        = T.barFillSprite != null ? T.barFillSprite : WhiteSprite;
        fillImg.type          = Image.Type.Filled;
        fillImg.fillMethod    = Image.FillMethod.Horizontal;
        fillImg.fillOrigin    = (int)Image.OriginHorizontal.Left;
        fillImg.fillAmount    = 1f;
        fillImg.raycastTarget = false;

        return (root, fillImg);
    }

    // ── Shared white sprite ───────────────────────────────────────────────────

    private static Sprite _whiteSprite;

    /// <summary>
    /// A plain white 4x4 sprite.
    ///
    /// Required by every Image using Type.Filled. Image.OnPopulateMesh returns
    /// early with a plain quad when its sprite is null, so fillAmount is silently
    /// ignored — which is why the HP bar rendered permanently full and the ability
    /// cooldown sweeps never appeared.
    /// </summary>
    public static Sprite WhiteSprite
    {
        get
        {
            if (_whiteSprite != null) return _whiteSprite;

            const int size = 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "ui_white" };
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();

            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            _whiteSprite.name = "ui_white";
            return _whiteSprite;
        }
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

        // RectMask2D, NOT Mask. Mask derives its stencil from the graphic's alpha,
        // so a viewport Image with color = clear writes nothing to the stencil and
        // masks out *everything* inside it — which silently blanked the class list,
        // the inventory grid and the skills list. RectMask2D clips by rectangle and
        // needs no graphic at all.
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(root.transform, false);
        FillParent(viewport.GetComponent<RectTransform>());

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

    /// <summary>
    /// A vertical scrolling list, ready to have rows parented straight to the
    /// returned content.
    ///
    /// ScrollView alone returns a content RectTransform anchored top-stretch with zero
    /// offsets — it is ZERO PIXELS TALL until something sizes it. Adding a stretched
    /// child and expecting it to fill that produces a list that overflows off the top
    /// of the viewport with nothing for ScrollRect to scroll, which is precisely how
    /// the class picker lost its first three rows.
    ///
    /// The layout group and size fitter therefore go on the content itself. Prefer
    /// this over ScrollView for any list of rows.
    /// </summary>
    public static (ScrollRect scroll, RectTransform content) ScrollList(Transform parent,
                                                                          string name = "ScrollList",
                                                                          float spacing = 0f,
                                                                          int padding = 6)
    {
        var (scroll, content) = ScrollView(parent, name);

        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing                = spacing > 0f ? spacing : T.spacing;
        layout.padding                = new RectOffset(padding, padding, padding, padding);
        layout.childForceExpandWidth  = true;
        layout.childForceExpandHeight = false;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        return (scroll, content);
    }

    /// <summary>
    /// A horizontally scrolling strip of cards, ready to have them parented straight
    /// to the returned content.
    ///
    /// ScrollList's sibling, and it exists because ScrollList cannot be reused for
    /// this: it hard-wires a VerticalLayoutGroup and fits the vertical axis.
    ///
    /// The important part is the re-anchor. ScrollView returns content anchored
    /// TOP-STRETCH — full width, ZERO HEIGHT — which is right for a vertical list
    /// that grows downward and completely wrong for a horizontal one. A caller that
    /// only adds a horizontal ContentSizeFitter fixes the axis that was already fine
    /// and leaves the broken one at zero; every card then gets height 0 from
    /// childControlHeight, and any child positioned by fractional anchors inside it
    /// collapses onto a single line, because 0.9 × 0 and 0.04 × 0 are the same point.
    /// That is exactly what happened to the class picker.
    ///
    /// Anchoring LEFT-STRETCH instead makes content inherit the viewport's height and
    /// grow rightward, which is the mirror image of what ScrollList does.
    /// </summary>
    public static (ScrollRect scroll, RectTransform content) ScrollStrip(Transform parent,
                                                                           string name = "ScrollStrip",
                                                                           float spacing = 0f,
                                                                           int padding = 12)
    {
        var (scroll, content) = ScrollView(parent, name, vertical: false, horizontal: true);

        content.anchorMin = new Vector2(0f, 0f);
        content.anchorMax = new Vector2(0f, 1f);
        content.pivot     = new Vector2(0f, 0.5f);
        content.offsetMin = Vector2.zero;
        content.offsetMax = Vector2.zero;
        content.anchoredPosition = Vector2.zero;

        var layout = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing                = spacing > 0f ? spacing : T.spacing;
        layout.padding                = new RectOffset(padding, padding, padding, padding);
        layout.childForceExpandWidth  = false;
        layout.childForceExpandHeight = true;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childAlignment         = TextAnchor.MiddleLeft;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit   = ContentSizeFitter.FitMode.Unconstrained;   // height comes from the viewport

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

        var fieldImg = go.GetComponent<Image>();
        fieldImg.color = T.slotBg;
        Skin(fieldImg, T.inputSprite, T.inputSpriteTint);

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
        // Root is the border; an inset child is the interior. Previously the
        // "Border" child filled the parent completely, painting over the
        // background so every slot rendered as one solid light block.
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(T.slotSize, T.slotSize);

        var borderImg = go.GetComponent<Image>();
        borderImg.color = T.slotBorder;

        // A slot sprite already draws its own frame and interior, so the hand-drawn
        // two-layer border is only used when there is no art.
        bool skinnedSlot = T.slotSprite != null;
        Skin(borderImg, T.slotSprite, T.slotSpriteTint);

        // Interior, inset by 2px on every side to leave the border visible
        var inner = new GameObject("Background", typeof(RectTransform), typeof(Image));
        inner.transform.SetParent(go.transform, false);
        var innerRt = inner.GetComponent<RectTransform>();
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(2f, 2f);
        innerRt.offsetMax = new Vector2(-2f, -2f);
        var innerImg = inner.GetComponent<Image>();
        innerImg.color         = T.slotBg;
        innerImg.raycastTarget = false;
        innerImg.enabled       = !skinnedSlot;

        // Icon
        var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        icon.transform.SetParent(go.transform, false);
        var iconRt = icon.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0.1f, 0.1f);
        iconRt.anchorMax = new Vector2(0.9f, 0.9f);
        iconRt.offsetMin = Vector2.zero;
        iconRt.offsetMax = Vector2.zero;
        var iconImg = icon.GetComponent<Image>();
        iconImg.color          = Color.white;
        iconImg.preserveAspect = true;
        iconImg.raycastTarget  = false;
        // An Image with no sprite draws as a white square, so start hidden and let
        // the caller enable it once a real sprite is assigned.
        iconImg.enabled = false;

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
        // A divider sprite is usually decorative and taller than a hairline, so the
        // element grows to fit it rather than squashing the art into 1px.
        var dividerImg = go.GetComponent<Image>();
        dividerImg.color         = T.slotBorder;
        dividerImg.raycastTarget = false;

        if (T.dividerSprite != null)
        {
            Skin(dividerImg, T.dividerSprite, T.dividerSpriteTint);
            dividerImg.preserveAspect = true;
            rt.sizeDelta = new Vector2(0, Mathf.Max(height, 8f));
        }
        else
        {
            rt.sizeDelta = new Vector2(0, height);
        }

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
