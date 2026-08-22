using UnityEngine;

/// <summary>
/// Screen 2: Login — account name + password, Login/Register buttons.
/// Phase 1: stub — immediately goes to CharacterSelect with a test account.
/// Phase 8: real server authentication.
/// </summary>
public class LoginScreen : UIScreen
{
    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);

        var title = UIFactory.Label(transform, "IDLE EXPLORERS", UIManager.Theme.fontSizeTitle,
                                     UIManager.Theme.accentGold, TMPro.TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.2f, 0.65f);
        titleRt.anchorMax = new Vector2(0.8f, 0.75f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        // Username field
        var user = UIFactory.InputField(transform, "Account Name", width: 400f);
        var userRt = user.GetComponent<RectTransform>();
        userRt.anchorMin = new Vector2(0.5f, 0.55f);
        userRt.anchorMax = new Vector2(0.5f, 0.55f);
        userRt.anchoredPosition = Vector2.zero;
        userRt.sizeDelta = new Vector2(400f, UIManager.Theme.buttonHeight);

        // Password field
        var pass = UIFactory.InputField(transform, "Password", width: 400f);
        pass.contentType = TMPro.TMP_InputField.ContentType.Password;
        var passRt = pass.GetComponent<RectTransform>();
        passRt.anchorMin = new Vector2(0.5f, 0.45f);
        passRt.anchorMax = new Vector2(0.5f, 0.45f);
        passRt.anchoredPosition = Vector2.zero;
        passRt.sizeDelta = new Vector2(400f, UIManager.Theme.buttonHeight);

        // Login button
        var loginBtn = UIFactory.Button(transform, "LOGIN", () =>
        {
            // Phase 1 stub: skip authentication
            GameManager.Account?.LoadAccount(AccountManager.Current);
            GameManager.Instance?.GoToCharacterSelect();
        }, width: 400f);
        var loginRt = loginBtn.GetComponent<RectTransform>();
        loginRt.anchorMin = new Vector2(0.5f, 0.35f);
        loginRt.anchorMax = new Vector2(0.5f, 0.35f);
        loginRt.anchoredPosition = Vector2.zero;
        loginRt.sizeDelta = new Vector2(400f, UIManager.Theme.buttonHeight);

        // Register button (stub)
        var regBtn = UIFactory.Button(transform, "CREATE ACCOUNT", () =>
        {
            Debug.Log("[LoginScreen] Register — stub for Phase 8");
            GameManager.Instance?.GoToCharacterSelect(); // shortcut for now
        }, width: 400f);
        var regRt = regBtn.GetComponent<RectTransform>();
        regRt.anchorMin = new Vector2(0.5f, 0.27f);
        regRt.anchorMax = new Vector2(0.5f, 0.27f);
        regRt.anchoredPosition = Vector2.zero;
        regRt.sizeDelta = new Vector2(400f, UIManager.Theme.buttonHeight);
    }
}
