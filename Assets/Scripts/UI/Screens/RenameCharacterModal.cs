using TMPro;
using UnityEngine;

/// <summary>
/// Rename an existing character, opened from their card on character select.
///
/// Validation goes through CharacterManager.ValidateName, the same rules character
/// creation uses — so a name rejected here is rejected there too, and vice versa.
/// </summary>
public class RenameCharacterModal : UIScreen
{
    /// <summary>Sits over character select rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>The field is seeded from whichever character was picked.</summary>
    public override bool RebuildOnShow => true;

    /// <summary>Set by the caller immediately before Push.</summary>
    public static CharacterData Target;

    private string   _pendingName = "";
    private TMP_Text _hint;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Backdrop closes without renaming
        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<UnityEngine.UI.Button>();
        backdropBtn.transition = UnityEngine.UI.Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var card = UIFactory.Panel(transform, "RenameCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.33f, 0.36f, 0.67f, 0.66f);

        var title = UIFactory.Label(card.transform, "RENAME CHARACTER", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.82f, 0.95f, 0.95f);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.05f, 0.78f, 0.95f, 0.80f);

        _pendingName = Target?.characterName ?? "";

        var currentLabel = UIFactory.Label(card.transform,
                                            $"Currently: {_pendingName}",
                                            theme.fontSizeSmall, theme.textSecondary,
                                            TextAlignmentOptions.Center);
        UIFactory.At(currentLabel, 0.05f, 0.66f, 0.95f, 0.76f);

        var nameField = UIFactory.InputField(card.transform, "New name...",
                                              v => { _pendingName = v; ClearHint(); }, width: 0f);
        UIFactory.At(nameField, 0.10f, 0.46f, 0.90f, 0.62f);
        nameField.characterLimit = CharacterManager.NameMaxLength;
        nameField.text = _pendingName;

        _hint = UIFactory.Label(card.transform, "", theme.fontSizeSmall,
                                 theme.accentRed, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.05f, 0.34f, 0.95f, 0.44f);

        var cancelBtn = UIFactory.Button(card.transform, "CANCEL", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(cancelBtn, 0.10f, 0.08f, 0.47f, 0.28f);

        var confirmBtn = UIFactory.Button(card.transform, "CONFIRM", Confirm, width: 0f);
        UIFactory.At(confirmBtn, 0.53f, 0.08f, 0.90f, 0.28f);
    }

    private void Confirm()
    {
        if (Target == null)
        {
            ShowHint("No character selected.");
            return;
        }

        if (GameManager.Character == null)
        {
            ShowHint("Character manager unavailable.");
            return;
        }

        if (!GameManager.Character.TryRename(Target, _pendingName, out string error))
        {
            ShowHint(error);
            return;
        }

        GameEvents.FireToast($"Renamed to {Target.characterName}.");
        Target = null;

        // Pop returns to character select, whose OnResume refreshes the cards.
        GameManager.UI?.Pop();
    }

    private void ShowHint(string message)
    {
        if (_hint != null) _hint.text = message;
    }

    private void ClearHint()
    {
        if (_hint != null) _hint.text = "";
    }
}
