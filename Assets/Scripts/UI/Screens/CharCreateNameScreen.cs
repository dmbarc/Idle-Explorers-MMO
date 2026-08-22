using UnityEngine;

/// <summary>Screen 4 of character creation: name entry.</summary>
public class CharCreateNameScreen : UIScreen
{
    private string _pendingName = "";

    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);

        UIFactory.Label(transform, "NAME YOUR EXPLORER", UIManager.Theme.fontSizeTitle,
                         UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center)
                  .GetComponent<RectTransform>().SetAsFirstSibling();

        var nameField = UIFactory.InputField(transform, "Enter name...",
                                              v => _pendingName = v, width: 500f);
        var rt = nameField.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(500f, UIManager.Theme.buttonHeight);

        UIFactory.Button(transform, "NEXT →", () =>
        {
            if (string.IsNullOrWhiteSpace(_pendingName)) return;
            // Store the pending name somewhere accessible to CharCreateClassScreen
            CharCreateState.PendingName = _pendingName.Trim();
            GameManager.UI?.Push<CharCreateClassScreen>();
        }, 300f);

        UIFactory.Button(transform, "← BACK", () =>
        {
            GameManager.UI?.Pop();
        }, 150f);
    }
}

/// <summary>Temporary state passed between character creation screens.</summary>
public static class CharCreateState
{
    public static string     PendingName;
    public static string     PendingClassId;
    public static SpumSaveData PendingSpum = new SpumSaveData();
}
