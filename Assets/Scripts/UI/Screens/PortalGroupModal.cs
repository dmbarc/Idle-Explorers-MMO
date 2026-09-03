using IdleExplorers.Backend;
using TMPro;
using UnityEngine;

/// <summary>
/// The door to the Goblin King, and who is going through it.
///
/// ══ WHY THE PORTAL IS A MENU AND NOT A DOOR ═══════════════════════════════════
///
/// Because walking into it alone was the only option, and the fight is tuned for
/// four. A door that silently starts a solo attempt at a group encounter is a door
/// that teaches players the boss is unfair.
///
/// So it opens on the two questions that decide whether to go in: are the kills done,
/// and who is coming.
///
/// ══ WHY IT REFRESHES WHILE OPEN ═══════════════════════════════════════════════
///
/// Somebody else joining is an event that happens on the SERVER, and there is no
/// socket to hear it on — Unity WebGL has none, so the world is polled. A group panel
/// that only read the roster when it opened would show three people for as long as it
/// stayed open, while a fourth stood outside pressing join.
///
/// It re-reads on the same cadence as presence. Two seconds late is invisible;
/// never is not.
/// </summary>
public class PortalGroupModal : UIScreen
{
    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    /// <summary>The portal that opened this. Set immediately before Push.</summary>
    public static BossPortalController Target;

    private TMP_Text _hint;
    private TMP_Text _roster;
    private float    _nextRefreshAt;

    public static void Show(BossPortalController portal)
    {
        Target = portal;
        GameManager.UI?.Push<PortalGroupModal>();
    }

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<UnityEngine.UI.Button>();
        backdropBtn.transition = UnityEngine.UI.Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var card = UIFactory.Panel(transform, "PortalCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.28f, 0.20f, 0.72f, 0.82f);

        var title = UIFactory.Label(card.transform, "THE GOBLIN THRONE", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.86f, 0.95f, 0.96f);

        var gate = UIFactory.Label(card.transform, GateText(), theme.fontSizeSmall,
                                    theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(gate, 0.05f, 0.78f, 0.95f, 0.85f);

        var heading = UIFactory.Label(card.transform,
                                       $"YOUR GROUP  (max {IdleExplorers.Rules.Party.MaxMembers})",
                                       theme.fontSizeSmall, theme.accentPurple,
                                       TextAlignmentOptions.MidlineLeft);
        UIFactory.At(heading, 0.08f, 0.68f, 0.92f, 0.75f);

        _roster = UIFactory.Label(card.transform, "Reading…", theme.fontSizeBody,
                                   theme.textPrimary, TextAlignmentOptions.TopLeft);
        UIFactory.At(_roster, 0.08f, 0.40f, 0.92f, 0.68f);

        _hint = UIFactory.Label(card.transform, "", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.05f, 0.33f, 0.95f, 0.40f);

        var form = UIFactory.Button(card.transform, "OPEN A GROUP", () => _ = FormAsync(), width: 0f);
        UIFactory.At(form, 0.08f, 0.23f, 0.49f, 0.32f);

        var leave = UIFactory.Button(card.transform, "LEAVE GROUP", () => _ = LeaveAsync(), width: 0f);
        UIFactory.At(leave, 0.51f, 0.23f, 0.92f, 0.32f);

        // ══ STARTING SHORT IS ALLOWED ═════════════════════════════════════════
        //
        // Deliberately. Waiting for a fourth who never arrives is how a playtest with
        // five people in it never sees the boss at all.
        var enter = UIFactory.Button(card.transform, "ENTER THE THRONE", () => _ = EnterAsync(), width: 0f);
        UIFactory.At(enter, 0.08f, 0.11f, 0.92f, 0.21f);

        var close = UIFactory.Button(card.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.32f, 0.02f, 0.68f, 0.10f);

        _ = RefreshAsync();
    }

    private void Update()
    {
        if (Time.time < _nextRefreshAt) return;

        _nextRefreshAt = Time.time + (float)IdleExplorers.Rules.Presence.ReportSeconds;

        _ = RefreshAsync();
    }

    private string GateText()
    {
        long kills = 0L;

        var counts = CharacterManager.Current?.kills;

        if (counts != null)
            foreach (var row in counts)
                if (row != null && row.monsterId == IdleExplorers.Rules.BossGate.Monster)
                    kills = row.activeKills;

        long need = IdleExplorers.Rules.BossGate.RequiredActiveKills;

        return kills >= need
            ? "The way is open."
            : $"Goblins slain: {NumberFormatter.Format(kills)} / {NumberFormatter.Format(need)}";
    }

    private async Awaitable RefreshAsync()
    {
        if (!ServerState.IsAuthoritative)
        {
            Draw(null);
            return;
        }

        Draw(await ServerState.GetPartyAsync());
    }

    private void Draw(PartySnapshot party)
    {
        if (_roster == null) return;

        if (party == null || !party.Exists)
        {
            _roster.text = "You are on your own.\n\nOpen a group and anybody standing\nnearby can click you to join.";
            return;
        }

        var lines = new System.Text.StringBuilder();

        foreach (PartyMember member in party.members)
        {
            if (member == null) continue;

            bool leader = member.characterId == party.leaderCharacterId;

            lines.AppendLine($"{(leader ? "★" : "•")}  {member.name}   Lv. {member.level}");
        }

        for (int i = party.Count; i < party.maxMembers; i++) lines.AppendLine("·  (open)");

        _roster.text = lines.ToString();
    }

    private async Awaitable FormAsync()
    {
        Say("Opening…");

        PartySnapshot party = await ServerState.FormPartyAsync();

        Draw(party);
        Say(party != null ? "Open. Others can click you to join." : "");
    }

    private async Awaitable LeaveAsync()
    {
        Say("Leaving…");

        Draw(await ServerState.LeavePartyAsync());
        Say("");
    }

    private async Awaitable EnterAsync()
    {
        if (Target == null)
        {
            Say("The portal is not here.");
            return;
        }

        Say("Opening the way…");

        // ══ A GROUP GOES IN TOGETHER ══════════════════════════════════════════
        //
        // Pressing this used to take exactly one person through, and the other three
        // stood in the camp watching them vanish -- so four people fighting a
        // four-person boss had to count down out loud in chat and all click the same
        // stone within a second of each other.
        //
        // Now it raises a CALL: everybody in the group sees the same countdown and can
        // step out of it. See ThroneCallModal for why it is an offer rather than a
        // teleport.
        //
        // The gate is still checked per person, at engage, by the server. Being called
        // through a door is not the same as being allowed through it, and somebody
        // who followed the group in without their own thousand kills is refused there
        // -- which is the right place for it, because it is the only place that
        // cannot be got round.
        PartySnapshot party = await ServerState.GetPartyAsync();

        if (party != null && party.Count > 1)
        {
            // Asked first, and its answer is what decides. A call that pulled three
            // people into an arena the caller could not enter would be a group
            // teleport with no boss at the end of it.
            bool open = await Target.TryOpenAsync(GameBackend.Current, ServerState.CharacterId);

            if (!open) { Say(""); return; }

            await ServerState.CallPartyAsync(Target.destinationMapId, Target.gateMonsterId);

            Say("The group has been called.");

            // Left open deliberately: the countdown appears over the top of it, and
            // popping this as well would take two screens away at once for a player
            // who has not decided anything yet.
            return;
        }

        // The gate is checked by the SERVER inside this call. The count drawn above is
        // a mirror and could be behind; the refusal that matters comes from there.
        bool entered = await Target.TryEnterAsync(GameBackend.Current, ServerState.CharacterId);

        if (entered) GameManager.UI?.Pop();
        else         Say("");
    }

    private void Say(string message)
    {
        if (_hint != null) _hint.text = message;
    }
}
