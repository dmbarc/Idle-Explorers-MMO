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

        var theme = UIManager.Theme;

        // ── The character themselves ──────────────────────────────────────────
        //
        // This said "(Appearance: default — customise in Phase 2)" for the whole life
        // of the project, including after the appearance editor shipped: the last
        // thing you saw before creating a character was a line of text telling you
        // their appearance did not exist yet. It is the one screen where showing them
        // matters most.
        var portrait = UIFactory.Panel(card.transform, "Portrait", theme.slotBg, false);
        UIFactory.At(portrait.transform, 0.06f, 0.08f, 0.44f, 0.92f);

        var cls = GameManager.Content?.GetClass(CharCreateState.PendingClassId);

        var preview = CharacterPreview.Create(portrait.transform,
                                               CharCreateState.PendingSpum ?? SpumAppearance.Default(),
                                               "Preview_Confirm");
        if (preview != null && cls?.previewEquipment != null)
        {
            // Dressed the way their class's card was, so the character they picked and
            // the character they are about to make look like the same person.
            foreach (var piece in cls.previewEquipment)
            {
                if (piece == null || string.IsNullOrEmpty(piece.slot)) continue;
                preview.SetEquipment(piece.slot, piece.sprite);
            }
        }

        // ── Who they are ──────────────────────────────────────────────────────
        var vstack = UIFactory.VStack(card.transform, theme.spacing * 2, true, "Summary");
        UIFactory.At(vstack.transform, 0.48f, 0.08f, 0.94f, 0.92f);

        UIFactory.Label(vstack.transform, CharCreateState.PendingName ?? "Unknown",
                         theme.fontSizeTitle, theme.textPrimary,
                         TMPro.TextAlignmentOptions.Center);

        // The class's display name, not its id. They happen to match today, and an id
        // is not a thing to show a player on the strength of a coincidence.
        string classDisplay = cls?.DisplayName ?? CharCreateState.PendingClassId ?? "None";
        UIFactory.Label(vstack.transform, $"Class: {classDisplay.ToUpper()}",
                         theme.fontSizeBody, theme.accentGold,
                         TMPro.TextAlignmentOptions.Center);

        UIFactory.Label(vstack.transform, "Level 1 — Fresh Explorer",
                         theme.fontSizeSmall, theme.textSecondary,
                         TMPro.TextAlignmentOptions.Center);

        UIFactory.HorizontalDivider(vstack.transform);

        if (!string.IsNullOrEmpty(cls?.flavorText))
        {
            var flavor = UIFactory.Label(vstack.transform, cls.flavorText,
                                          theme.fontSizeSmall, theme.textSecondary,
                                          TMPro.TextAlignmentOptions.Top);
            flavor.textWrappingMode = TMPro.TextWrappingModes.Normal;
        }

        UIFactory.Label(vstack.transform, "You can change how they look later with a Mirror of Faces.",
                         theme.fontSizeLabel, theme.textDisabled,
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

    /// <summary>
    /// Builds the character, waits for the server to own it, then enters the world.
    ///
    /// async void because it is a button handler; the awaits inside are what make the
    /// character real before anything is done with it.
    /// </summary>
    private async void CreateCharacter()
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
            // A DRAFT id. Offline it is the real one; connected, the server replaces it
            // and CreateCharacter returns the entry that carries the replacement.
            characterId        = System.Guid.NewGuid().ToString(),
            characterName      = CharCreateState.PendingName,
            classId            = CharCreateState.PendingClassId,
            classIds           = new System.Collections.Generic.List<string> { CharCreateState.PendingClassId },
            level              = 1,
            xp                 = 0,
            lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            isOnline           = true,
            lastMapId          = "goblin_camp",
            allowGhostDisplay  = true,
            spumConfig         = CharCreateState.PendingSpum ?? SpumAppearance.Default(),
        };

        // ══ AWAITED, AND THE ANSWER IS WHAT GETS PLAYED ══════════════════════
        //
        // Under an authoritative server the id above is a DRAFT. The server mints the
        // real one, and CreateCharacter hands back the roster entry carrying it.
        //
        // This used to fire and forget, then select newChar on the next line -- so the
        // character being played wore a Guid the server had never heard of and every
        // call about it 404ed. Silently: the activity never changed, so somebody who
        // set their character fishing was still fighting goblins as far as the server
        // was concerned; nothing saved; and a mystic gem answered "no such character".
        CharacterData created = GameManager.Character != null
            ? await GameManager.Character.CreateCharacter(newChar)
            : newChar;

        if (created == null)
        {
            // CreateCharacter has already said why. Staying here beats dropping
            // somebody into a world with no character.
            return;
        }

        // Creating does not make it the active character — without this,
        // CharacterManager.Current stays null and the world spawns with no player
        // stats, no inventory target and no save.
        if (GameManager.Character != null)
            await GameManager.Character.SelectCharacterAsync(created);

        GameManager.Save?.Save();

        GameEvents.FireToast($"Welcome, {created.characterName}!");

        // Clear creation state
        CharCreateState.PendingName    = null;
        CharCreateState.PendingClassId = null;
        CharCreateState.PendingSpum    = SpumAppearance.Default();

        GameManager.Instance?.GoToGame();
    }
}
