using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The icon that follows the cursor during a drag.
///
/// Extracted because it existed twice already — InventoryPanel and BankPanel carried
/// byte-identical copies — and dragging abilities onto the action bar would have made
/// it three. It is also the first drag in this project that CROSSES SCREENS: the
/// source is a talent card on an overlay and the target is the HUD underneath it, so
/// no single panel can own the ghost any more.
///
/// Lives on UIManager.DragCanvas (sortingOrder 999) so it draws above every screen,
/// and never blocks raycasts — a ghost under the cursor would shadow the drop target
/// it is being dragged onto.
/// </summary>
public static class DragGhost
{
    private static GameObject _instance;

    /// <summary>What is being dragged, for a drop target to interrogate. Null when idle.</summary>
    public static object Payload { get; private set; }

    public static bool IsDragging => _instance != null;

    /// <summary>
    /// Starts a drag. <paramref name="payload"/> is handed to whatever accepts the
    /// drop — an ability id, a slot index, anything the two ends agree on.
    /// </summary>
    public static void Begin(Sprite sprite, Vector2 screenPos, object payload, string label = null)
    {
        Cancel();

        Payload = payload;

        var canvas = UIManager.DragCanvas != null
            ? UIManager.DragCanvas.transform
            : UIManager.MainCanvas?.transform;

        if (canvas == null) return;

        _instance = new GameObject("DragGhost", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        _instance.transform.SetParent(canvas, false);

        var rt = _instance.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(UIManager.Theme.slotSize, UIManager.Theme.slotSize);
        rt.position  = screenPos;

        var img = _instance.GetComponent<Image>();
        img.sprite         = sprite;
        img.preserveAspect = true;
        img.raycastTarget  = false;
        img.enabled        = sprite != null;

        // A dragged ability often has no icon. A bare label beats an invisible ghost,
        // which reads as the drag having failed to start.
        if (sprite == null && !string.IsNullOrEmpty(label))
        {
            var text = UIFactory.Label(_instance.transform, label, UIManager.Theme.fontSizeLabel,
                                        UIManager.Theme.textPrimary, TMPro.TextAlignmentOptions.Center);
            UIFactory.FillParent(text.rectTransform);

            var backing = _instance.GetComponent<Image>();
            backing.enabled = true;
            backing.color   = UIManager.Theme.cardBg;
        }

        var group = _instance.GetComponent<CanvasGroup>();
        group.alpha          = 0.85f;
        group.blocksRaycasts = false;
    }

    public static void Move(Vector2 screenPos)
    {
        if (_instance == null) return;
        _instance.GetComponent<RectTransform>().position = screenPos;
    }

    /// <summary>
    /// Ends the drag and clears the payload. Safe at any time, and called from
    /// OnEndDrag — which always runs whether or not a drop landed, and is therefore
    /// the one reliable place to clean up.
    /// </summary>
    public static void Cancel()
    {
        if (_instance != null)
        {
            Object.Destroy(_instance);
            _instance = null;
        }
        Payload = null;
    }

    /// <summary>The payload as a type, or default when the drag is something else.</summary>
    public static T PayloadAs<T>() where T : class => Payload as T;
}
