using IdleExplorers.Backend;
using UnityEngine;

/// <summary>
/// The door to the Goblin King.
///
/// ══ WHAT IT SHOWS AND WHAT IT DECIDES ═════════════════════════════════════════
///
/// It shows a number from the client's own mirror — "Goblin Kills — 412 / 1000" —
/// because a portal that has to make a network call before it can render a label is
/// a portal that flickers.
///
/// It decides nothing. Walking through asks the server, and the server's answer wins
/// even when it disagrees with the label. The two disagreeing is not a bug to be
/// designed away: the mirror is a cache, and a cache is allowed to be a moment stale.
/// What matters is that the stale one is never the one that opens the door.
///
/// ══ WHY THE LABEL IS HONEST ABOUT AFK KILLS ═══════════════════════════════════
///
/// A player who has farmed nine thousand goblins in their sleep and finds a sealed
/// portal will assume it is broken. So when there are AFK kills, the label says so
/// rather than showing a bare 412 that looks like the game lost count.
/// </summary>
public class BossPortalController : MonoBehaviour
{
    [Tooltip("Which monster's ACTIVE kills open this. From monster_data.json.")]
    public string gateMonsterId = BossGate.Monster;

    [Tooltip("How many active kills are needed.")]
    public long requiredKills = BossGate.RequiredActiveKills;

    [Tooltip("Map this portal travels to. Empty until the arena is built.")]
    public string destinationMapId = "goblin_throne";

    [Tooltip("How close the player must be to use it.")]
    public float interactionRange = 3f;

    /// <summary>The label above the portal. Rebuilt whenever the count moves.</summary>
    private TMPro.TMP_Text _label;

    /// <summary>Set once the server has confirmed it. Null means "not asked yet".</summary>
    private bool? _serverSaysOpen;

    private void Start()
    {
        _label = GetComponentInChildren<TMPro.TMP_Text>();
        Refresh();
    }

    private void OnEnable()  => GameEvents.OnKillCountChanged += OnKillsChanged;
    private void OnDisable() => GameEvents.OnKillCountChanged -= OnKillsChanged;

    private void OnKillsChanged(string monsterId, long active)
    {
        if (monsterId != gateMonsterId) return;

        Refresh();
    }

    /// <summary>
    /// Whether the portal LOOKS open. Presentation only — see the class comment.
    /// </summary>
    public bool LooksOpen => KillTracker.GateLooksOpen(gateMonsterId, requiredKills);

    private void Refresh()
    {
        if (_label == null) return;

        long active = KillTracker.ActiveKills(gateMonsterId);
        long afk    = KillTracker.AfkKills(gateMonsterId);

        string monster = GameManager.Content?.GetMonster(gateMonsterId)?.DisplayName ?? gateMonsterId;

        if (LooksOpen)
        {
            _label.text = $"The Throne  ·  Open";
            return;
        }

        string line = $"{monster} Kills — {NumberFormatter.Format(active)} / {NumberFormatter.Format(requiredKills)}";

        // Said explicitly, because a player with nine thousand sleeping kills and a
        // sealed door will otherwise conclude the game lost their progress.
        if (afk > 0L)
            line += $"\n({NumberFormatter.Format(afk)} while away — these do not count)";

        _label.text = line;
    }

    /// <summary>
    /// Tries to go through.
    ///
    /// The server is asked every time, and its answer is what happens. The local
    /// mirror only decides whether it is worth asking -- refusing early saves a round
    /// trip for a player who is plainly nowhere near, and costs nothing when the
    /// mirror is behind, because the next kill refreshes it.
    /// </summary>
    public async Awaitable<bool> TryEnterAsync(IGameBackend backend, string characterId)
    {
        if (backend == null || string.IsNullOrEmpty(characterId)) return false;

        try
        {
            BossGateSnapshot gate = await backend.UnlockBossAsync(characterId);

            _serverSaysOpen = gate != null && gate.open;

            if (_serverSaysOpen != true)
            {
                long remaining = gate?.remaining ?? requiredKills;

                GameEvents.FireToast(
                    $"The throne is sealed. {NumberFormatter.Format(remaining)} more kills.",
                    ChatTone.Warning);

                return false;
            }
        }
        catch (BackendException e)
        {
            // A transient failure is not a refusal, and saying "sealed" would be a
            // lie that sends the player off to grind kills they already have.
            GameEvents.FireToast(
                e.IsTransient
                    ? "The throne does not answer. Try again in a moment."
                    : $"The throne is sealed. {e.Message}",
                ChatTone.Warning);

            return false;
        }

        if (string.IsNullOrEmpty(destinationMapId))
        {
            GameEvents.FireToast("The way is open, but the throne is not built yet.", ChatTone.Info);
            return false;
        }

        GameManager.Zone?.EnterMap(destinationMapId);
        return true;
    }
}
