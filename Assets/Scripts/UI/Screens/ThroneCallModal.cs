using IdleExplorers.Backend;
using TMPro;
using UnityEngine;

/// <summary>
/// "We are going in." A countdown the whole group can see, and step out of.
///
/// ══ WHY A GROUP NEEDS THIS AT ALL ═════════════════════════════════════════════
///
/// Because the throne took one person. Whoever pressed the portal went through it
/// alone, and the other three stood in a field watching their tank vanish -- so the
/// only way for four people to fight a four-person boss was to count down out loud in
/// chat and all press the same stone at the same moment.
///
/// ══ WHY NOT JUST MOVE EVERYBODY ═══════════════════════════════════════════════
///
/// Because being teleported without warning is worse than being left behind. Any
/// member of the group could otherwise yank the other three out of whatever they were
/// doing -- mid-craft, mid-sentence, mid-anything -- and there is no undo for having
/// been moved.
///
/// So the call is an OFFER with a clock on it. Ten seconds, everybody sees the same
/// ten, and anybody can decline. Declining is a local decision and is never reported:
/// a member who says no does not cancel the trip for the rest of the group, they
/// simply do not come.
///
/// ══ WHY THE COUNTDOWN IS NOT COUNTED HERE ═════════════════════════════════════
///
/// The server sends how many seconds are LEFT, computed where the row lives. Counting
/// from an absolute timestamp against the browser's own clock would put four people
/// out of step by however wrong somebody's laptop is, and four people arriving over
/// four seconds is the failure this whole mechanism exists to prevent.
///
/// Between polls it ticks down locally so the number moves every frame, and every
/// poll re-anchors it. Same shape as the boss health bar, for the same reason.
/// </summary>
public class ThroneCallModal : UIScreen
{
    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    /// <summary>
    /// Calls this client has finished with, so it is not asked about them again.
    ///
    /// Static and deliberately not cleared on close: the row stays live on the server
    /// for well past the countdown, so without this a player who declined would be
    /// asked again two seconds later, for ever -- and a player who ACCEPTED and then
    /// walked back out of the arena would be dragged straight back through the door.
    /// </summary>
    private static string _handledToken = "";

    /// <summary>The call being counted. Replaced on every poll.</summary>
    private static PartyCall _call;

    private TMP_Text _line;
    private TMP_Text _clock;

    private float _secondsLeft;

    // ── Deciding when to appear ───────────────────────────────────────────────

    /// <summary>
    /// Watches the party for a call, and raises the modal when one arrives.
    ///
    /// A plain static subscriber rather than a MonoBehaviour, because there is nowhere
    /// sensible for a component to live: the modal itself does not exist until there
    /// is something to show, and the alternative is a permanent hidden object whose
    /// only job is waiting.
    /// </summary>
    public static void Listen()
    {
        GameEvents.OnPartyChanged -= OnParty;
        GameEvents.OnPartyChanged += OnParty;
    }

    private static void OnParty(PartySnapshot party)
    {
        if (party == null || !party.HasCall) { _call = null; return; }

        PartyCall call = party.call;

        if (call.callToken == _handledToken) return;

        _call = call;

        // ══ THE TRAVEL HAPPENS WHETHER OR NOT ANYBODY IS LOOKING ══════════════
        //
        // A player whose poll arrived late -- a suspended tab, a slow request, a modal
        // opened over the top -- would otherwise miss the whole countdown and stay
        // behind. So the decision to travel is made from the CALL, not from the
        // dialog: if the count has finished and this client never declined, it goes.
        if (call.travelNow)
        {
            Travel(call);
            return;
        }

        if (GameManager.UI != null && !GameManager.UI.IsOpen<ThroneCallModal>())
            GameManager.UI.Push<ThroneCallModal>();
    }

    private static void Travel(PartyCall call)
    {
        // Marked BEFORE travelling. EnterMap can take a scene load, and a poll that
        // lands in the middle of it would otherwise see an unhandled call and travel
        // again.
        _handledToken = call.callToken;
        _call         = null;

        if (GameManager.UI != null && GameManager.UI.IsOpen<ThroneCallModal>())
            GameManager.UI.Pop();

        if (string.IsNullOrEmpty(call.mapId)) return;

        // Already here. The person who raised the call is usually the person who
        // walked through the door, and sending them through it a second time would
        // reload the arena underneath a fight they had just started.
        if (GameManager.Zone?.CurrentMap?.id == call.mapId) return;

        GameEvents.FireToast("The group goes in.", ChatTone.Good);
        GameManager.Zone?.EnterMap(call.mapId);
    }

    // ── The dialog ────────────────────────────────────────────────────────────

    public override void Build()
    {
        var theme = UIManager.Theme;

        // No backdrop button. Every other overlay closes when the dark is clicked, and
        // here that would be an accidental "no" to something with a clock on it.
        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var card = UIFactory.Panel(transform, "CallCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.30f, 0.34f, 0.70f, 0.66f);

        var title = UIFactory.Label(card.transform, "THE GROUP IS GOING IN", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.78f, 0.95f, 0.94f);

        _line = UIFactory.Label(card.transform, Where(), theme.fontSizeBody,
                                 theme.textPrimary, TextAlignmentOptions.Center);
        UIFactory.At(_line, 0.05f, 0.62f, 0.95f, 0.76f);

        _clock = UIFactory.Label(card.transform, "", theme.fontSizeTitle,
                                  theme.accentGreen, TextAlignmentOptions.Center);
        UIFactory.At(_clock, 0.05f, 0.36f, 0.95f, 0.60f);

        var go = UIFactory.Button(card.transform, "GO NOW", GoNow, width: 0f);
        UIFactory.At(go, 0.08f, 0.16f, 0.49f, 0.32f);

        var stay = UIFactory.Button(card.transform, "STAY HERE", Decline, width: 0f);
        UIFactory.At(stay, 0.51f, 0.16f, 0.92f, 0.32f);

        var note = UIFactory.Label(card.transform,
                                    "Staying does not stop the others going.",
                                    theme.fontSizeSmall, theme.textSecondary,
                                    TextAlignmentOptions.Center);
        UIFactory.At(note, 0.05f, 0.04f, 0.95f, 0.14f);

        _secondsLeft = _call != null ? (float)_call.secondsLeft : 0f;
    }

    private static string Where()
    {
        string mapId = _call?.mapId ?? "";

        MapData map = GameManager.Content?.GetMap(mapId);

        return map != null ? map.name : "Somewhere else";
    }

    private void Update()
    {
        // The call went away -- everybody arrived, or it went stale. Nothing left to
        // count.
        if (_call == null) { GameManager.UI?.Pop(); return; }

        // Re-anchored on every poll and ticked locally in between, so the number moves
        // every frame rather than in two-second steps.
        _secondsLeft = Mathf.Min(_secondsLeft, (float)_call.secondsLeft);
        _secondsLeft = Mathf.Max(0f, _secondsLeft - Time.deltaTime);

        if (_clock != null) _clock.text = Mathf.CeilToInt(_secondsLeft).ToString();

        if (_call.travelNow || _secondsLeft <= 0f) Travel(_call);
    }

    private void GoNow()
    {
        if (_call != null) Travel(_call);
        else               GameManager.UI?.Pop();
    }

    /// <summary>
    /// Not coming.
    ///
    /// Recorded on this client and nowhere else. Telling the server would let one
    /// member cancel a trip the other three had agreed to, and "I am not coming" is
    /// not the same sentence as "nobody is going".
    /// </summary>
    private void Decline()
    {
        if (_call != null) _handledToken = _call.callToken;

        _call = null;

        GameEvents.FireToast("You stay behind.", ChatTone.Info);
        GameManager.UI?.Pop();
    }
}
