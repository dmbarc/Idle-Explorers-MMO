using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen 9: the in-game menu behind the ≡ button.
///
/// Previously ≡ dropped you straight to character select with no confirmation.
/// It now opens a proper menu, with Settings nested inside it.
/// </summary>
public class MenuModal : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>The activity-panel option's label reflects current state.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Backdrop closes the menu when clicked outside the card
        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var card = UIFactory.Panel(transform, "MenuCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.38f, 0.28f, 0.62f, 0.76f);

        var title = UIFactory.Label(card.transform, "MENU", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.86f, 0.95f, 0.97f);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.05f, 0.83f, 0.95f, 0.85f);

        var stack = UIFactory.VStack(card.transform, theme.spacing, true, "MenuOptions");
        UIFactory.At(stack.transform, 0.08f, 0.06f, 0.92f, 0.80f);

        AddOption(stack.transform, "RESUME",   () => GameManager.UI?.Pop());
        AddOption(stack.transform, "SETTINGS", () => GameManager.UI?.Push<SettingsPanel>());
        AddOption(stack.transform, ToggleActivityLabel(), ToggleActivityPanel);
        AddOption(stack.transform, "SAVE GAME", () =>
        {
            GameManager.Save?.SaveActiveState();
            GameEvents.FireToast("Progress saved.");
        });
        AddOption(stack.transform, "CHARACTER SELECT", () =>
        {
            // SaveAndDisconnect stamps the logout time, which is what AFK accrual
            // measures from — leaving via this button must go through it.
            GameManager.UI?.Pop();
            GameManager.Instance?.ReturnToMainMenu();
        });
    }

    private void AddOption(Transform parent, string label, System.Action onClick)
    {
        var btn = UIFactory.Button(parent, label, onClick, width: 0f);
        var le  = btn.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = UIManager.Theme.buttonHeight;
    }

    private string ToggleActivityLabel()
    {
        var hud = FindAnyObjectByType<GameHUD>();
        return hud != null && hud.IsActivityPanelVisible ? "HIDE ACTIVITY PANEL" : "SHOW ACTIVITY PANEL";
    }

    private void ToggleActivityPanel()
    {
        var hud = FindAnyObjectByType<GameHUD>();
        if (hud == null) return;

        bool nowVisible = !hud.IsActivityPanelVisible;
        hud.SetActivityPanelVisible(nowVisible);
        GameEvents.FireToast(nowVisible ? "Activity panel shown" : "Activity panel hidden");
        GameManager.UI?.Pop();
    }
}
