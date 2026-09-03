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

        AddBeacon();
        Refresh();
    }

    /// <summary>
    /// Makes the portal look like a door rather than like scenery.
    ///
    /// ══ WHY THIS EXISTS ════════════════════════════════════════════════════
    ///
    /// The portal is a tall grey stone, and it stood in a field of nine other kinds of
    /// rock. Players reported it missing from the map -- not hard to find, missing.
    /// Moving it onto open ground fixed half of that; the other half is that nothing
    /// about it said "this is the way in".
    ///
    /// Built in code for the same reason DamageNumber and NamePlate are: no prefab, no
    /// scene setup, and it survives the editor regenerating the map -- which it would
    /// have to, because the map IS regenerated from a recipe.
    /// </summary>
    private void AddBeacon()
    {
        // ══ A COLUMN OF LIGHT ═════════════════════════════════════════════════
        //
        // Unlit and transparent so it glows rather than being shaded like rock, and
        // tall enough to clear the treeline -- the point is to be visible from across
        // the map, not to decorate the stone.
        var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);

        beam.name = "PortalBeam";
        beam.transform.SetParent(transform, false);
        beam.transform.localPosition = Vector3.up * BeaconHeight * 0.5f;
        beam.transform.localScale    = new Vector3(0.9f, BeaconHeight * 0.5f, 0.9f);

        // No collider: this is something to see, never something to walk into or to
        // catch a click that belongs to the portal itself.
        Destroy(beam.GetComponent<Collider>());

        var renderer = beam.GetComponent<Renderer>();

        // ══ WHY THE FALLBACK CHAIN GOES ALL THE WAY DOWN ═══════════════════
        //
        // This shipped magenta. Shader.Find only finds shaders INCLUDED IN THE BUILD,
        // and when both of the first two came back null the code left the primitive
        // wearing its default material -- which under URP is the built-in Diffuse, and
        // the built-in pipeline's shaders are not in a URP build. Magenta is what a
        // missing shader looks like, and it was a hundred metres tall.
        //
        // Sprites/Default always ships, so the chain now ends somewhere real and the
        // material is ALWAYS assigned rather than conditionally. Copied from
        // TelegraphDecal, which had already learned this.
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default");

        renderer.material = new Material(unlit) { name = "PortalBeam" };
        renderer.material.color = BeaconColor;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // ══ AND SOMETHING THE EYE CATCHES AT NIGHT ════════════════════════════
        var glow = new GameObject("PortalGlow");

        glow.transform.SetParent(transform, false);
        glow.transform.localPosition = Vector3.up * 1.5f;

        var light = glow.AddComponent<Light>();

        light.type      = LightType.Point;
        light.color     = BeaconColor;
        light.range     = 14f;
        light.intensity = 3.5f;
    }

    /// <summary>How far the beam reaches. Chosen to clear the treeline, not the stone.</summary>
    private const float BeaconHeight = 14f;

    /// <summary>
    /// Violet, and deliberately a colour nothing else in the camp uses.
    ///
    /// The grass is green, the rocks grey, the fires orange. A beacon that shares a
    /// hue with any of them is a beacon somebody has to look for.
    /// </summary>
    private static readonly Color BeaconColor = new(0.62f, 0.36f, 1f, 0.40f);

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
        if (!await TryOpenAsync(backend, characterId)) return false;

        if (string.IsNullOrEmpty(destinationMapId))
        {
            GameEvents.FireToast("The way is open, but the throne is not built yet.", ChatTone.Info);
            return false;
        }

        GameManager.Zone?.EnterMap(destinationMapId);
        return true;
    }

    /// <summary>
    /// Asks the server whether this character may go through, and grants the unlock
    /// if they may. Does not travel.
    ///
    /// Split out from TryEnterAsync because a GROUP needs the question without the
    /// answer: whoever is standing in the portal has to know the way is open for them
    /// before calling three other people to it, and then travel with everybody else at
    /// the end of the countdown rather than immediately.
    /// </summary>
    public async Awaitable<bool> TryOpenAsync(IGameBackend backend, string characterId)
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

        return true;
    }
}
