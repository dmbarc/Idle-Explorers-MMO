using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;

/// <summary>
/// Screen 1: Splash — the title, the loading bar, and what a player is walking into.
///
/// ══ WHY IT NO LONGER ADVANCES ON ITS OWN ══════════════════════════════════════
///
/// It used to animate a bar for two and a half seconds and move to Login by itself.
/// That is right for a splash and wrong for a notice: a message nobody has to
/// acknowledge is a message that flashes past while the page is still settling, and
/// the whole point of this one is that it be read before anybody forms an opinion of
/// the game.
///
/// So the bar still fills, the content still loads behind it, and the player presses
/// a button. Once per page load — not once ever — because an alpha changes under
/// people and the last thing they read about it should not be from a fortnight ago.
/// </summary>
public class SplashScreen : UIScreen
{
    /// <summary>
    /// What every player sees before anything else.
    ///
    /// Written as one string rather than assembled from parts so that changing the
    /// wording is changing one thing. The address is the owner's own and is here at
    /// their request — it is the whole point of the notice.
    /// </summary>
    private const string AlphaNotice =
        "Thank you for checking out my game! This is extremely early alpha so there " +
        "will be plenty of things that don't make any sense and many possible bugs. " +
        "None of the art is final and many things will be changed in the future.\n\n" +
        "If you find any bugs, exploits, or anything, please reach out to me " +
        "personally, or email me at danielmbarcus@gmail.com.";

    private Image    _loadingFill;
    private Button   _continueButton;
    private TMP_Text _continueLabel;

    /// <summary>Set once the bar has filled and the content is in. Until then, waiting.</summary>
    private bool _ready;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Dark full-screen background
        UIFactory.Panel(transform, "Bg", theme.headerBg, true);

        // Game title
        var title = UIFactory.Label(transform, "IDLE EXPLORERS", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.15f, 0.83f, 0.85f, 0.95f);

        // Subtitle
        var sub = UIFactory.Label(transform, "A World in Decline", theme.fontSizeBody,
                                   theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(sub, 0.20f, 0.77f, 0.80f, 0.83f);

        BuildNotice(theme);

        // ── Loading bar ───────────────────────────────────────────────────────
        var barBg = UIFactory.Panel(transform, "BarBg", theme.barBg, false);
        UIFactory.At(barBg.transform, 0.20f, 0.19f, 0.80f, 0.215f);

        var fillGo = UIFactory.Panel(barBg.transform, "Fill", theme.accentGold, true);

        _loadingFill = fillGo.GetComponent<Image>();
        _loadingFill.type       = Image.Type.Filled;
        _loadingFill.fillMethod = Image.FillMethod.Horizontal;
        _loadingFill.fillAmount = 0f;

        // ── The button that has to be pressed ─────────────────────────────────
        _continueButton = UIFactory.Button(transform, "LOADING…", Continue, width: 0f);
        UIFactory.At(_continueButton, 0.35f, 0.09f, 0.65f, 0.165f);

        _continueButton.interactable = false;
        _continueLabel = _continueButton.GetComponentInChildren<TMP_Text>();

        var ver = UIFactory.Label(transform, "v0.1.0 — Alpha", theme.fontSizeLabel,
                                   theme.textDisabled, TextAlignmentOptions.Center);
        UIFactory.At(ver, 0.30f, 0.04f, 0.70f, 0.085f);
    }

    /// <summary>
    /// The notice itself, on a panel so it reads as a message rather than as more
    /// title screen.
    /// </summary>
    private void BuildNotice(UITheme theme)
    {
        var card = UIFactory.Panel(transform, "Notice", theme.cardBg, false);
        UIFactory.At(card.transform, 0.12f, 0.26f, 0.88f, 0.73f);

        var heading = UIFactory.Label(card.transform, "EARLY ALPHA", theme.fontSizeSmall,
                                       theme.accentRed, TextAlignmentOptions.Center);
        UIFactory.At(heading, 0.05f, 0.84f, 0.95f, 0.96f);

        var body = UIFactory.Label(card.transform, AlphaNotice, theme.fontSizeSmall,
                                    theme.textPrimary, TextAlignmentOptions.TopLeft);

        // Wrapped, because this is four sentences rather than a label — and the whole
        // notice has to be legible at whatever width the browser window happens to be.
        body.textWrappingMode = TextWrappingModes.Normal;

        UIFactory.At(body, 0.06f, 0.06f, 0.94f, 0.82f);
    }

    public override void OnShow()
    {
        _ready = false;
        StartCoroutine(AnimateLoading());
    }

    private IEnumerator AnimateLoading()
    {
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime * 0.4f;   // 2.5s load animation
            if (_loadingFill != null) _loadingFill.fillAmount = t;
            yield return null;
        }

        // Wait for ContentManager to finish. The button stays dead until it has:
        // pressing through to Login with an empty catalogue is how a player reaches a
        // character screen with no classes on it.
        while (GameManager.Content != null && !GameManager.Content.IsLoaded)
            yield return new WaitForSeconds(0.1f);

        _ready = true;

        if (_loadingFill != null) _loadingFill.fillAmount = 1f;

        if (_continueButton != null) _continueButton.interactable = true;
        if (_continueLabel  != null) _continueLabel.text = "CONTINUE";
    }

    /// <summary>
    /// Moves on. Guarded on _ready as well as on the button, because a button is a
    /// thing a script can click and the content genuinely has to be loaded first.
    /// </summary>
    private void Continue()
    {
        if (!_ready) return;

        GameManager.Instance?.TransitionTo(GameManager.GameState.Login);
    }
}
