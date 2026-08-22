using TMPro;
using UnityEngine;

/// <summary>
/// Screen 2: Login.
///
/// Phase 1 has no server, so this authenticates against the local save: LOGIN
/// requires a matching saved account, CREATE ACCOUNT requires that none exists.
/// Previously both buttons did exactly the same thing, which made the screen
/// meaningless. Phase 8 replaces this with real server auth.
///
/// No password is stored or checked — see the note on the field below.
/// </summary>
public class LoginScreen : UIScreen
{
    private TMP_InputField _nameField;
    private TMP_Text       _hint;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Bg", theme.panelBg, true);

        var title = UIFactory.Label(transform, "IDLE EXPLORERS", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.20f, 0.72f, 0.80f, 0.82f);

        var tagline = UIFactory.Label(transform, "A World in Decline", theme.fontSizeSmall,
                                       theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(tagline, 0.25f, 0.67f, 0.75f, 0.71f);

        _nameField = UIFactory.InputField(transform, "Account Name", _ => ClearHint(), width: 400f);
        UIFactory.At(_nameField, 0.35f, 0.53f, 0.65f, 0.60f);
        _nameField.characterLimit = 20;

        // Local-only placeholder: nothing is hashed, transmitted or stored, so it
        // is deliberately not validated. Real credentials arrive with the server
        // in Phase 8 — do not start persisting this field before then.
        var pass = UIFactory.InputField(transform, "Password (unused in Phase 1)", null, width: 400f);
        UIFactory.At(pass, 0.35f, 0.44f, 0.65f, 0.51f);
        pass.contentType = TMP_InputField.ContentType.Password;

        _hint = UIFactory.Label(transform, "", theme.fontSizeSmall, theme.accentRed, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.25f, 0.38f, 0.75f, 0.42f);

        var loginBtn = UIFactory.Button(transform, "LOGIN", AttemptLogin, width: 0f);
        UIFactory.At(loginBtn, 0.35f, 0.28f, 0.65f, 0.35f);

        var createBtn = UIFactory.Button(transform, "CREATE ACCOUNT", AttemptCreate, width: 0f);
        UIFactory.At(createBtn, 0.35f, 0.19f, 0.65f, 0.26f);
    }

    public override void OnShow()
    {
        ClearHint();

        // Pre-fill the saved account so returning players just press LOGIN
        var existing = AccountManager.Current;
        if (_nameField != null && existing != null && !string.IsNullOrEmpty(existing.accountName))
            _nameField.text = existing.accountName;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void AttemptLogin()
    {
        string entered = _nameField != null ? _nameField.text.Trim() : "";
        if (string.IsNullOrEmpty(entered))
        {
            ShowHint("Enter your account name.");
            return;
        }

        bool hasSave = GameManager.Save?.HasSave ?? false;
        var  account = AccountManager.Current;

        if (!hasSave || account == null)
        {
            ShowHint("No account found. Use CREATE ACCOUNT.");
            return;
        }

        if (!string.Equals(account.accountName, entered, System.StringComparison.OrdinalIgnoreCase))
        {
            ShowHint($"No account named '{entered}' on this device.");
            return;
        }

        GameManager.Account?.LoadAccount(account);
        GameManager.Instance?.GoToCharacterSelect();
    }

    private void AttemptCreate()
    {
        string entered = _nameField != null ? _nameField.text.Trim() : "";
        if (string.IsNullOrEmpty(entered))
        {
            ShowHint("Choose an account name first.");
            return;
        }

        if (GameManager.Save?.HasSave == true)
        {
            ShowHint("An account already exists on this device. Use LOGIN.");
            return;
        }

        var account = AccountManager.Current ?? new AccountData();
        account.accountName = entered;
        account.accountId   = System.Guid.NewGuid().ToString();

        GameManager.Account?.LoadAccount(account);
        GameManager.Save?.Save();

        GameEvents.FireToast($"Welcome, {entered}.");
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
