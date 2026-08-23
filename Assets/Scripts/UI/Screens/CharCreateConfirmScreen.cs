using System;
using UnityEngine;

/// <summary>
/// Screen 7 of character creation: summary + final confirm.
/// Shows name, class, appearance, then calls CharacterManager.CreateCharacter.
/// </summary>
public class CharCreateConfirmScreen : UIScreen
{
    /// <summary>The whole card is rendered from CharCreateState at build time.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);

        // Title
        var title = UIFactory.Label(transform, "CONFIRM CHARACTER", UIManager.Theme.fontSizeTitle,
                                     UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.05f, 0.88f);
        titleRt.anchorMax = new Vector2(0.95f, 0.97f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        // Summary card
        var card = UIFactory.Panel(transform, "SummaryCard", UIManager.Theme.cardBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.20f, 0.25f);
        cardRt.anchorMax = new Vector2(0.80f, 0.85f);
        cardRt.offsetMin = cardRt.offsetMax = Vector2.zero;

        var vstack = UIFactory.VStack(card.transform, UIManager.Theme.spacing * 2, true, "Summary");
        UIFactory.FillParent(vstack.GetComponent<RectTransform>());

        // Character name
        UIFactory.Label(vstack.transform, CharCreateState.PendingName ?? "Unknown",
                         UIManager.Theme.fontSizeTitle, UIManager.Theme.textPrimary,
                         TMPro.TextAlignmentOptions.Center);

        // Class
        string classDisplay = CharCreateState.PendingClassId ?? "None";
        UIFactory.Label(vstack.transform, $"Class: {classDisplay.ToUpper()}",
                         UIManager.Theme.fontSizeBody, UIManager.Theme.accentGold,
                         TMPro.TextAlignmentOptions.Center);

        // Level 1 badge
        UIFactory.Label(vstack.transform, "Level 1 — Fresh Explorer",
                         UIManager.Theme.fontSizeSmall, UIManager.Theme.textSecondary,
                         TMPro.TextAlignmentOptions.Center);

        UIFactory.HorizontalDivider(vstack.transform);

        // Appearance summary placeholder
        UIFactory.Label(vstack.transform, "(Appearance: default — customise in Phase 2)",
                         UIManager.Theme.fontSizeLabel, UIManager.Theme.textDisabled,
                         TMPro.TextAlignmentOptions.Center);

        // Navigation
        var backBtn = UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), 150f);
        var backRt = backBtn.GetComponent<RectTransform>();
        backRt.anchorMin = new Vector2(0.3f, 0.06f);
        backRt.anchorMax = new Vector2(0.3f, 0.06f);
        backRt.anchoredPosition = Vector2.zero;
        backRt.sizeDelta = new Vector2(150f, UIManager.Theme.buttonHeight);

        var createBtn = UIFactory.Button(transform, "✦  CREATE CHARACTER  ✦", () =>
        {
            CreateCharacter();
        }, 320f);
        var createRt = createBtn.GetComponent<RectTransform>();
        createRt.anchorMin = new Vector2(0.65f, 0.06f);
        createRt.anchorMax = new Vector2(0.65f, 0.06f);
        createRt.anchoredPosition = Vector2.zero;
        createRt.sizeDelta = new Vector2(320f, UIManager.Theme.buttonHeight);
    }

    private void CreateCharacter()
    {
        if (string.IsNullOrEmpty(CharCreateState.PendingClassId))
        {
            GameEvents.FireToast("Class is missing — go back and pick one.");
            return;
        }

        // Re-checked at the last moment as well as on the name screen: another
        // character could have been created (or renamed) in between.
        if (!CharacterManager.ValidateName(CharCreateState.PendingName, null, out string nameError))
        {
            GameEvents.FireToast(nameError);
            return;
        }

        var newChar = new CharacterData
        {
            characterId        = System.Guid.NewGuid().ToString(),
            characterName      = CharCreateState.PendingName,
            classId            = CharCreateState.PendingClassId,
            level              = 1,
            xp                 = 0,
            lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            isOnline           = true,
            lastMapId          = "goblin_camp",
            allowGhostDisplay  = true,
            spumConfig         = CharCreateState.PendingSpum ?? SpumAppearance.Default(),
        };

        GameManager.Character?.CreateCharacter(newChar);

        // Creating does not make it the active character — without this,
        // CharacterManager.Current stays null and the world spawns with no player
        // stats, no inventory target and no save.
        GameManager.Character?.SelectCharacter(newChar);
        GameManager.Save?.Save();

        GameEvents.FireToast($"Welcome, {newChar.characterName}!");

        // Clear creation state
        CharCreateState.PendingName    = null;
        CharCreateState.PendingClassId = null;
        CharCreateState.PendingSpum    = SpumAppearance.Default();

        GameManager.Instance?.GoToGame();
    }
}
