using UnityEngine;

/// <summary>
/// Screen 6 of character creation: appearance customisation.
/// Phase 1: placeholder sliders. Phase 2: SPUM live preview + full colour/part pickers.
/// </summary>
public class CharCreateAppearanceScreen : UIScreen
{
    /// <summary>Will render live SPUM state in Phase 2; rebuilt now so it stays correct then.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);

        // Title
        var title = UIFactory.Label(transform, "CUSTOMISE APPEARANCE", UIManager.Theme.fontSizeTitle,
                                     UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.05f, 0.88f);
        titleRt.anchorMax = new Vector2(0.95f, 0.97f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        // Preview placeholder (Phase 2: SPUM live preview)
        var previewPanel = UIFactory.Panel(transform, "SPUMPreview", UIManager.Theme.cardBg, false);
        var previewRt = previewPanel.GetComponent<RectTransform>();
        previewRt.anchorMin = new Vector2(0.05f, 0.20f);
        previewRt.anchorMax = new Vector2(0.45f, 0.85f);
        previewRt.offsetMin = previewRt.offsetMax = Vector2.zero;

        UIFactory.Label(previewPanel.transform,
                         "Character Preview\n\n(Phase 2: SPUM live appearance here)",
                         UIManager.Theme.fontSizeSmall, UIManager.Theme.textDisabled,
                         TMPro.TextAlignmentOptions.Center);

        // Controls placeholder
        var controlsPanel = UIFactory.Panel(transform, "Controls", UIManager.Theme.cardBg, false);
        var controlsRt = controlsPanel.GetComponent<RectTransform>();
        controlsRt.anchorMin = new Vector2(0.50f, 0.20f);
        controlsRt.anchorMax = new Vector2(0.95f, 0.85f);
        controlsRt.offsetMin = controlsRt.offsetMax = Vector2.zero;

        var vstack = UIFactory.VStack(controlsPanel.transform, UIManager.Theme.spacing * 2, true, "Controls");
        UIFactory.FillParent(vstack.GetComponent<RectTransform>());

        UIFactory.Label(vstack.transform, "Hair Style", UIManager.Theme.fontSizeSmall,
                         UIManager.Theme.textSecondary, TMPro.TextAlignmentOptions.Left);
        UIFactory.Label(vstack.transform, "← Prev    [Style 1]    Next →", UIManager.Theme.fontSizeBody,
                         UIManager.Theme.textPrimary, TMPro.TextAlignmentOptions.Center);

        UIFactory.HorizontalDivider(vstack.transform);

        UIFactory.Label(vstack.transform, "Body Type", UIManager.Theme.fontSizeSmall,
                         UIManager.Theme.textSecondary, TMPro.TextAlignmentOptions.Left);
        UIFactory.Label(vstack.transform, "← Prev    [Type 1]    Next →", UIManager.Theme.fontSizeBody,
                         UIManager.Theme.textPrimary, TMPro.TextAlignmentOptions.Center);

        UIFactory.HorizontalDivider(vstack.transform);

        UIFactory.Label(vstack.transform, "Phase 2: Full SPUM colour pickers", UIManager.Theme.fontSizeLabel,
                         UIManager.Theme.textDisabled, TMPro.TextAlignmentOptions.Center);

        // Navigation
        var backBtn = UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), 150f);
        var backRt = backBtn.GetComponent<RectTransform>();
        backRt.anchorMin = new Vector2(0.3f, 0.04f);
        backRt.anchorMax = new Vector2(0.3f, 0.04f);
        backRt.anchoredPosition = Vector2.zero;
        backRt.sizeDelta = new Vector2(150f, UIManager.Theme.buttonHeight);

        var nextBtn = UIFactory.Button(transform, "NEXT →", () =>
        {
            // Appearance data stored in CharCreateState.PendingSpum (Phase 2)
            GameManager.UI?.Push<CharCreateConfirmScreen>();
        }, 200f);
        var nextRt = nextBtn.GetComponent<RectTransform>();
        nextRt.anchorMin = new Vector2(0.65f, 0.04f);
        nextRt.anchorMax = new Vector2(0.65f, 0.04f);
        nextRt.anchoredPosition = Vector2.zero;
        nextRt.sizeDelta = new Vector2(200f, UIManager.Theme.buttonHeight);
    }
}
