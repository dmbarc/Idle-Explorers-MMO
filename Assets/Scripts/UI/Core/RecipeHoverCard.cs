using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The tooltip a crafting recipe shows, with the character wearing what it makes.
///
/// ══ WHAT THIS REPLACES ════════════════════════════════════════════════════════
///
/// A hover card on the anvil OBJECT, out in the world, listing what the station could
/// make. That answered a question nobody had: standing next to an anvil, the panel is
/// one click away and lists everything anyway.
///
/// The question people actually have is about ONE recipe, while looking at the list:
/// what is a Tin Helmet, what does it do, and what will I look like in it. So the
/// hover belongs on the row, and it carries the same ItemTooltip every bag slot uses
/// -- one card in the game, so a helmet reads the same wherever it is described.
///
/// ══ WHY THE PREVIEW IS WORTH THE COST ═════════════════════════════════════════
///
/// A crafting list is a wall of names and numbers, and the thing a player is actually
/// buying with four hours of mining is how they will look. An icon does not answer
/// that -- the icon is a painted object on a black square, and the worn art is a
/// different picture entirely.
///
/// It costs a RenderTexture, which is why there is exactly ONE, reused for every row
/// and released with the panel. Building one per recipe would allocate a texture per
/// item in a list that can run to thirty.
/// </summary>
public class RecipeHoverCard : MonoBehaviour
{
    private ItemTooltip.Card _card;
    private CharacterPreview _preview;
    private GameObject       _previewFrame;
    private RectTransform    _canvas;

    /// <summary>How wide the character window is, in pixels.</summary>
    private const float PreviewWidth = 150f;

    /// <summary>And how tall. Taller than wide, because people are.</summary>
    private const float PreviewHeight = 190f;

    /// <summary>
    /// Builds the card once, on a screen.
    ///
    /// Hidden until something hovers. Built up front rather than on first hover
    /// because a RenderTexture and a rig instantiate in a frame the player would feel.
    /// </summary>
    public static RecipeHoverCard Attach(UIScreen screen)
    {
        if (screen == null) return null;

        var host = screen.gameObject.AddComponent<RecipeHoverCard>();

        host._canvas = (RectTransform)screen.transform;
        host._card   = ItemTooltip.CreateCard(screen.transform);

        host.BuildPreview(screen.transform);
        host.Hide();

        return host;
    }

    private void BuildPreview(Transform parent)
    {
        var theme = UIManager.Theme;

        _previewFrame = UIFactory.Panel(parent, "RecipePreview", theme.cardBg, false);

        var rt = _previewFrame.GetComponent<RectTransform>();
        rt.pivot     = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(PreviewWidth, PreviewHeight);

        // Never intercepts the pointer. A preview that can be hovered sits under the
        // cursor, hides itself, reappears, and flickers -- the same reason the
        // tooltip card carries a CanvasGroup.
        var group = _previewFrame.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable   = false;

        var caption = UIFactory.Label(_previewFrame.transform, "EQUIPPED", theme.fontSizeLabel,
                                       theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(caption, 0.05f, 0.02f, 0.95f, 0.12f);

        var stage = UIFactory.Panel(_previewFrame.transform, "Stage", Color.clear, false);
        UIFactory.At(stage.transform, 0.05f, 0.13f, 0.95f, 0.97f);

        // The player's OWN look, so what they see is themselves in the thing rather
        // than a mannequin. Falls back to whatever the rig ships with when there is no
        // character yet -- the editor, or a preview opened before selection.
        _preview = CharacterPreview.Create(stage.transform,
                                           CharacterManager.Current?.spumConfig,
                                           "RecipePreviewRig");
    }

    /// <summary>
    /// Shows the card for one recipe's output.
    ///
    /// Nothing is shown for a recipe whose output is not an item the catalogue knows,
    /// which is a content error rather than something to draw an empty card for.
    /// </summary>
    public void Show(CraftRecipe recipe, Vector2 screenPos)
    {
        if (_card?.Root == null || recipe == null) return;

        ItemData item = GameManager.Content?.GetItem(recipe.outputItemId);

        if (item == null) { Hide(); return; }

        ItemTooltip.Show(_card, _canvas, item, screenPos, quantity: recipe.outputQuantity);

        ShowPreview(item, screenPos);
    }

    /// <summary>
    /// Dresses the preview, and only when there is something to see.
    ///
    /// A Tin Bar has no worn art, and a character window showing an undressed rig
    /// beside a stack of metal is noise. The window appears for equipment and stays
    /// away for everything else.
    /// </summary>
    private void ShowPreview(ItemData item, Vector2 screenPos)
    {
        if (_previewFrame == null || _preview == null) return;

        bool wearable = item.IsEquippable && !string.IsNullOrEmpty(item.equipSpriteAddress);

        // An aura is a particle effect rather than a sprite layer, so there is nothing
        // for a still preview to draw. Saying nothing beats an empty frame.
        bool drawable = wearable && item.equipSlot != "aura";

        if (!drawable) { _previewFrame.SetActive(false); return; }

        _previewFrame.SetActive(true);

        // Reset to the character's own look first, or the last hovered item's helmet
        // is still on their head under this one's chestplate.
        _preview.SetLook(CharacterManager.Current?.spumConfig);
        _preview.SetEquipment(item.equipSlot, item.equipSpriteAddress);

        Position(screenPos);
    }

    /// <summary>
    /// Puts the window beside the tooltip and keeps it on screen.
    ///
    /// To the LEFT of the cursor, because the tooltip goes right -- two cards on the
    /// same side would overlap, and the tooltip is the one that must stay readable.
    /// </summary>
    private void Position(Vector2 screenPos)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvas, screenPos, null, out Vector2 local))
            return;

        var rt   = _previewFrame.GetComponent<RectTransform>();
        Rect area = _canvas.rect;

        float x = local.x - PreviewWidth - 16f;
        float y = local.y + PreviewHeight * 0.5f;

        // Off the left edge: put it on the right instead rather than clipping it.
        if (x < area.xMin) x = local.x + 16f;

        y = Mathf.Clamp(y, area.yMin + PreviewHeight, area.yMax);

        rt.anchoredPosition = new Vector2(x, y);
    }

    public void Hide()
    {
        _card?.Hide();

        if (_previewFrame != null) _previewFrame.SetActive(false);
    }

    /// <summary>
    /// Wires one row to this card.
    ///
    /// An EventTrigger rather than a component per row: the rows are built by
    /// UIFactory panels that carry no script of their own, and adding one type per
    /// hoverable thing is how a UI ends up with nine of them.
    /// </summary>
    public void Bind(GameObject row, CraftRecipe recipe)
    {
        if (row == null || recipe == null) return;

        // The row's own image has to take the pointer or nothing is hovered at all.
        var image = row.GetComponent<Image>();
        if (image != null) image.raycastTarget = true;

        var trigger = row.GetComponent<EventTrigger>() ?? row.AddComponent<EventTrigger>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(data => Show(recipe, ((PointerEventData)data).position));

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => Hide());

        trigger.triggers.Add(enter);
        trigger.triggers.Add(exit);

        // No PointerMove entry: this Unity version's EventTriggerType has none, and
        // the card is positioned where the pointer ENTERED. That is stable rather
        // than following the cursor, which for a card this size reads better anyway.
    }
}
