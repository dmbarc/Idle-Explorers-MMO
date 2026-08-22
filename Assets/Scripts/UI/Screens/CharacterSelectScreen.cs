using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 3: Character Select.
/// Shows account level + XP bar, 4+ character cards, Create button.
/// Character cards display: SPUM live anim, name, level, class, AFK timer.
/// Phase 1: layout + data binding. SPUM preview wired in Phase 2 fully.
/// </summary>
public class CharacterSelectScreen : UIScreen
{
    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);
        BuildHeader();
        BuildCharacterGrid();
        BuildCreateButton();
    }

    public override void OnShow()
    {
        // Refresh grid each time we return here
        Refresh();
    }

    public override void OnResume() => Refresh();

    private void Refresh()
    {
        // Rebuild the grid with fresh data
        // Simple approach: destroy and recreate the grid container
        var grid = transform.Find("CharGrid");
        if (grid != null) Destroy(grid.gameObject);
        BuildCharacterGrid();
    }

    private void BuildHeader()
    {
        var header = UIFactory.Panel(transform, "Header", UIManager.Theme.headerBg, false);
        var rt = header.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.88f);
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var account = AccountManager.Current;
        string acctName  = account?.accountName ?? "Explorer";
        int    acctLevel = account?.accountLevel ?? 1;

        var nameLabel = UIFactory.Label(header.transform, $"{acctName}   Account Lv. {acctLevel}",
                                         UIManager.Theme.fontSizeBody, UIManager.Theme.textPrimary,
                                         TMPro.TextAlignmentOptions.MidlineLeft);
        var nameLabelRt = nameLabel.GetComponent<RectTransform>();
        nameLabelRt.anchorMin = new Vector2(0.02f, 0.2f);
        nameLabelRt.anchorMax = new Vector2(0.6f,  0.8f);
        nameLabelRt.offsetMin = nameLabelRt.offsetMax = Vector2.zero;

        // Account XP bar
        var (barRoot, barFill) = UIFactory.ProgressBar(header.transform, "AcctXPBar",
                                                         UIManager.Theme.xpFill, 400f, 10f);
        var barRt = barRoot.GetComponent<RectTransform>();
        barRt.anchorMin = new Vector2(0.02f, 0.05f);
        barRt.anchorMax = new Vector2(0.50f, 0.18f);
        barRt.offsetMin = barRt.offsetMax = Vector2.zero;
        barRt.sizeDelta = Vector2.zero;
        // Fill amount (0–1)
        if (account != null)
            barFill.fillAmount = (float)(account.accountXP % 100) / 100f; // simplified
    }

    private void BuildCharacterGrid()
    {
        var account = AccountManager.Current;
        int totalSlots = 4; // start with 4; SlotUnlockConfig can expand this

        var gridGo = new GameObject("CharGrid", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        gridGo.transform.SetParent(transform, false);
        var gridRt = gridGo.GetComponent<RectTransform>();
        gridRt.anchorMin = new Vector2(0.05f, 0.12f);
        gridRt.anchorMax = new Vector2(0.95f, 0.86f);
        gridRt.offsetMin = gridRt.offsetMax = Vector2.zero;

        var hlg = gridGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing              = UIManager.Theme.spacing * 2;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth    = true;
        hlg.childControlHeight   = true;

        for (int i = 0; i < totalSlots; i++)
        {
            bool unlocked = GameManager.Account?.IsSlotUnlocked(i) ?? i < 2;
            CharacterData data = (account != null && i < account.characters.Count)
                                 ? account.characters[i] : null;
            BuildCharacterCard(gridGo.transform, i, data, unlocked);
        }
    }

    private void BuildCharacterCard(Transform parent, int slotIndex, CharacterData data, bool unlocked)
    {
        var card = UIFactory.Panel(parent, $"Card_{slotIndex}", UIManager.Theme.cardBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        // Sizing handled by HorizontalLayoutGroup

        if (!unlocked)
        {
            BuildLockedCard(card.transform, slotIndex);
            return;
        }

        if (data == null)
        {
            BuildEmptyCard(card.transform, slotIndex);
            return;
        }

        BuildFilledCard(card.transform, data);
    }

    private void BuildLockedCard(Transform card, int slotIndex)
    {
        UIFactory.Label(card, "🔒", UIManager.Theme.fontSizeTitle,
                         UIManager.Theme.textDisabled, TMPro.TextAlignmentOptions.Center);

        UIFactory.Label(card, "LOCKED", UIManager.Theme.fontSizeSmall,
                         UIManager.Theme.textDisabled, TMPro.TextAlignmentOptions.Center);

        // Show unlock requirements
        var req = GetUnlockText(slotIndex);
        UIFactory.Label(card, req, UIManager.Theme.fontSizeLabel,
                         UIManager.Theme.textSecondary, TMPro.TextAlignmentOptions.Center);
    }

    private void BuildEmptyCard(Transform card, int slotIndex)
    {
        UIFactory.Label(card, "+", UIManager.Theme.fontSizeTitle * 1.5f,
                         UIManager.Theme.textSecondary, TMPro.TextAlignmentOptions.Center);

        UIFactory.Button(card, "CREATE CHARACTER", () =>
        {
            GameManager.Instance?.GoToCharacterCreate();
        }, width: 180f);
    }

    private void BuildFilledCard(Transform card, CharacterData data)
    {
        // Character name
        UIFactory.Label(card, data.characterName, UIManager.Theme.fontSizeBody,
                         UIManager.Theme.textPrimary, TMPro.TextAlignmentOptions.Center);

        // Level + class
        string classDisplay = data.classId ?? "Unknown";
        UIFactory.Label(card, $"Lv. {data.level}  •  {classDisplay}", UIManager.Theme.fontSizeSmall,
                         UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);

        // AFK time or online status
        string afkLabel = data.isOnline ? "⚡ ONLINE"
                        : NumberFormatter.FormatAFKTime(
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - data.lastLogoutUnixTime);
        var afkColor = data.isOnline ? UIManager.Theme.accentGreen : UIManager.Theme.textSecondary;
        UIFactory.Label(card, afkLabel, UIManager.Theme.fontSizeSmall, afkColor,
                         TMPro.TextAlignmentOptions.Center);

        // Play button
        UIFactory.Button(card, "▶  PLAY", () =>
        {
            GameManager.Character?.SelectCharacter(data);
            GameManager.Activity?.ProcessAFKRewards(data);
            GameManager.Instance?.GoToGame();
        }, width: 180f);
    }

    private void BuildCreateButton()
    {
        var btn = UIFactory.Button(transform, "+ NEW CHARACTER",
                                   () => GameManager.Instance?.GoToCharacterCreate(), width: 300f);
        var rt = btn.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.04f);
        rt.anchorMax = new Vector2(0.5f, 0.04f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(300f, UIManager.Theme.buttonHeight);
    }

    private string GetUnlockText(int slotIndex)
    {
        foreach (var req in GameManager.Content?.SlotUnlocks ?? new System.Collections.Generic.List<SlotUnlockRequirement>())
        {
            if (req.slot == slotIndex)
            {
                string s = $"Acct Lv. {req.reqAccountLevel}";
                if (req.reqAnyCharLevel > 0) s += $"\n+ Any Char Lv. {req.reqAnyCharLevel}";
                return s;
            }
        }
        return "???";
    }
}
