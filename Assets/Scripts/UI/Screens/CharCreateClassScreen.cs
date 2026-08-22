using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 5 of character creation: choose a class.
/// Shows class cards with icon, name, flavor text, and key ability list.
/// </summary>
public class CharCreateClassScreen : UIScreen
{
    private string _selectedClassId;

    // Fallback inline class display data (used when ContentManager isn't loaded yet)
    private static readonly List<(string id, string name, string flavor, string abilities)> _fallback =
        new List<(string, string, string, string)>
        {
            ("warrior",  "Warrior",  "Steel and fury. Outlasts everything by refusing to fall.",
             "Cleave · Shield Bash · Battlecry · Reckless Strike\n⬦ Endure (passive)"),

            ("ranger",   "Ranger",   "Rapid and ruthless. A dozen arrows while you raise your sword.",
             "Rapid Shot · Marked Target · Barrage · Evasion Roll\n⬦ Eagle Eye (passive)"),

            ("sorcerer", "Sorcerer", "Glass cannon. You will die constantly and kill everything first.",
             "Fireball · Frost Nova · Arcane Surge · Blink\n⬦ Mana Efficiency (passive)"),

            ("tinkerer", "Tinkerer", "Gadget-fuelled chaos. Your Fabrication skill makes you absurdly powerful.",
             "Deploy Turret · Smoke Bomb · Overclock · Salvage Strike\n⬦ Jury-Rig (passive)"),

            ("specter",  "Specter",  "Ghost-energy combat. Synergises with Spectral Work. Half here, half not.",
             "Spectral Strike · Phase Shift · Haunt · Soul Drain\n⬦ Liminal (passive)"),
        };

    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);

        // Title
        var title = UIFactory.Label(transform, "CHOOSE YOUR CLASS", UIManager.Theme.fontSizeTitle,
                                     UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.05f, 0.88f);
        titleRt.anchorMax = new Vector2(0.95f, 0.97f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        // Card scroll area
        var (scroll, content) = UIFactory.ScrollView(transform, "ClassScroll", vertical: false, horizontal: true);
        var scrollRt = ((Component)scroll).GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.02f, 0.15f);
        scrollRt.anchorMax = new Vector2(0.98f, 0.86f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        // Horizontal layout in scroll content
        var hlg = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing              = UIManager.Theme.spacing * 2;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth    = false;
        hlg.childControlHeight   = true;
        content.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Populate from ContentManager or fallback
        var classes = GameManager.Content?.Classes;
        if (classes != null && classes.Count > 0)
        {
            foreach (var kv in classes)
                BuildClassCard(content, kv.Value.id, kv.Value.name, kv.Value.flavorText, "");
        }
        else
        {
            foreach (var (id, name, flavor, abilities) in _fallback)
                BuildClassCard(content, id, name, flavor, abilities);
        }

        // Navigation buttons
        UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), 150f)
                  .GetComponent<RectTransform>().anchoredPosition = new Vector2(-120f, 0f);

        UIFactory.Button(transform, "NEXT →", () =>
        {
            if (string.IsNullOrEmpty(_selectedClassId)) return;
            CharCreateState.PendingClassId = _selectedClassId;
            GameManager.UI?.Push<CharCreateAppearanceScreen>();
        }, 200f).GetComponent<RectTransform>().anchoredPosition = new Vector2(120f, 0f);
    }

    private void BuildClassCard(RectTransform parent, string id, string name, string flavor, string abilities)
    {
        bool isSelected = _selectedClassId == id;
        var card = UIFactory.Panel(parent, $"Card_{id}", isSelected ? UIManager.Theme.accentGold : UIManager.Theme.cardBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        cardRt.sizeDelta = new Vector2(300f, 0f);

        var vstack = UIFactory.VStack(card.transform, UIManager.Theme.spacing * 1.5f, true, "CardContent");
        UIFactory.FillParent(vstack.GetComponent<RectTransform>());

        // Class name
        UIFactory.Label(vstack.transform, name.ToUpper(), UIManager.Theme.fontSizeBody * 1.2f,
                         isSelected ? UIManager.Theme.panelBg : UIManager.Theme.accentGold,
                         TMPro.TextAlignmentOptions.Center);

        // Flavor text
        UIFactory.Label(vstack.transform, flavor, UIManager.Theme.fontSizeSmall,
                         isSelected ? UIManager.Theme.panelBg : UIManager.Theme.textSecondary,
                         TMPro.TextAlignmentOptions.Center);

        if (!string.IsNullOrEmpty(abilities))
        {
            UIFactory.HorizontalDivider(vstack.transform);
            UIFactory.Label(vstack.transform, abilities, UIManager.Theme.fontSizeLabel,
                             isSelected ? UIManager.Theme.panelBg : UIManager.Theme.textPrimary,
                             TMPro.TextAlignmentOptions.Center);
        }

        // Select button
        UIFactory.Button(vstack.transform, isSelected ? "✓ SELECTED" : "SELECT", () =>
        {
            _selectedClassId = id;
            Rebuild();
        }, 200f);
    }

    private void Rebuild()
    {
        // Destroy all children and rebuild (Build already handles selection state)
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        Build();
    }
}
