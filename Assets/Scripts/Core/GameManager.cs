using UnityEngine;

/// <summary>
/// Top-level singleton. Bootstraps all other managers, owns the game state machine.
/// Placed on a persistent "Managers" GameObject in the Bootstrap scene.
/// </summary>
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    public enum GameState
    {
        Splash,
        Login,
        CharacterSelect,
        CharacterCreate,
        LoadingIntoGame,
        InGame,
        Disconnected
    }

    public GameState CurrentState { get; private set; } = GameState.Splash;

    // ── Manager references (all on the same Managers GameObject) ─────────────
    public static ContentManager    Content     { get; private set; }
    public static AccountManager    Account     { get; private set; }
    public static CharacterManager  Character   { get; private set; }
    public static InventoryManager  Inventory   { get; private set; }
    public static SkillManager      Skills      { get; private set; }
    public static ActivityManager   Activity    { get; private set; }
    public static MergeManager      Merge       { get; private set; }
    public static ZoneManager       Zone        { get; private set; }
    public static CollectionManager Collection  { get; private set; }
    public static UIManager         UI          { get; private set; }
    public static AudioManager      Audio       { get; private set; }
    public static GhostSpawner      Ghosts      { get; private set; }

    // ── Stubs (Phase 7+) ──────────────────────────────────────────────────────
    public static AuctionManager    Auction     { get; private set; }
    public static GuildManager      Guild       { get; private set; }
    public static QuestManager      Quests      { get; private set; }
    public static SlayerManager     Slayer      { get; private set; }

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Collect managers — they're all components on this same GameObject
        Content    = GetComponent<ContentManager>();
        Account    = GetComponent<AccountManager>();
        Character  = GetComponent<CharacterManager>();
        Inventory  = GetComponent<InventoryManager>();
        Skills     = GetComponent<SkillManager>();
        Activity   = GetComponent<ActivityManager>();
        Merge      = GetComponent<MergeManager>();
        Zone       = GetComponent<ZoneManager>();
        Collection = GetComponent<CollectionManager>();
        UI         = GetComponent<UIManager>();
        Audio      = GetComponent<AudioManager>();
        Ghosts     = GetComponent<GhostSpawner>();
        Auction    = GetComponent<AuctionManager>();
        Guild      = GetComponent<GuildManager>();
        Quests     = GetComponent<QuestManager>();
        Slayer     = GetComponent<SlayerManager>();

        // Validate — warn if a manager is missing (easy to catch in Editor)
        if (Content   == null) Debug.LogError("GameManager: ContentManager component missing!");
        if (UI        == null) Debug.LogError("GameManager: UIManager component missing!");
        if (Audio     == null) Debug.LogError("GameManager: AudioManager component missing!");
    }

    void Start()
    {
        // Content loads first (async), then transitions to Login when ready
        if (Content != null)
            Content.LoadAll(OnContentReady);
        else
            TransitionTo(GameState.Login);
    }

    void OnContentReady()
    {
        TransitionTo(GameState.Login);
    }

    /// <summary>Transition to a new game state. UIManager reacts to show the right screen.</summary>
    public void TransitionTo(GameState newState)
    {
        Debug.Log($"[GameManager] {CurrentState} → {newState}");
        CurrentState = newState;
        UI?.OnStateChanged(newState);
        Audio?.OnStateChanged(newState);
    }

    // ── Convenience transition methods ────────────────────────────────────────

    public void GoToCharacterSelect()       => TransitionTo(GameState.CharacterSelect);
    public void GoToCharacterCreate()       => TransitionTo(GameState.CharacterCreate);
    public void GoToGame()                  => TransitionTo(GameState.InGame);
    public void ReturnToMainMenu()
    {
        Character?.SaveAndDisconnect();
        Zone?.UnloadCurrentZone();
        TransitionTo(GameState.CharacterSelect);
        GameEvents.OnReturnToMainMenu?.Invoke();
    }
}
