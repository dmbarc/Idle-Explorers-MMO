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
    public static BankManager       Bank        { get; private set; }
    public static EquipmentManager  Equipment   { get; private set; }
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
    public static SaveManager       Save        { get; private set; }

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
        Bank       = GetComponent<BankManager>();
        Equipment  = GetComponent<EquipmentManager>();
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
        Save       = GetComponent<SaveManager>();

        // Validate — warn if a manager is missing (easy to catch in Editor)
        if (Content   == null) Debug.LogError("GameManager: ContentManager component missing!");
        if (UI        == null) Debug.LogError("GameManager: UIManager component missing!");
        if (Audio     == null) Debug.LogError("GameManager: AudioManager component missing!");
    }

    void Start()
    {
        // CurrentState is initialized to Splash but nothing had ever transitioned
        // *to* it, so UIManager never pushed SplashScreen and the first frame was
        // blank. Transition explicitly so the splash is actually shown while
        // content loads.
        TransitionTo(GameState.Splash);

        // Volume / quality / fullscreen chosen in a previous session
        SettingsPanel.ApplySavedSettings();

        // Content loads first (async), then transitions to Login when ready
        if (Content != null)
            Content.LoadAll(OnContentReady);
        else
            TransitionTo(GameState.Login);
    }

    void OnContentReady()
    {
        // Deliberately does not transition. SplashScreen polls Content.IsLoaded and
        // moves to Login itself once its loading bar has played — transitioning here
        // as well would race it and skip the splash entirely.
        Debug.Log("[GameManager] Content ready.");

        // Content-driven systems get one chance to complain about their own data
        // while the console is still readable. A talent with a misspelled effectType
        // costs a point and does nothing, which is otherwise invisible.
        TalentManager.ValidateContent();
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

    /// <summary>Map a character spawns into when they have never played before.</summary>
    public const string StartingMapId = "goblin_camp";

    public void GoToCharacterSelect()       => TransitionTo(GameState.CharacterSelect);
    public void GoToCharacterCreate()       => TransitionTo(GameState.CharacterCreate);

    /// <summary>
    /// Enters the world. Loads the character's last map (or the starting map) and
    /// shows the HUD — without the map load, InGame would show a HUD over nothing.
    /// </summary>
    public void GoToGame()
    {
        var character = CharacterManager.Current;
        string mapId  = !string.IsNullOrEmpty(character?.lastMapId) ? character.lastMapId : StartingMapId;

        TransitionTo(GameState.InGame);
        Zone?.EnterMap(mapId);

        if (character != null) character.lastMapId = mapId;
    }
    public void ReturnToMainMenu()
    {
        Character?.SaveAndDisconnect();   // stamps lastLogoutUnixTime and persists
        Zone?.UnloadCurrentZone();
        TransitionTo(GameState.CharacterSelect);
        GameEvents.OnReturnToMainMenu?.Invoke();
    }
}
