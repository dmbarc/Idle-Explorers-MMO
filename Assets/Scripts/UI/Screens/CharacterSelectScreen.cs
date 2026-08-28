using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 3: Character Select.
///
/// Card contents use a VerticalLayoutGroup rather than free-floating labels —
/// unanchored children default to a full stretch, which is why the locked-slot
/// requirements previously drew on top of each other.
/// </summary>
public class CharacterSelectScreen : UIScreen
{
    private const int TotalSlots = 4;

    /// <summary>Header and cards both render account and character state.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        UIFactory.Panel(transform, "Bg", UIManager.Theme.panelBg, true);
        BuildHeader();
        BuildCharacterGrid();
    }

    // No OnShow refresh: RebuildOnShow already reran Build a moment earlier.
    // OnResume still needs it, for returning from a non-overlay screen.
    public override void OnResume() => Refresh();

    public override void OnShow()
    {
        // An overlay (the rename modal) never triggers OnHide here, so this stays
        // subscribed while the modal is open — which is exactly what lets a rename
        // update the card immediately. OnResume alone cannot do it: Pop() skips it
        // for overlays on purpose, to avoid double-subscribing the screen below.
        GameEvents.OnCharacterRosterChanged += Refresh;
    }

    public override void OnHide()
    {
        GameEvents.OnCharacterRosterChanged -= Refresh;
    }

    private void Refresh()
    {
        var grid = transform.Find("CharGrid");
        if (grid != null) DestroyImmediate(grid.gameObject);
        BuildCharacterGrid();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader()
    {
        var theme  = UIManager.Theme;
        var header = UIFactory.Panel(transform, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.88f, 1f, 1f);

        var account = AccountManager.Current;
        string acctName  = account?.accountName ?? "Explorer";
        int    acctLevel = account?.accountLevel ?? 1;

        var nameLabel = UIFactory.Label(header.transform, $"{acctName}   •   Account Lv. {acctLevel}",
                                         theme.fontSizeBody, theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(nameLabel, 0.02f, 0.40f, 0.60f, 0.92f);

        var (barRoot, barFill) = UIFactory.ProgressBar(header.transform, "AcctXPBar", theme.xpFill, 400f, 10f);
        UIFactory.At(barRoot.transform, 0.02f, 0.12f, 0.40f, 0.32f);
        // Progress between this level's XP floor and the next one's. The old
        // `accountXP % 100` assumed a linear 100-per-level curve, but the curve is
        // quadratic, so the bar drifted further from the truth at every level.
        barFill.fillAmount = AccountManager.LevelProgress(account);

        // Logging out has to go through here so the account is written to disk
        var logoutBtn = UIFactory.Button(header.transform, "LOG OUT", () =>
        {
            GameManager.Save?.SaveActiveState();
            GameManager.Instance?.TransitionTo(GameManager.GameState.Login);
        }, width: 0f);
        UIFactory.At(logoutBtn, 0.87f, 0.20f, 0.98f, 0.80f);
    }

    // ── Grid ──────────────────────────────────────────────────────────────────

    private void BuildCharacterGrid()
    {
        var theme   = UIManager.Theme;
        var account = AccountManager.Current;

        var gridGo = new GameObject("CharGrid", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        gridGo.transform.SetParent(transform, false);
        UIFactory.At(gridGo.transform, 0.04f, 0.06f, 0.96f, 0.86f);

        var hlg = gridGo.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing                = theme.spacing * 2;
        hlg.padding                = new RectOffset(8, 8, 8, 8);
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;

        for (int i = 0; i < TotalSlots; i++)
        {
            bool unlocked = GameManager.Account?.IsSlotUnlocked(i) ?? i < 2;
            CharacterData data = (account?.characters != null && i < account.characters.Count)
                                 ? account.characters[i] : null;
            BuildCharacterCard(gridGo.transform, i, data, unlocked);
        }
    }

    /// <summary>A card whose children stack vertically and centre themselves.</summary>
    private VerticalLayoutGroup BuildCardBody(Transform parent, int slotIndex, bool unlocked)
    {
        var theme = UIManager.Theme;

        var card = UIFactory.Panel(parent, $"Card_{slotIndex}",
                                    unlocked ? theme.cardBg : theme.headerBg, false);

        var vlg = UIFactory.VStack(card.transform, theme.spacing, true, "CardBody");
        UIFactory.At(vlg.transform, 0.06f, 0.06f, 0.94f, 0.94f);
        vlg.childAlignment = TextAnchor.MiddleCenter;
        vlg.padding        = new RectOffset(8, 8, 8, 8);

        return vlg;
    }

    private void BuildCharacterCard(Transform parent, int slotIndex, CharacterData data, bool unlocked)
    {
        var body = BuildCardBody(parent, slotIndex, unlocked);

        if (!unlocked)      BuildLockedCard(body.transform, slotIndex);
        else if (data == null) BuildEmptyCard(body.transform);
        else                BuildFilledCard(body.transform, data);
    }

    private void BuildLockedCard(Transform body, int slotIndex)
    {
        var theme = UIManager.Theme;

        Row(UIFactory.Label(body, "LOCKED", theme.fontSizeBody, theme.textDisabled, TextAlignmentOptions.Center), 30f);
        Row(UIFactory.HorizontalDivider(body).transform, 2f);
        Row(UIFactory.Label(body, "Unlocks at", theme.fontSizeLabel, theme.textSecondary, TextAlignmentOptions.Center), 20f);
        Row(UIFactory.Label(body, GetUnlockText(slotIndex), theme.fontSizeSmall, theme.accentGold, TextAlignmentOptions.Center), 52f);
    }

    private void BuildEmptyCard(Transform body)
    {
        var theme = UIManager.Theme;

        Row(UIFactory.Label(body, "Empty Slot", theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center), 24f);
        Row(UIFactory.Button(body, "CREATE CHARACTER",
                              () => GameManager.Instance?.GoToCharacterCreate(), width: 0f), theme.buttonHeight);
    }

    private void BuildFilledCard(Transform body, CharacterData data)
    {
        var theme = UIManager.Theme;

        string className = ClassManager.TitleFor(data);

        Row(UIFactory.Label(body, data.characterName, theme.fontSizeBody, theme.textPrimary, TextAlignmentOptions.Center), 30f);
        Row(UIFactory.Label(body, $"Lv. {data.level}  •  {className}", theme.fontSizeSmall,
                             theme.accentGold, TextAlignmentOptions.Center), 24f);

        Row(UIFactory.HorizontalDivider(body).transform, 2f);

        // What this character has been doing while logged out — the reason to have
        // more than one of them.
        var activity = data.currentActivity;
        string activityText = (activity != null && !string.IsNullOrEmpty(activity.skillId))
            ? $"{GameManager.Content?.GetSkill(activity.skillId)?.DisplayName ?? activity.skillId}\n{activity.activityTargetName}"
            : "Idle";
        Row(UIFactory.Label(body, activityText, theme.fontSizeLabel, theme.textSecondary, TextAlignmentOptions.Center), 36f);

        // lastLogoutUnixTime is 0 for a character that has never been played, and
        // subtracting from that reports roughly fifty years of idling.
        string afkLabel;
        if (data.isOnline)                    afkLabel = "⚡ ONLINE";
        else if (data.lastLogoutUnixTime <= 0) afkLabel = "Never played";
        else afkLabel = NumberFormatter.FormatAFKTime(
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - data.lastLogoutUnixTime);
        Row(UIFactory.Label(body, afkLabel, theme.fontSizeSmall,
                             data.isOnline ? theme.accentGreen : theme.textSecondary, TextAlignmentOptions.Center), 24f);

        Row(UIFactory.Button(body, "▶  PLAY", () => PlayCharacter(data), width: 0f), theme.buttonHeight);
        Row(UIFactory.Button(body, "RENAME", () =>
        {
            RenameCharacterModal.Target = data;
            GameManager.UI?.Push<RenameCharacterModal>();
        }, width: 0f), theme.buttonHeight * 0.8f);
    }

    private async void PlayCharacter(CharacterData data)
    {
        // SelectCharacter runs AFK accrual internally — calling ProcessAFKRewards
        // here as well would grant every reward twice.
        //
        // AWAITED, because under an authoritative server the accrual is a round trip.
        // Reading PendingSummary immediately after a fire-and-forget call read it
        // before the request had even been sent, so the summary was always empty and
        // the screen never appeared.
        if (GameManager.Character != null)
            await GameManager.Character.SelectCharacterAsync(data);

        var summary = GameManager.Activity?.PendingSummary;
        if (summary != null && summary.HasAnything)
            GameManager.UI?.Push<AFKSummaryScreen>();   // COLLECT continues into the game
        else
            GameManager.Instance?.GoToGame();
    }

    /// <summary>Gives a layout-group child an explicit height so rows cannot collapse onto each other.</summary>
    private static void Row(Component element, float height)
    {
        var le = element.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = height;
    }

    private string GetUnlockText(int slotIndex)
    {
        var unlocks = GameManager.Content?.SlotUnlocks;
        if (unlocks == null) return "???";

        foreach (var req in unlocks)
        {
            if (req.slot != slotIndex) continue;

            string s = $"Account Lv. {req.reqAccountLevel}";
            if (req.reqAnyCharLevel > 0) s += $"\nAny character Lv. {req.reqAnyCharLevel}";
            return s;
        }
        return "???";
    }
}
