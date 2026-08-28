using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The hover card for an item: stats, equip effects, durability, and the armour set
/// it belongs to.
///
/// One implementation, shared by the inventory, the bank and the paperdoll, because
/// three near-identical tooltips is how they end up disagreeing about what an item
/// does. Panels own a Card and hand it an item; everything else happens here.
///
/// The set block follows the shape the design asked for:
///
///     Set: Weak Tin Man (2/6)
///       Tin Helmet          ← grey unowned, yellow carried, green worn
///       ...
///     Equip:
///       2 Set Bonus - ...   ← grey below the threshold, yellow earned,
///       4 Set Bonus - ...     green once the whole set is on
///
/// Colour is the entire point of this readout, so it is written with TextMeshPro rich
/// text rather than one label per line — a set is up to six pieces plus three bonuses,
/// and building fifteen labels per hover to recolour them individually would cost more
/// than the panel that owns it.
/// </summary>
public static class ItemTooltip
{
    // ── Palette ───────────────────────────────────────────────────────────────
    //
    // Fixed hexes rather than theme colours: these three carry meaning (missing /
    // carried / worn) and must stay legible and distinct from each other whatever the
    // theme does to its greys.

    private const string GreyHex   = "#7C8497";
    private const string YellowHex = "#E8C46A";
    private const string GreenHex  = "#67D28B";
    private const string StatHex   = "#8FD8FF";
    private const string FlavorHex = "#9AA3B5";
    private const string WarnHex   = "#E06C6C";

    private static string HexFor(ItemSetManager.PieceState state) => state switch
    {
        ItemSetManager.PieceState.Equipped => GreenHex,
        ItemSetManager.PieceState.Carried  => YellowHex,
        _                                  => GreyHex,
    };

    private static string Colour(string text, string hex) => $"<color={hex}>{text}</color>";

    // ── The card ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A tooltip panel plus its two labels. Created once per screen and shown or
    /// hidden — building it per hover would allocate a layout rebuild every time the
    /// pointer crossed a slot.
    /// </summary>
    public class Card
    {
        public GameObject Root;
        public TMP_Text   Title;
        public TMP_Text   Body;

        public void Hide() { if (Root != null) Root.SetActive(false); }
    }

    /// <summary>Builds the tooltip card as a child of a screen's root transform.</summary>
    public static Card CreateCard(Transform parent)
    {
        var theme = UIManager.Theme;

        var root = UIFactory.Panel(parent, "Tooltip", theme.cardBg, false);
        var rt   = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(360f, 140f);
        rt.pivot     = new Vector2(0f, 1f);

        // Never let the tooltip intercept the pointer — a tooltip that can be hovered
        // sits under the cursor, hides itself, reappears, and flickers forever.
        var group = root.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable   = false;

        var vlg = root.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(12, 12, 10, 10);
        vlg.spacing                = 4f;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = root.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var title = UIFactory.Label(root.transform, "", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.TopLeft);
        var body  = UIFactory.Label(root.transform, "", theme.fontSizeSmall,
                                     theme.textPrimary, TextAlignmentOptions.TopLeft);

        root.SetActive(false);
        return new Card { Root = root, Title = title, Body = body };
    }

    /// <summary>
    /// Fills a card for an item and positions it beside the cursor.
    ///
    /// <paramref name="quantity"/> is shown when above one; <paramref name="location"/>
    /// is a short line such as "In the vault". Pass an equipped slot id to show the
    /// piece's actual durability rather than its maximum.
    /// </summary>
    public static void Show(Card card, RectTransform canvas, ItemData item, Vector2 screenPos,
                            long quantity = 1, string location = null, string equippedSlotId = null)
    {
        if (card?.Root == null || item == null) return;

        card.Title.text = ColourTitle(item);
        card.Body.text  = BuildBody(item, quantity, location, equippedSlotId);

        card.Root.SetActive(true);
        card.Root.transform.SetAsLastSibling();

        Position(card, canvas, screenPos);
    }

    /// <summary>
    /// Fills a card with plain title-and-body text.
    ///
    /// For anything that wants the same hover card without being an item — the stat
    /// sheet's explanations, a talent's description. Sharing the card rather than
    /// building a second tooltip is what keeps every hover in the game looking and
    /// behaving the same.
    /// </summary>
    public static void ShowText(Card card, RectTransform canvas, string title, string body,
                                Vector2 screenPos)
    {
        if (card?.Root == null) return;

        card.Title.text = title ?? "";
        card.Body.text  = body ?? "";

        card.Root.SetActive(true);
        card.Root.transform.SetAsLastSibling();

        Position(card, canvas, screenPos);
    }

    /// <summary>
    /// Keeps the card on screen. A slot near the right edge would otherwise open a
    /// 360px card off the side of the display, and one near the bottom would run off
    /// the end of the set list.
    /// </summary>
    private static void Position(Card card, RectTransform canvas, Vector2 screenPos)
    {
        if (canvas == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screenPos, null, out Vector2 local))
            return;

        var rt = card.Root.GetComponent<RectTransform>();

        // The fitter has not run yet this frame, so ask the layout system directly —
        // reading rect.height here returns the PREVIOUS item's size and the card
        // would be nudged for the wrong content.
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

        var  area   = canvas.rect;
        float width  = rt.rect.width;
        float height = rt.rect.height;

        float x = local.x + 16f;
        float y = local.y - 16f;

        // Flip to the other side of the cursor rather than clamping, so the card never
        // sits on top of the slot the pointer is over.
        if (x + width > area.xMax) x = local.x - 16f - width;
        if (y - height < area.yMin) y = Mathf.Min(area.yMax, local.y + 16f + height);

        rt.anchoredPosition = new Vector2(Mathf.Max(area.xMin, x), y);
    }

    // ── Body ──────────────────────────────────────────────────────────────────

    private static string ColourTitle(ItemData item)
    {
        // Set pieces get the set's own colour so a complete set reads as complete from
        // the title alone.
        var set = ItemSetManager.SetFor(item);
        if (set == null) return item.DisplayName;

        return Colour(item.DisplayName, HexFor(ItemSetManager.HeaderState(set)));
    }

    private static string BuildBody(ItemData item, long quantity, string location, string equippedSlotId)
    {
        var sb = new StringBuilder();

        if (item.IsEquippable)
            sb.AppendLine(Colour(EquipmentSlots.NameOf(item.equipSlot), FlavorHex));

        AppendRequirement(sb, item);
        AppendDurability(sb, item, equippedSlotId);
        AppendStats(sb, item);
        AppendEquipEffects(sb, item);
        AppendSet(sb, item);

        if (!string.IsNullOrEmpty(item.description))
        {
            sb.AppendLine();
            sb.AppendLine(Colour($"<i>{item.description}</i>", FlavorHex));
        }

        if (quantity > 1)
            sb.AppendLine(Colour($"Quantity: {NumberFormatter.Format(quantity)}", FlavorHex));

        if (!string.IsNullOrEmpty(location))
            sb.AppendLine(Colour(location, FlavorHex));

        return sb.ToString().TrimEnd();
    }

    private static void AppendRequirement(StringBuilder sb, ItemData item)
    {
        if (item.levelReq <= 0 || string.IsNullOrEmpty(item.sourceSkill)) return;

        string skill = GameManager.Content?.GetSkill(item.sourceSkill)?.DisplayName ?? item.sourceSkill;
        int    have  = GameManager.Skills?.GetSkillLevel(item.sourceSkill) ?? 1;
        bool   met   = have >= item.levelReq;

        sb.AppendLine(Colour($"Requires {skill} {item.levelReq}" + (met ? "" : $" (you are {have})"),
                             met ? FlavorHex : WarnHex));
    }

    private static void AppendDurability(StringBuilder sb, ItemData item, string equippedSlotId)
    {
        if (!item.HasDurability) return;

        int max = item.maxDurability;
        int now = max;

        // Only a worn piece has a real condition. In the bag the ledger knows it, and
        // asking the ledger from here would leak equipment internals into the UI — so
        // an unworn piece shows its ceiling and the paperdoll shows the truth.
        if (!string.IsNullOrEmpty(equippedSlotId) && GameManager.Equipment != null)
            now = GameManager.Equipment.GetDurability(equippedSlotId);

        float fraction = max > 0 ? now / (float)max : 1f;
        string hex = now <= 0        ? WarnHex
                   : fraction < 0.3f ? YellowHex
                   : FlavorHex;

        string label = now <= 0 ? $"Durability 0 / {max} — BROKEN" : $"Durability {now} / {max}";
        sb.AppendLine(Colour(label, hex));
    }

    /// <summary>
    /// The blue stat block: what wearing this actually adds to the character.
    /// Separated from the "Equip:" procs below, the way an ARPG tooltip separates
    /// numbers you can compare from effects you have to read.
    /// </summary>
    private static void AppendStats(StringBuilder sb, ItemData item)
    {
        if (item.effects == null) return;

        bool any = false;
        foreach (var effect in item.effects)
        {
            if (effect == null) continue;
            if (effect.trigger != "onEquipPassive" || effect.action != "statBonus") continue;

            if (!any) { sb.AppendLine(); any = true; }

            string statId = Stats.Canonical(effect.param);
            sb.AppendLine(Colour($"{Stats.Render(statId, effect.magnitude)} {StatName(statId)}", StatHex));
        }
    }

    private static void AppendEquipEffects(StringBuilder sb, ItemData item)
    {
        if (item.effects == null) return;

        bool any = false;
        foreach (var effect in item.effects)
        {
            if (effect == null || string.IsNullOrEmpty(effect.equipText)) continue;

            // statBonus rows are already in the block above as numbers; repeating them
            // as prose doubles the tooltip's height and says nothing new.
            if (effect.action == "statBonus") continue;

            if (!any) { sb.AppendLine(); any = true; }
            sb.AppendLine(Colour(effect.equipText, GreenHex));
        }
    }

    /// <summary>
    /// Human name for a stat id, so tooltips never print "maxHp".
    ///
    /// Through the Stats registry rather than a local table, so an item and the
    /// character sheet cannot end up calling the same stat two different things.
    /// </summary>
    private static string StatName(string statId) =>
        string.IsNullOrEmpty(statId) ? "Unknown" : Stats.NameOf(Stats.Canonical(statId));

    // ── The set block ─────────────────────────────────────────────────────────

    private static void AppendSet(StringBuilder sb, ItemData item)
    {
        var set = ItemSetManager.SetFor(item);
        if (set == null) return;

        int worn = ItemSetManager.EquippedCount(set);

        sb.AppendLine();
        sb.AppendLine(Colour($"Set: {set.DisplayName} ({worn}/{set.PieceCount})",
                             HexFor(ItemSetManager.HeaderState(set))));

        foreach (var pieceId in set.itemIds)
        {
            if (string.IsNullOrEmpty(pieceId)) continue;

            var piece = GameManager.Content?.GetItem(pieceId);
            var state = ItemSetManager.StateOf(pieceId);

            sb.AppendLine(Colour($"  {piece?.DisplayName ?? pieceId}", HexFor(state)));
        }

        if (set.bonuses == null || set.bonuses.Length == 0) return;

        sb.AppendLine();
        sb.AppendLine(Colour("Equip:", FlavorHex));

        // ══ BY TIER, NOT BY FILE ORDER ════════════════════════════════════
        //
        // The tin set listed 2pc, 4pc, 6pc, 4pc, because that was the order somebody
        // had appended them in. A ladder printed out of order reads as a data error
        // even when the data is fine, and it hid a real one -- two bonuses sharing a
        // tier -- inside what looked like a display quirk.
        foreach (var bonus in ByTier(set.bonuses))
        {
            // A blank description is deliberate, not missing: some bonuses take two
            // entries to implement — one for what happens when you are hit and one for
            // what happens when you hit — and the player should read one line, not two.
            if (bonus == null || string.IsNullOrEmpty(bonus.description)) continue;

            string hex = HexFor(ItemSetManager.BonusState(set, bonus, worn));
            sb.AppendLine(Colour($"  {bonus.piecesRequired} Set Bonus - {bonus.description}", hex));
        }
    }

    // ── Equipment panel summary ───────────────────────────────────────────────

    /// <summary>
    /// The set lines for the paperdoll's summary column: every set with a piece worn,
    /// how far along it is, and which bonuses that has earned.
    /// </summary>
    public static string ActiveSetsSummary()
    {
        var active = ItemSetManager.ActiveSets();
        if (active.Count == 0) return Colour("No set pieces worn.", GreyHex);

        var sb = new StringBuilder();

        foreach (var (set, worn) in active)
        {
            sb.AppendLine(Colour($"{set.DisplayName} ({worn}/{set.PieceCount})",
                                 HexFor(ItemSetManager.HeaderState(set))));

            if (set.bonuses == null) continue;

            foreach (var bonus in ByTier(set.bonuses))
            {
                if (bonus == null || string.IsNullOrEmpty(bonus.description)) continue;

                string hex = HexFor(ItemSetManager.BonusState(set, bonus, worn));
                sb.AppendLine(Colour($"  {bonus.piecesRequired}pc — {bonus.description}", hex));
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Every stat the character gains from gear, for the paperdoll header.</summary>
    public static string EquippedStatsSummary()
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return "";

        var rows = new List<string>();

        // Every stat, not a hardcoded three: gear can now grant any of them, and a
        // list that has to be edited whenever a new stat appears is a list that will
        // silently stop showing one.
        foreach (var info in Stats.All)
        {
            float total = equipment.AggregateStat(info.Id);
            if (Mathf.Abs(total) < 0.001f) continue;

            rows.Add(Colour($"{Stats.Render(info.Id, total)} {info.Name}", StatHex));
        }

        return rows.Count == 0 ? Colour("No stats from gear yet.", GreyHex) : string.Join("   ", rows);
    }

    /// <summary>Set bonuses in ladder order, whatever order the file lists them in.</summary>
    private static System.Collections.Generic.IEnumerable<ItemSetBonus> ByTier(ItemSetBonus[] bonuses)
    {
        if (bonuses == null) yield break;

        foreach (var bonus in System.Linq.Enumerable.OrderBy(bonuses, b => b?.piecesRequired ?? 0))
            yield return bonus;
    }
}
