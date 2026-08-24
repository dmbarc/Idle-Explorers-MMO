using TMPro;
using UnityEngine;

/// <summary>
/// Screen 6 of character creation: what the character looks like.
///
/// This was a mockup for the whole life of the project — its "← Prev / Next →" rows
/// were Labels rather than Buttons, the class had no fields at all, and the preview
/// was a panel containing the text "(Phase 2: SPUM live appearance here)". Nothing it
/// showed was connected to anything, and SpumSaveData was written all-zero and read
/// by nothing.
///
/// The editor itself lives in AppearanceEditor so the barber item can reopen exactly
/// this, rather than a second implementation offering a different set of hairstyles.
/// </summary>
public class CharCreateAppearanceScreen : UIScreen
{
    /// <summary>The preview and every row read the pending look, so it rebuilds per visit.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Bg", theme.panelBg, true);

        var title = UIFactory.Label(transform, "CUSTOMIZE APPEARANCE", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.90f, 0.95f, 0.98f);

        var hint = UIFactory.Label(transform,
                                    "You can change all of this later with a Mirror of Faces.",
                                    theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(hint, 0.15f, 0.855f, 0.85f, 0.895f);

        // Edits CharCreateState.PendingSpum in place, which is what
        // CharCreateConfirmScreen writes onto the new character.
        CharCreateState.PendingSpum ??= SpumAppearance.Default();
        AppearanceEditor.Build(transform, CharCreateState.PendingSpum, 0.06f, 0.16f, 0.94f, 0.845f);

        var backBtn = UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(backBtn, 0.33f, 0.05f, 0.47f, 0.12f);

        var nextBtn = UIFactory.Button(transform, "NEXT →",
                                        () => GameManager.UI?.Push<CharCreateConfirmScreen>(), width: 0f);
        UIFactory.At(nextBtn, 0.53f, 0.05f, 0.67f, 0.12f);
    }
}
