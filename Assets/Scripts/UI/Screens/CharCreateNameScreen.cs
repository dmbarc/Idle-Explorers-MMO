using TMPro;
using UnityEngine;

/// <summary>Screen 4 of character creation: name entry.</summary>
public class CharCreateNameScreen : UIScreen
{
    private string _pendingName = "";
    private TMP_Text _hint;

    /// <summary>The name field is seeded from creation state, which changes between visits.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Carry the name back when stepping Back from the class screen; blank for
        // a fresh character, because GoBack resets the shared state.
        _pendingName = CharCreateState.PendingName ?? "";

        UIFactory.Panel(transform, "Bg", theme.panelBg, true);

        // Every element is explicitly anchored. Without this the title, the field
        // and both buttons all default to a centred stretch and draw on top of
        // one another.
        var title = UIFactory.Label(transform, "NAME YOUR EXPLORER", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.20f, 0.74f, 0.80f, 0.86f);

        // Says what actually happens next. The old copy promised the appearance
        // editor, but GoNext pushes the CLASS picker — appearance is the screen after
        // that, and a first-run player following the sentence hit the wrong screen.
        var subtitle = UIFactory.Label(transform, "Choose a class and customise your look next.",
                                        theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(subtitle, 0.25f, 0.68f, 0.75f, 0.73f);

        var nameField = UIFactory.InputField(transform, "Enter name...",
                                              v => { _pendingName = v; ClearHint(); }, width: 500f);
        UIFactory.At(nameField, 0.33f, 0.50f, 0.67f, 0.58f);
        nameField.characterLimit = 20;
        nameField.text = _pendingName;

        _hint = UIFactory.Label(transform, "", theme.fontSizeSmall,
                                 theme.accentRed, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.30f, 0.43f, 0.70f, 0.48f);

        var backBtn = UIFactory.Button(transform, "← BACK", GoBack, width: 0f);
        UIFactory.At(backBtn, 0.33f, 0.28f, 0.47f, 0.35f);

        var nextBtn = UIFactory.Button(transform, "NEXT →", GoNext, width: 0f);
        UIFactory.At(nextBtn, 0.53f, 0.28f, 0.67f, 0.35f);
    }

    public override void OnShow()
    {
        // Returning from the class screen should not keep a stale error visible
        ClearHint();
    }

    private void GoNext()
    {
        // Same rules the rename modal uses, so a name accepted here can never be
        // rejected later — including the duplicate check creation used to skip.
        if (!CharacterManager.ValidateName(_pendingName, null, out string error))
        {
            ShowHint(error);
            return;
        }

        CharCreateState.PendingName = _pendingName.Trim();
        GameManager.UI?.Push<CharCreateClassScreen>();
    }

    /// <summary>
    /// Back out of character creation entirely. This screen is the bottom of the
    /// creation stack, so Pop() would leave an empty stack and a black screen —
    /// it has to transition states instead.
    /// </summary>
    private void GoBack()
    {
        CharCreateState.Reset();
        GameManager.Instance?.GoToCharacterSelect();
    }

    private void ShowHint(string message)
    {
        if (_hint != null) _hint.text = message;
        GameEvents.FireToast(message);
    }

    private void ClearHint()
    {
        if (_hint != null) _hint.text = "";
    }
}

/// <summary>Temporary state passed between character creation screens.</summary>
public static class CharCreateState
{
    public static string       PendingName;
    public static string       PendingClassId;

    /// <summary>
    /// Seeded with a real look rather than an empty struct, so a player who skips
    /// straight through the appearance screen still gets a character rather than an
    /// invisible rig. The appearance screen edits this in place.
    /// </summary>
    public static SpumSaveData PendingSpum = SpumAppearance.Default();

    public static void Reset()
    {
        PendingName    = null;
        PendingClassId = null;
        PendingSpum    = SpumAppearance.Default();
    }
}
