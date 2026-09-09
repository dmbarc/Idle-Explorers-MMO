using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Changing an existing character's appearance, from a Mirror of Faces.
///
/// The same AppearanceEditor character creation uses, so the two can never offer
/// different options. It edits a COPY and commits on confirm — unlike creation, this
/// character already exists and is standing in the world, so half-finished edits must
/// not leak onto them while the player is still deciding.
///
/// Like ClassChangeModal, the item is spent when the modal opens rather than when a
/// choice is made. Holding it back would mean an inventory slot in limbo and a modal
/// that must survive every route out of it — a screen change, a death, a logout. The
/// cancel button says so plainly.
/// </summary>
public class AppearanceModal : UIScreen
{
    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    private SpumSaveData _draft;

    public override void Build()
    {
        var theme     = UIManager.Theme;
        var character = CharacterManager.Current;

        // No click-to-dismiss on the backdrop: the mirror is already spent, and
        // losing it to a stray click outside the panel would be indefensible.
        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        backdrop.AddComponent<Button>().transition = Selectable.Transition.None;

        var panel   = UIFactory.Panel(transform, "AppearancePanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.10f, 0.08f);
        panelRt.anchorMax = new Vector2(0.90f, 0.92f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        var header = UIFactory.Panel(panel.transform, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.92f, 1f, 1f);

        var title = UIFactory.Label(header.transform, "MIRROR OF FACES", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.02f, 0.45f, 0.98f, 0.95f);

        var note = UIFactory.Label(header.transform,
                                    "Only how you look changes. Your level, skills, gear and talents are untouched.",
                                    theme.fontSizeLabel, theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(note, 0.02f, 0.05f, 0.98f, 0.44f);

        // A copy, so backing out leaves the character exactly as they were.
        _draft = (character?.spumConfig ?? SpumAppearance.Default()).Clone();
        if (_draft.IsEmpty) _draft = SpumAppearance.Default();

        AppearanceEditor.Build(panel.transform, _draft, 0.03f, 0.13f, 0.97f, 0.90f);

        var cancel = UIFactory.Button(panel.transform, "CANCEL (MIRROR IS SPENT)",
                                       () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(cancel, 0.05f, 0.03f, 0.45f, 0.11f);

        var confirm = UIFactory.Button(panel.transform, "LOOK LIKE THIS", Commit, width: 0f);
        UIFactory.At(confirm, 0.55f, 0.03f, 0.95f, 0.11f);
    }

    private void Commit()
    {
        var character = CharacterManager.Current;
        if (character == null)
        {
            GameManager.UI?.Pop();
            return;
        }

        character.spumConfig = _draft;
        GameManager.Save?.Save();

        // The local save is not the source of anything under an authoritative server,
        // and a look kept only there vanishes at the next character select. Fire and
        // forget: the rig has already redrawn and a failed save is retried the next
        // time somebody visits the barber.
        _ = IdleExplorers.Backend.ServerState.SaveAppearanceAsync(character.characterId, _draft);

        // Pop BEFORE announcing, so the live rig redraws behind a screen that is
        // already on its way out rather than under a modal covering it.
        GameManager.UI?.Pop();

        GameEvents.OnAppearanceChanged?.Invoke();
        GameEvents.FireToast("You look like someone else entirely.");
    }
}
