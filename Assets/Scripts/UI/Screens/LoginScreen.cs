using TMPro;
using UnityEngine;

/// <summary>
/// Screen 2: Login. Two screens, really, depending on whether there is a server.
///
/// ══ WHY IT IS BOTH ════════════════════════════════════════════════════════════
///
/// CONNECTED, it signs in against Supabase and the password field is real. OFFLINE, it
/// authenticates against the local save exactly as it always did, and the password is
/// still ignored.
///
/// Keeping the offline path is not sentiment. Opening the project and pressing play
/// must not require a server, an account or a network, or the iteration loop dies for
/// everybody working on presentation. The mode is decided by whether a ServerConfig
/// asset names an API -- there is no toggle to leave in the wrong position.
///
/// ══ WHAT IS DONE WITH THE PASSWORD ════════════════════════════════════════════
///
/// Connected: sent over HTTPS to Supabase, and nowhere else. Never written to disk,
/// never logged, never put in a URL, and not held after the request. What comes back
/// is a token; the refresh half of it is what persists, not the password.
///
/// Offline: still ignored, and the field says so.
/// </summary>
public class LoginScreen : UIScreen
{
    private TMP_InputField _nameField;
    private TMP_InputField _passField;
    private TMP_Text       _hint;

    /// <summary>True when there is a server to sign in to. Decided by the config asset.</summary>
    private static bool Connected => IdleExplorers.Backend.Session.HasServer;

    /// <summary>Stops a second press while a request is in flight.</summary>
    private bool _busy;

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

        _nameField = UIFactory.InputField(transform,
                                          Connected ? "Email" : "Account Name",
                                          _ => ClearHint(), width: 400f);
        UIFactory.At(_nameField, 0.35f, 0.53f, 0.65f, 0.60f);
        // An account name is short; an email is not, and a 20-character limit silently
        // truncates one into something that will never authenticate.
        _nameField.characterLimit = Connected ? 254 : 20;

        if (Connected) _nameField.contentType = TMP_InputField.ContentType.EmailAddress;

        // Connected, this is real and goes to Supabase over HTTPS. Offline it is
        // ignored, and the placeholder says which -- a password box that silently does
        // nothing is worse than no password box.
        _passField = UIFactory.InputField(
            transform,
            Connected ? "Password" : "Password (not used offline)",
            _ => ClearHint(), width: 400f);

        UIFactory.At(_passField, 0.35f, 0.44f, 0.65f, 0.51f);
        _passField.contentType = TMP_InputField.ContentType.Password;

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

        // Pre-fill the saved account so returning players just press LOGIN.
        //
        // Only when a save actually exists. AccountManager.Awake fabricates a stub
        // named "Adventurer" when there is none, and pre-filling that handed every
        // first-time player the placeholder — press CREATE ACCOUNT without clearing
        // the field and your account is named after it.
        bool hasSave = GameManager.Save?.HasSave ?? false;
        var  existing = AccountManager.Current;

        if (_nameField != null)
            _nameField.text = (hasSave && !string.IsNullOrEmpty(existing?.accountName))
                ? existing.accountName
                : "";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private async void AttemptLogin()
    {
        if (_busy) return;

        if (Connected) { await SignInAsync(signUp: false); return; }

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

    private async void AttemptCreate()
    {
        if (_busy) return;

        if (Connected) { await SignInAsync(signUp: true); return; }

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

    /// <summary>
    /// Signs in or signs up, and says what happened.
    ///
    /// One method for both, because the only difference is which call is made and the
    /// error handling is identical -- and duplicating it is how one of the two ends up
    /// forgetting to re-enable the buttons.
    /// </summary>
    private async Awaitable SignInAsync(bool signUp)
    {
        string email    = _nameField != null ? _nameField.text.Trim() : "";
        string password = _passField != null ? _passField.text : "";

        if (string.IsNullOrEmpty(email))    { ShowHint("Enter your email."); return; }
        if (string.IsNullOrEmpty(password)) { ShowHint("Enter your password."); return; }

        _busy = true;
        ShowHint(signUp ? "Creating your account…" : "Signing in…");

        try
        {
            var result = signUp
                ? await IdleExplorers.Backend.Session.SignUpAsync(email, password)
                : await IdleExplorers.Backend.Session.SignInAsync(email, password);

            if (result.Ok)
            {
                // Cleared the moment it is no longer needed. It was never written
                // anywhere, and leaving it in a field is one screenshot away from
                // being somewhere.
                if (_passField != null) _passField.text = "";

                GameManager.Instance?.GoToCharacterSelect();
                return;
            }

            // Confirmation is not a failure -- the player did nothing wrong and the
            // next step is in their inbox.
            ShowHint(result.Message);
        }
        finally
        {
            _busy = false;
        }
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
