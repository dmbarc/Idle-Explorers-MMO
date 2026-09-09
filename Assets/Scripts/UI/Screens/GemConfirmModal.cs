using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Asks before a large Mystic Gem is spent, and says what it will actually yield.
///
/// This exists because gems are bought with real money and crafting can run dry.
/// A Gigantic Gem is 72 hours and a thousand relic coins; used at a campfire with
/// twenty raw shrimp banked it produces twenty cooked shrimp and stops. The accrual
/// already reports that honestly AFTER the fact, which is exactly the wrong moment
/// to find out. So the projection is shown here, before anything is consumed.
///
/// The Small gem is exempt — it costs twenty coins and is the one people use
/// casually, and a confirmation on every use would train players to click through it.
/// </summary>
public class GemConfirmModal : UIScreen
{
    /// <summary>Set by the caller immediately before Push.</summary>
    public static string PendingItemId;
    public static int    PendingSlot = -1;

    /// <summary>Gems below this need no confirmation. One hour, i.e. the Small gem.</summary>
    private const float ConfirmAboveSeconds = 3600f;

    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    /// <summary>
    /// Pushes the confirmation if this item warrants one. Returns true when it did,
    /// so the caller knows not to consume the item itself.
    /// </summary>
    public static bool TryIntercept(string itemId, int slotIndex)
    {
        float seconds = GrantedSeconds(itemId);
        if (seconds <= ConfirmAboveSeconds) return false;

        var ui = GameManager.UI;
        if (ui == null) return false;

        PendingItemId = itemId;
        PendingSlot   = slotIndex;
        ui.Push<GemConfirmModal>();
        return true;
    }

    /// <summary>Seconds of activity an item grants, or 0 when it grants none.</summary>
    private static float GrantedSeconds(string itemId)
    {
        var item = GameManager.Content?.GetItem(itemId);
        if (item?.effects == null) return 0f;

        foreach (var effect in item.effects)
            if (effect != null && effect.trigger == "onConsume" && effect.action == "grantAfkTime")
                return effect.magnitude;

        return 0f;
    }

    public override void Build()
    {
        var theme = UIManager.Theme;
        var item  = GameManager.Content?.GetItem(PendingItemId);

        if (item == null) { Close(); return; }

        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        // No click-to-dismiss: this window is about spending something valuable, so
        // both outcomes should be a deliberate press.
        backdrop.AddComponent<Button>().transition = Selectable.Transition.None;

        var card   = UIFactory.Panel(transform, "GemConfirmCard", theme.panelBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.28f, 0.26f);
        cardRt.anchorMax = new Vector2(0.72f, 0.74f);
        cardRt.offsetMin = cardRt.offsetMax = Vector2.zero;

        long grantSeconds = (long)GrantedSeconds(PendingItemId);

        var title = UIFactory.Label(card.transform, item.DisplayName, theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.86f, 0.95f, 0.96f);

        var body = UIFactory.Label(card.transform, BuildProjection(grantSeconds, out bool wasteful),
                                    theme.fontSizeSmall,
                                    wasteful ? theme.accentRed : theme.textPrimary,
                                    TextAlignmentOptions.Top);
        UIFactory.At(body, 0.07f, 0.24f, 0.93f, 0.84f);
        body.textWrappingMode = TextWrappingModes.Normal;

        var confirm = UIFactory.Button(card.transform,
                                        wasteful ? "USE IT ANYWAY" : "USE THE GEM", Confirm, width: 0f);
        UIFactory.At(confirm, 0.07f, 0.12f, 0.48f, 0.21f);

        var cancel = UIFactory.Button(card.transform, "KEEP IT", Close, width: 0f);
        UIFactory.At(cancel, 0.52f, 0.12f, 0.93f, 0.21f);
    }

    /// <summary>
    /// The sentence that decides whether this gem is worth spending right now.
    /// <paramref name="wasteful"/> is set when materials cover less than half of it.
    /// </summary>
    private static string BuildProjection(long grantSeconds, out bool wasteful)
    {
        wasteful = false;

        var activity = GameManager.Activity?.CurrentActivity;
        string window = NumberFormatter.FormatAFKTime(grantSeconds).Replace(" AFK", "");

        if (activity == null || string.IsNullOrEmpty(activity.skillId))
        {
            wasteful = true;
            return $"This grants {window} of your current activity.\n\n" +
                   "You are not doing anything. Nothing would accrue and the gem would be gone.";
        }

        string skill  = GameManager.Content?.GetSkill(activity.skillId)?.DisplayName ?? activity.skillId;
        string doing  = $"Currently: {skill} — {activity.activityTargetName}.";

        // Gathering and combat cannot run out, so there is nothing to warn about.
        if (!CraftingSupply.ProjectSupply(activity, out long crafts, out long supplySeconds,
                                           out string limitingItemId))
            return $"{doing}\n\nThis grants {window}, and nothing about it can run out.";

        string material = GameManager.Content?.GetItem(limitingItemId)?.DisplayName ?? limitingItemId;

        if (crafts <= 0)
        {
            wasteful = true;
            return $"{doing}\n\nYou have no {material} left. Using this now would " +
                   $"consume the gem and produce nothing.";
        }

        string lasts = NumberFormatter.FormatAFKTime(supplySeconds).Replace(" AFK", "");

        if (supplySeconds < grantSeconds / 2)
        {
            wasteful = true;
            return $"{doing}\n\nThis grants {window}, but your {material} runs out after " +
                   $"about {lasts} ({NumberFormatter.Format(crafts)} more). " +
                   $"The rest of the gem would be wasted — bank more {material} first.";
        }

        if (supplySeconds < grantSeconds)
            return $"{doing}\n\nThis grants {window}. Your {material} covers about {lasts} of it " +
                   $"({NumberFormatter.Format(crafts)} more), then it stops.";

        return $"{doing}\n\nThis grants {window}, and you have {material} enough to cover all of it.";
    }

    private void Confirm()
    {
        string itemId = PendingItemId;
        int    slot   = PendingSlot;

        // Close BEFORE consuming: the gem pushes the AFK summary, and popping after
        // that would take the summary down instead of this card — the same trap
        // ItemActionMenu.Act exists to avoid.
        Close();

        if (slot >= 0) ItemEffectResolver.Consume(itemId, slot);
    }

    private void Close()
    {
        PendingItemId = null;
        PendingSlot   = -1;
        GameManager.UI?.Pop();
    }
}
