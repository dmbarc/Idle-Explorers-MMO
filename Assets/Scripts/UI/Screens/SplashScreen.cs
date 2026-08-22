using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections;

/// <summary>
/// Screen 1: Splash — logo, loading bar, version.
/// Transitions to Login automatically when ContentManager finishes loading.
/// </summary>
public class SplashScreen : UIScreen
{
    private Image _loadingFill;

    public override void Build()
    {
        // Dark full-screen background
        UIFactory.Panel(transform, "Bg", UIManager.Theme.headerBg, true);

        // Game title
        var title = UIFactory.Label(transform, "IDLE EXPLORERS", UIManager.Theme.fontSizeTitle,
                                     UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.2f, 0.55f);
        titleRt.anchorMax = new Vector2(0.8f, 0.70f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        // Subtitle
        var sub = UIFactory.Label(transform, "A World in Decline", UIManager.Theme.fontSizeBody,
                                   UIManager.Theme.textSecondary, TMPro.TextAlignmentOptions.Center);
        var subRt = sub.GetComponent<RectTransform>();
        subRt.anchorMin = new Vector2(0.25f, 0.48f);
        subRt.anchorMax = new Vector2(0.75f, 0.55f);
        subRt.offsetMin = subRt.offsetMax = Vector2.zero;

        // Loading bar background
        var barBg = UIFactory.Panel(transform, "BarBg", UIManager.Theme.barBg, false);
        var barBgRt = barBg.GetComponent<RectTransform>();
        barBgRt.anchorMin = new Vector2(0.2f, 0.35f);
        barBgRt.anchorMax = new Vector2(0.8f, 0.38f);
        barBgRt.offsetMin = barBgRt.offsetMax = Vector2.zero;

        // Loading fill
        var fillGo = UIFactory.Panel(barBg.transform, "Fill", UIManager.Theme.accentGold, true);
        _loadingFill = fillGo.GetComponent<Image>();
        _loadingFill.type = Image.Type.Filled;
        _loadingFill.fillMethod = Image.FillMethod.Horizontal;
        _loadingFill.fillAmount = 0f;

        // Version label
        var ver = UIFactory.Label(transform, $"v0.1.0 — Alpha", UIManager.Theme.fontSizeLabel,
                                   UIManager.Theme.textDisabled, TMPro.TextAlignmentOptions.Center);
        var verRt = ver.GetComponent<RectTransform>();
        verRt.anchorMin = new Vector2(0.3f, 0.28f);
        verRt.anchorMax = new Vector2(0.7f, 0.33f);
        verRt.offsetMin = verRt.offsetMax = Vector2.zero;
    }

    public override void OnShow()
    {
        StartCoroutine(AnimateLoading());
    }

    private IEnumerator AnimateLoading()
    {
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime * 0.4f; // 2.5s load animation
            if (_loadingFill != null) _loadingFill.fillAmount = t;
            yield return null;
        }
        // Wait for ContentManager to finish, then transition
        while (GameManager.Content != null && !GameManager.Content.IsLoaded)
            yield return new WaitForSeconds(0.1f);

        GameManager.Instance?.TransitionTo(GameManager.GameState.Login);
    }
}
