using TMPro;
using UnityEngine;

/// <summary>
/// Shown when the character's health reaches zero.
///
/// Death currently costs nothing but the walk back — a penalty belongs with the
/// wider progression pass, and punishing an idle game's AFK combat before the
/// numbers are balanced would just be frustrating.
/// </summary>
public class DeathScreen : UIScreen
{
    public override bool IsOverlay => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", new Color(0.15f, 0.02f, 0.02f, 0.78f), true);

        var card = UIFactory.Panel(transform, "DeathCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.34f, 0.34f, 0.66f, 0.66f);

        var title = UIFactory.Label(card.transform, "YOU HAVE DIED", theme.fontSizeTitle,
                                     theme.accentRed, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.70f, 0.95f, 0.88f);

        var flavor = UIFactory.Label(card.transform,
                                      "The world is dying too. It barely noticed.",
                                      theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(flavor, 0.08f, 0.56f, 0.92f, 0.66f);

        var respawnBtn = UIFactory.Button(card.transform, "RESPAWN", Respawn, width: 0f);
        UIFactory.At(respawnBtn, 0.15f, 0.32f, 0.85f, 0.45f);

        var menuBtn = UIFactory.Button(card.transform, "CHARACTER SELECT", () =>
        {
            GameManager.UI?.Pop();
            GameManager.Instance?.ReturnToMainMenu();
        }, width: 0f);
        UIFactory.At(menuBtn, 0.15f, 0.14f, 0.85f, 0.27f);
    }

    private void Respawn()
    {
        var player = FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            // Nothing to revive — fall back to the menu rather than trapping the
            // player on a screen whose only button does nothing.
            GameEvents.FireToast("Could not find your character.");
            GameManager.UI?.Pop();
            GameManager.Instance?.ReturnToMainMenu();
            return;
        }

        player.Respawn();
        GameManager.UI?.Pop();
    }
}
