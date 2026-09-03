using System.Collections.Generic;
using IdleExplorers.Backend;
using IdleExplorers.Rules;
using TMPro;
using UnityEngine;

/// <summary>
/// Who gets the crown.
///
/// ══ WHY A GROUP BOSS NEEDS THIS ═══════════════════════════════════════════════
///
/// Because the King used to roll his whole table separately for every fighter. Four
/// people killed him, four Goblin Spears existed, and by the second clear the rarest
/// thing in the game was the thing everybody already had. A boss whose rewards
/// multiply by party size has no reason to be a group boss.
///
/// So the fight drops one of each thing, and the group decides between them. The
/// rules are the oldest and dullest ones there are -- need beats greed however high
/// the greed rolled, highest roll wins inside a band, a pass is not a roll -- because
/// everybody already knows them and a loot system that has to be explained is a loot
/// system that gets argued about.
///
/// ══ WHAT THIS WINDOW DOES NOT SHOW ════════════════════════════════════════════
///
/// Anybody else's answer. Seeing that two people have already pressed need would
/// change what the third presses, and a need/greed window whose outcome depends on
/// how long you waited before answering is a race rather than a roll. You see the
/// item, the clock, and what you yourself said.
///
/// ══ AND THE DICE ARE NOT ROLLED HERE ══════════════════════════════════════════
///
/// The client sends a WORD. The number comes back from the server, derived from the
/// fight's own seed -- so it can be re-derived from two columns months later rather
/// than taken on anybody's word, and so a client cannot roll itself a hundred.
/// </summary>
public class LootRollModal : UIScreen
{
    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    /// <summary>How often the open window re-reads. The same beat as everything else.</summary>
    private const float RefreshSeconds = 2f;

    private static readonly List<LootRollOffer> _offers = new();

    private Transform _list;
    private TMP_Text  _empty;
    private float     _nextRefreshAt;

    // ── Knowing there is something to roll for ────────────────────────────────

    /// <summary>
    /// Starts watching for contested drops.
    ///
    /// ══ WHY IT WATCHES THE BOSS RATHER THAN POLLING ═══════════════════════════
    ///
    /// A loot roll can only follow a boss dying, and a boss dying is an event this
    /// client already hears. Polling /rolls on a timer for the whole session would be
    /// a request every two seconds, for every player, to answer "no" -- for a window
    /// that opens a handful of times a week.
    ///
    /// So nothing is asked until the King falls. From then on the window polls itself
    /// while it is open, and stops when there is nothing left to decide.
    /// </summary>
    public static void Listen()
    {
        // ══ UNSUBSCRIBE FIRST, RATHER THAN GUARD WITH A FLAG ══════════════════
        //
        // A "have I already subscribed" bool is wrong in a Unity editor: GameEvents
        // nulls every handler in ResetStatics, and a static bool that survived that
        // would say yes to a question whose answer is now no -- so the window would
        // stop opening and nothing would say why.
        //
        // Removing a handler that is not there is free, so this is idempotent without
        // remembering anything.
        GameEvents.OnBossDefeated -= OnBossFell;
        GameEvents.OnBossDefeated += OnBossFell;
    }

    private static void OnBossFell(string monsterId) => Open();

    /// <summary>
    /// Asks what is on offer, and shows the window if anything is.
    ///
    /// Called after the King falls rather than immediately: resolve and the roll rows
    /// are written in the same transaction, but the client learns the boss died from
    /// its own predicted health, which can be a fraction of a second early.
    /// </summary>
    private static async void Open()
    {
        if (!ServerState.IsAuthoritative || string.IsNullOrEmpty(ServerState.CharacterId)) return;

        try
        {
            // One retry a beat later. The rows land in the resolve transaction and
            // this fires off a predicted death, so the first ask can genuinely be too
            // early -- and a window that never opens is a crown nobody can claim.
            if (!await RefreshAsync())
            {
                await Awaitable.WaitForSecondsAsync(1.5f);

                if (!await RefreshAsync()) return;
            }
        }
        catch (BackendException e)
        {
            Debug.LogWarning($"[Loot] Could not read the rolls: {e.Message}");
            return;
        }

        if (GameManager.UI != null && !GameManager.UI.IsOpen<LootRollModal>())
            GameManager.UI.Push<LootRollModal>();
    }

    /// <summary>Re-reads the open rolls. True when there is at least one.</summary>
    private static async Awaitable<bool> RefreshAsync()
    {
        LootRollList list = await GameBackend.Current.GetLootRollsAsync(ServerState.CharacterId);

        _offers.Clear();

        if (list?.rolls != null)
            foreach (LootRollOffer offer in list.rolls) if (offer != null) _offers.Add(offer);

        return _offers.Count > 0;
    }

    // ── The window ────────────────────────────────────────────────────────────

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var card = UIFactory.Panel(transform, "RollCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.26f, 0.18f, 0.74f, 0.84f);

        var title = UIFactory.Label(card.transform, "THE KING'S THINGS", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.88f, 0.95f, 0.97f);

        var rule = UIFactory.Label(card.transform,
                                    "Need beats greed. Highest roll wins. Passing is not a roll.",
                                    theme.fontSizeSmall, theme.textSecondary,
                                    TextAlignmentOptions.Center);
        UIFactory.At(rule, 0.05f, 0.82f, 0.95f, 0.88f);

        var stack = UIFactory.VStack(card.transform, theme.spacing, true, "Rolls");
        UIFactory.At(stack.transform, 0.05f, 0.14f, 0.95f, 0.80f);

        _list = stack.transform;

        _empty = UIFactory.Label(card.transform, "", theme.fontSizeBody,
                                  theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_empty, 0.05f, 0.44f, 0.95f, 0.56f);

        var close = UIFactory.Button(card.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.32f, 0.03f, 0.68f, 0.12f);

        Draw();

        _nextRefreshAt = Time.time + RefreshSeconds;
    }

    private void Update()
    {
        if (Time.time < _nextRefreshAt) return;

        _nextRefreshAt = Time.time + RefreshSeconds;

        _ = TickAsync();
    }

    private async Awaitable TickAsync()
    {
        if (!ServerState.IsAuthoritative) return;

        try
        {
            // ══ THE READ IS ALSO WHAT SETTLES AN EXPIRED ROLL ═════════════════
            //
            // Deliberately. There is no background service in this API, and adding one
            // to expire a loot roll would be a whole new thing to run and watch for a
            // job that has to happen roughly once a minute. So the server settles
            // anything out of time when somebody looks -- which, while this window is
            // open, is every two seconds.
            bool any = await RefreshAsync();

            Draw();

            // Everything decided. Closing rather than sitting on an empty card, so the
            // last press is the last thing the player has to do.
            if (!any) GameManager.UI?.Pop();
        }
        catch (BackendException e)
        {
            Debug.LogWarning($"[Loot] Roll refresh failed: {e.Message}");
        }
    }

    private void Draw()
    {
        if (_list == null) return;

        for (int i = _list.childCount - 1; i >= 0; i--) Destroy(_list.GetChild(i).gameObject);

        if (_empty != null) _empty.text = _offers.Count == 0 ? "Nothing left to settle." : "";

        foreach (LootRollOffer offer in _offers) Row(offer);
    }

    private void Row(LootRollOffer offer)
    {
        var theme = UIManager.Theme;

        var row = UIFactory.Panel(_list, "Roll_" + offer.rollId, theme.slotBg, false);

        var layout = row.AddComponent<UnityEngine.UI.LayoutElement>();
        layout.minHeight       = 68f;
        layout.preferredHeight = 68f;

        ItemData item = GameManager.Content?.GetItem(offer.itemId);

        string what = item != null ? item.DisplayName : offer.itemId;

        if (offer.quantity > 1L) what += $"  ×{offer.quantity}";

        var name = UIFactory.Label(row.transform, what, theme.fontSizeBody,
                                    theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(name, 0.03f, 0.52f, 0.62f, 0.95f);

        var clock = UIFactory.Label(row.transform, $"{Mathf.CeilToInt((float)offer.secondsLeft)}s",
                                     theme.fontSizeSmall, theme.textSecondary,
                                     TextAlignmentOptions.MidlineLeft);
        UIFactory.At(clock, 0.03f, 0.08f, 0.30f, 0.48f);

        // ══ ONCE ANSWERED, THE BUTTONS GO ═════════════════════════════════════
        //
        // Changing your mind after the fact is the one thing a need/greed window must
        // not allow, and the server refuses a second answer regardless -- so leaving
        // the buttons up would be offering something that cannot happen.
        if (offer.Answered)
        {
            var said = UIFactory.Label(row.transform,
                                        offer.myChoice == LootRoll.Pass
                                            ? "passed"
                                            : $"{offer.myChoice}  ·  rolled {offer.myRoll}",
                                        theme.fontSizeBody, theme.accentGreen,
                                        TextAlignmentOptions.MidlineRight);

            UIFactory.At(said, 0.60f, 0.20f, 0.97f, 0.80f);
            return;
        }

        Choice(row.transform, "NEED",  LootRoll.Need,  offer.rollId, 0.60f, 0.72f);
        Choice(row.transform, "GREED", LootRoll.Greed, offer.rollId, 0.735f, 0.855f);
        Choice(row.transform, "PASS",  LootRoll.Pass,  offer.rollId, 0.87f, 0.97f);
    }

    private void Choice(Transform parent, string label, string choice, string rollId,
                        float left, float right)
    {
        var button = UIFactory.Button(parent, label, () => _ = AnswerAsync(rollId, choice), width: 0f);

        UIFactory.At(button, left, 0.18f, right, 0.82f);
    }

    private async Awaitable AnswerAsync(string rollId, string choice)
    {
        try
        {
            LootRollAnswer answer =
                await GameBackend.Current.AnswerLootRollAsync(ServerState.CharacterId, rollId, choice);

            if (answer != null && answer.answered && choice != LootRoll.Pass)
                GameEvents.FireToast($"You rolled {answer.roll}.", ChatTone.Info);

            await TickAsync();
        }
        catch (BackendException e)
        {
            GameEvents.FireToast(e.Title, ChatTone.Bad);
            Debug.LogWarning($"[Loot] Answer failed: {e.Message}");
        }
    }
}
