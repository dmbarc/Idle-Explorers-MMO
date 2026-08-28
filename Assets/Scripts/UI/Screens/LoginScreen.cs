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

        // ══ ONLY WHEN THERE IS SOMEWHERE TO SIGN IN TO ════════════════════════
        //
        // Offline there is no provider, and a Google button that explains it cannot
        // work is worse than no button -- it reads as broken rather than as absent.
        if (!IdleExplorers.Backend.GoogleSignIn.IsAvailable) return;

        var googleBtn = UIFactory.Button(transform, "SIGN IN WITH GOOGLE", AttemptGoogle, width: 0f);
        UIFactory.At(googleBtn, 0.35f, 0.10f, 0.65f, 0.17f);

        var or = UIFactory.Label(transform, "— or —", theme.fontSizeSmall,
                                  theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(or, 0.35f, 0.165f, 0.65f, 0.19f);
    }

    /// <summary>Remembers the last email signed in with. Not the password.</summary>
    private const string LastEmailKey = "idle.auth.email";

    public override void OnShow()
    {
        ClearHint();

        // Asked every time this screen appears, because the answer may already be yes
        // before it ever did. See the note on Session.SignedInChanged.
        if (Connected) _ = ResumeIfAlreadySignedInAsync();

        if (_nameField == null) return;

        if (Connected)
        {
            // ══ THE LOCAL ACCOUNT NAME IS NOT AN EMAIL ════════════════════════
            //
            // This used to pre-fill from AccountManager, which in offline mode holds a
            // name like "Adventurer". Connected, that put a non-email in the email
            // field -- so the email check passed, the password check failed, and the
            // player was told "Enter your password" while looking at a form that
            // appeared to be filled in. The one message that could not explain itself.
            //
            // The last EMAIL is worth remembering; the local account name never was.
            _nameField.text = PlayerPrefs.GetString(LastEmailKey, "");

            if (_passField != null) _passField.text = "";
            return;
        }

        // Offline, the field really is an account name. Pre-filled only when a save
        // exists: AccountManager fabricates a stub named "Adventurer" when there is
        // none, and offering that to a first-time player is how an account ends up
        // named after the placeholder.
        bool hasSave  = GameManager.Save?.HasSave ?? false;
        var  existing = AccountManager.Current;

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
    /// Hands the whole thing to the browser.
    ///
    /// No email field, no password field, and nothing typed into this game -- which is
    /// the point of it. On desktop the browser comes back to a loopback port this
    /// process listens on for a few minutes; on the web the page itself goes to Google
    /// and a later page load finishes the job.
    /// </summary>
    private async void AttemptGoogle()
    {
        if (_busy) return;

        _busy = true;
        ShowHint("Opening your browser…");

        try
        {
            var result = await IdleExplorers.Backend.Session.SignInWithGoogleAsync();

            if (result.Ok)
            {
                await LoadAccountAsync();
                return;
            }

            // An empty message is the web platform on its way out -- the page is
            // navigating and there is nothing to report. Anything else is worth saying.
            if (!string.IsNullOrEmpty(result.Message)) ShowHint(result.Message);
        }
        finally
        {
            _busy = false;
        }
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
                // The email, so a returning player presses one field fewer. Saved only
                // on SUCCESS -- remembering an address that failed to sign in would
                // helpfully re-offer the typo every time.
                PlayerPrefs.SetString(LastEmailKey, email);
                PlayerPrefs.Save();

                // ══ THE PASSWORD STAYS IN THE FIELD ═══════════════════════════
                //
                // It used to be wiped here, on the reasoning that a password sitting
                // in a text field is one screenshot from being somewhere it should not
                // be. True, but it was the wrong trade, and the failure showed exactly
                // why: signing in SUCCEEDED and then the game server did not answer,
                // which leaves the player on this screen -- with the retry button in
                // front of them and an empty password box. The password was correct.
                // Emptying it charged them for the server's problem.
                //
                // So it is emptied only when it was actually wrong, below. On the happy
                // path this screen is torn down a moment later and the field goes with
                // it; the value never leaves the field, is never logged and is never
                // written to disk, and the box is masked throughout.
                await LoadAccountAsync();
                return;
            }

            // Wrong email or password: emptying the box is helpful here, because the
            // next attempt needs a different value in it and selecting the old one
            // first is a step nobody wants.
            if (result.CredentialsRejected && _passField != null) _passField.text = "";

            // Confirmation is not a failure -- the player did nothing wrong and the
            // next step is in their inbox.
            ShowHint(result.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Goes straight through when the player is already signed in.
    ///
    /// ══ WHY THIS SCREEN HAS TO ASK ══════════════════════════════════════
    ///
    /// Because nothing tells it. Session raises SignedInChanged when a Google redirect
    /// completes or a stored session is restored -- but both happen in Begin(), at
    /// BeforeSceneLoad, when no scene exists and nothing is subscribed. The event goes
    /// nowhere.
    ///
    /// So a player came back from Google correctly signed in, with a token and an
    /// account, and sat looking at a login form. The same was true of anyone
    /// reopening the tab with a valid stored session: signed in, and asked to sign in.
    ///
    /// Asking on show fixes both, and cannot be defeated by ordering the way a
    /// subscription can -- a state is true whenever you look at it, a moment is not.
    ///
    /// ══ WHY IT WAITS ════════════════════════════════════════════════════
    ///
    /// The token exchange is a network round trip started before the splash. It has
    /// usually finished by the time content has loaded, but "usually" decides whether
    /// a player signs in twice, so the screen waits for the answer rather than racing
    /// it.
    /// </summary>
    private async Awaitable ResumeIfAlreadySignedInAsync()
    {
        if (!IdleExplorers.Backend.Session.Ready)
        {
            ShowHint("Restoring your session…");
            await IdleExplorers.Backend.Session.UntilReadyAsync();
        }

        // Signed out, or never signed in. The form is the right answer.
        if (!IdleExplorers.Backend.Session.IsSignedIn)
        {
            // A sign-in that was attempted and FAILED says so, rather than dropping the
            // player back here with no explanation for the trip they just took.
            string problem = IdleExplorers.Backend.Session.ResumeProblem;

            if (!string.IsNullOrEmpty(problem)) ShowHint(problem);
            else                                ClearHint();

            return;
        }

        await LoadAccountAsync();
    }

    /// <summary>
    /// Loads the account before showing the roster.
    ///
    /// ══ WHY THE SCREEN WAITS ══════════════════════════════════════════════════
    ///
    /// Character select reads AccountManager.Current.characters. Going there before
    /// the account has been pulled shows an EMPTY roster to a player who has
    /// characters, and the obvious reaction is to create another one -- which is a
    /// duplicate the server may or may not accept, off the back of a screen that lied.
    ///
    /// A second of "Loading your account…" is a far better trade than a roster that
    /// fills in after somebody has already acted on it.
    ///
    /// Offline the pull is a no-op and this is one frame.
    /// </summary>
    private async Awaitable LoadAccountAsync()
    {
        if (!IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            GameManager.Instance?.GoToCharacterSelect();
            return;
        }

        ShowHint("Loading your account…");

        bool loaded = await IdleExplorers.Backend.ServerState.PullAccountAsync();

        if (!loaded)
        {
            // Signed in but unable to reach the game server. Deliberately NOT sent
            // through to a roster the client would then invent locally -- that is how
            // a player ends up with characters the server has never heard of.
            ShowHint("Signed in, but the game server did not answer. Try again shortly.");
            return;
        }

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
