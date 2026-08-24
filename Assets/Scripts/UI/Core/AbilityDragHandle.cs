using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// What is being dragged when an ability is picked up.
///
/// Carries where it came from as well as what it is: dragging between two bar slots
/// has to swap them, while dragging in from the talent tree only has to assign. A drop
/// target that could not tell the two apart would leave a duplicate behind.
/// </summary>
public class AbilityDrag
{
    public string AbilityId;

    /// <summary>Bar slot it came from, or -1 when it came from the talent tree.</summary>
    public int FromSlot = -1;
}

/// <summary>
/// Makes something draggable as an ability — a talent card that grants one, or an
/// action bar slot that holds one.
///
/// Deliberately not built on InventorySlotView. That class looks reusable but is not:
/// SlotContainerKind is a closed two-value enum, SetContents takes an InventoryEntry,
/// and its OnDrop hard-codes the three inventory/bank routes. What IS reusable is the
/// ghost lifecycle, which now lives in DragGhost and is shared by both.
/// </summary>
public class AbilityDragHandle : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private string _abilityId;
    private int    _fromSlot = -1;
    private Sprite _icon;

    /// <summary>Called after the object is built. Slot -1 means "from the talent tree".</summary>
    public void Bind(string abilityId, Sprite icon, int fromSlot = -1)
    {
        _abilityId = abilityId;
        _icon      = icon;
        _fromSlot  = fromSlot;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (string.IsNullOrEmpty(_abilityId)) return;

        var ability = TalentManager.FindAbilityFor(CharacterManager.Current, _abilityId);

        // A passive cannot sit on the bar — it is always on. Saying so here beats
        // letting the player drag it across the screen and silently refusing at the end.
        if (ability != null && !ability.IsActivatable)
        {
            GameEvents.FireToast($"{ability.name} is passive — it is always active.");
            return;
        }

        DragGhost.Begin(_icon, eventData.position,
                        new AbilityDrag { AbilityId = _abilityId, FromSlot = _fromSlot },
                        ability?.name ?? _abilityId);

        // The talent tree is drawn OVER the action bar, so the panel has to stop
        // taking raycasts before the drop can reach a slot on it.
        HidePanelForDrag();
    }

    public void OnDrag(PointerEventData eventData) => DragGhost.Move(eventData.position);

    /// <summary>
    /// Always runs, whether the drop landed on a slot or on empty space, so this is
    /// the one reliable place to tear the ghost down and put the panel back.
    /// </summary>
    public void OnEndDrag(PointerEventData eventData)
    {
        DragGhost.Cancel();
        RestorePanelAfterDrag();
    }

    // ── Getting the panel out of the way ──────────────────────────────────────

    private CanvasGroup _dimmedPanel;
    private float       _restoreAlpha;
    private bool        _restoreBlocksRaycasts;

    /// <summary>
    /// Fades the panel this handle lives on and stops it taking raycasts, for as long
    /// as the drag lasts.
    ///
    /// ══ WHY THE ABILITY COULD BE PICKED UP AND NEVER PUT DOWN ═════════════════
    ///
    /// The action bar is on the HUD; the talent tree is an overlay drawn on top of it,
    /// with a full-screen backdrop that takes raycasts so clicking outside the tree
    /// closes it. That backdrop is between the cursor and every slot on the bar, so
    /// the drop landed on the talent panel every time. The ghost followed the cursor
    /// perfectly and there was nowhere on screen it could be released — the one part
    /// of the interaction that looked like it was working was the only part that was.
    ///
    /// Fading rather than closing. Closing the panel would fire RebuildOnShow when it
    /// came back, and — more to the point — the player is mid-gesture: a panel that
    /// vanishes and reappears reads as the drag having failed. At 12% it is still
    /// legible as "the thing I came from" while the bar underneath is unmistakably
    /// the target.
    ///
    /// Setting blocksRaycasts false also stops the SOURCE card taking raycasts, which
    /// is harmless: once a drag has begun, Unity routes OnDrag and OnEndDrag to the
    /// object that started it regardless of what the pointer is over.
    /// </summary>
    private void HidePanelForDrag()
    {
        var screen = GetComponentInParent<UIScreen>();

        // The HUD is never in the way — it IS the target. A bar slot dragged to
        // another bar slot must not dim the thing it is being dropped onto.
        if (screen == null || screen is GameHUD) return;

        var group = screen.GetComponent<CanvasGroup>();
        if (group == null) group = screen.gameObject.AddComponent<CanvasGroup>();
        if (group == null) return;

        _dimmedPanel           = group;
        _restoreAlpha          = group.alpha;
        _restoreBlocksRaycasts = group.blocksRaycasts;

        group.alpha          = 0.12f;
        group.blocksRaycasts = false;
    }

    /// <summary>
    /// Puts it back exactly as it was found, rather than assuming it was opaque and
    /// interactive — a panel mid-fade would otherwise be snapped to full opacity by
    /// a drag that happened to start during it.
    /// </summary>
    private void RestorePanelAfterDrag()
    {
        if (_dimmedPanel == null) return;

        _dimmedPanel.alpha          = _restoreAlpha;
        _dimmedPanel.blocksRaycasts = _restoreBlocksRaycasts;
        _dimmedPanel                = null;
    }
}

/// <summary>
/// An action bar slot that accepts a dragged ability.
///
/// Dropping onto an occupied slot swaps rather than overwrites, so a bar can be
/// rearranged without an ability falling off the end of it.
/// </summary>
public class AbilitySlotDrop : MonoBehaviour, IDropHandler
{
    private int _slot;

    public void Bind(int slot) => _slot = slot;

    public void OnDrop(PointerEventData eventData)
    {
        var drag = DragGhost.PayloadAs<AbilityDrag>();
        if (drag == null) return;

        var player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            GameEvents.FireToast("No character in the world yet.");
            return;
        }

        if (drag.FromSlot >= 0) player.SwapAbilitySlots(drag.FromSlot, _slot);
        else                    player.AssignAbility(_slot, drag.AbilityId);

        DragGhost.Cancel();
    }
}
