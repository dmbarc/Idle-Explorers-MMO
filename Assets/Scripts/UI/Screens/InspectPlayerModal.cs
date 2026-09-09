using IdleExplorers.Backend;
using TMPro;
using UnityEngine;

/// <summary>
/// Who is that, and can I group with them.
///
/// ══ WHAT IT SHOWS AND WHY THAT IS ALL ═════════════════════════════════════════
///
/// Name, level and class — the three things that answer "who is this" without
/// publishing anything the person did not choose to stand in public wearing. Not
/// their account, not their email, not what they are carrying.
///
/// Every one of them comes from the SERVER's presence answer, which reads them from
/// the character table. None of it is taken from the other player's client, so
/// nobody can inspect their way into believing somebody is level 500.
///
/// ══ WHY GROUPING LIVES HERE ═══════════════════════════════════════════════════
///
/// Because this is where you already are when you decide you want to. Walking to a
/// portal to invite the person standing next to you is a menu built around the
/// server's convenience rather than the player's.
/// </summary>
public class InspectPlayerModal : UIScreen
{
    /// <summary>Sits over the game rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Contents are read from whoever was clicked.</summary>
    public override bool RebuildOnShow => true;

    /// <summary>Set immediately before Push. See RenameCharacterModal for the pattern.</summary>
    public static RemotePlayerView Target;

    private TMP_Text _hint;

    /// <summary>Opens the panel on somebody. Does nothing when handed nobody.</summary>
    public static void Show(RemotePlayerView person)
    {
        if (person == null) return;

        Target = person;
        GameManager.UI?.Push<InspectPlayerModal>();
    }

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Dim", new Color(0f, 0f, 0f, 0.55f), true);

        var panel = UIFactory.Panel(transform, "Card", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.32f, 0.30f, 0.68f, 0.70f);

        if (Target == null)
        {
            // They walked away, or logged out, between the click and the draw.
            var gone = UIFactory.Label(panel.transform, "They are no longer here.",
                                        theme.fontSizeBody, theme.textSecondary,
                                        TextAlignmentOptions.Center);
            UIFactory.At(gone, 0.05f, 0.45f, 0.95f, 0.60f);

            var shut = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
            UIFactory.At(shut, 0.30f, 0.10f, 0.70f, 0.24f);
            return;
        }

        var name = UIFactory.Label(panel.transform, Target.Name, theme.fontSizeTitle,
                                    theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(name, 0.05f, 0.76f, 0.95f, 0.92f);

        string className = GameManager.Content?.GetClass(Target.ClassId)?.DisplayName ?? Target.ClassId;

        var detail = UIFactory.Label(panel.transform,
                                      $"Level {Target.Level}   ·   {className}",
                                      theme.fontSizeBody, theme.textPrimary,
                                      TextAlignmentOptions.Center);
        UIFactory.At(detail, 0.05f, 0.63f, 0.95f, 0.74f);

        _hint = UIFactory.Label(panel.transform, "", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.05f, 0.50f, 0.95f, 0.60f);

        var invite = UIFactory.Button(panel.transform, "JOIN THEIR GROUP",
                                       () => _ = JoinAsync(), width: 0f);
        UIFactory.At(invite, 0.10f, 0.34f, 0.90f, 0.46f);

        // ══ WHAT IS NOT HERE YET ══════════════════════════════════════════════
        //
        // TODO(social): a friends list. The seam is this panel and a
        // /friend/{characterId} pair on the server; nothing else has to move.
        var friends = UIFactory.Label(panel.transform, "Friends list coming soon",
                                       theme.fontSizeSmall, theme.textSecondary,
                                       TextAlignmentOptions.Center);
        UIFactory.At(friends, 0.05f, 0.26f, 0.95f, 0.33f);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.30f, 0.08f, 0.70f, 0.20f);
    }

    /// <summary>
    /// Joins the group this player leads, forming one for them if they have none.
    ///
    /// ══ WHY IT ASKS THEM TO FORM ONE ══════════════════════════════════════════
    ///
    /// A join needs something to join. Somebody standing alone is not in a group, so
    /// the first person to walk up and press this would otherwise be told "that
    /// player is not in a group" — which is true, unhelpful, and exactly the moment
    /// they wanted a group to exist.
    ///
    /// The server cannot form one on their behalf: a party whose leader never agreed
    /// to lead is an invitation nobody sent. So this reports the refusal plainly and
    /// tells them what to ask for.
    /// </summary>
    private async Awaitable JoinAsync()
    {
        if (Target == null || !ServerState.IsAuthoritative) return;

        Say("Joining…");

        PartySnapshot party = await ServerState.JoinPartyAsync(Target.CharacterId);

        if (party == null)
        {
            Say("They have not started a group yet — ask them to open one.");
            return;
        }

        Say($"In a group of {party.Count} of {party.maxMembers}.");
    }

    private void Say(string message)
    {
        if (_hint != null) _hint.text = message;
    }
}
