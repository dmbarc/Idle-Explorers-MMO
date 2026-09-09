using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen stack manager. All UI is code-generated and managed here.
/// Push a screen to show it; Pop to return to the previous one.
///
/// Bootstrap creates two Canvases on startup:
///   MainCanvas  (sortingOrder 0)  — all game screens
///   DragCanvas  (sortingOrder 999) — drag ghost icons only
/// </summary>
public class UIManager : MonoBehaviour
{
    public static Canvas     MainCanvas { get; private set; }
    public static Canvas     DragCanvas { get; private set; }
    public static ToastLayer Toasts     { get; private set; }
    public static BootstrapCamera MenuCamera { get; private set; }

    private readonly Stack<UIScreen> _screenStack = new();
    private readonly Dictionary<Type, UIScreen> _activeScreens = new();

    // ── Current theme (colors, fonts) ────────────────────────────────────────
    public static UITheme Theme { get; private set; }

    void Awake()
    {
        // Load theme from Resources (UITheme ScriptableObject)
        Theme = Resources.Load<UITheme>("UITheme") ?? ScriptableObject.CreateInstance<UITheme>();

        // Create main canvas
        MainCanvas = CreateCanvas("MainCanvas", 0);
        DragCanvas = CreateCanvas("DragCanvas", 999);

        // Toast overlay sits above both and listens for OnToastRequested
        Toasts = ToastLayer.Create();

        // Menus need a camera or Unity renders "No cameras rendering" over them.
        // It disables itself once a map scene brings its own camera.
        MenuCamera = BootstrapCamera.Create();

        // Ensure EventSystem exists
        if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            // InputSystemUIInputModule, not StandaloneInputModule: the Input System
            // package drives gameplay input already (PlayerController reads
            // Mouse.current), and the legacy module throws outright if the project
            // is ever switched off the old backend.
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            DontDestroyOnLoad(es);
        }
    }

    public void OnStateChanged(GameManager.GameState state)
    {
        ClearAll();

        // Chat does not survive leaving the world. The next character should not open
        // their window onto the last one's death, and there is no window on the menus
        // to read it in anyway.
        if (state != GameManager.GameState.InGame) ChatLog.Clear();

        // Nor do buffs. They belong to a character, and the mirror is keyed on nothing
        // but the stat — so carrying one out to the menu would apply the last
        // character's Draught of Fury to the next one the player picks up.
        //
        // The SERVER is unaffected either way: the row is keyed on character_id and
        // is still there when they come back. This only stops the client showing a
        // buff on somebody who does not have it.
        if (state != GameManager.GameState.InGame) BuffManager.Clear();

        switch (state)
        {
            case GameManager.GameState.Splash:          Push<SplashScreen>();          break;
            case GameManager.GameState.Login:           Push<LoginScreen>();           break;
            case GameManager.GameState.CharacterSelect: Push<CharacterSelectScreen>(); break;
            case GameManager.GameState.CharacterCreate: Push<CharCreateNameScreen>();  break;
            case GameManager.GameState.InGame:          Push<GameHUD>();               break;

            case GameManager.GameState.Disconnected:    Push<LoginScreen>();           break;

            // LoadingIntoGame is currently unused — maps load additively while the
            // HUD is already up. Note it does NOT push SplashScreen: that screen
            // transitions itself to Login once content is loaded, which would
            // bounce the player out of the game.
            case GameManager.GameState.LoadingIntoGame:
                Debug.Log("[UIManager] LoadingIntoGame — keeping current screen.");
                break;
        }
    }


    // ── What the world camera is allowed to hear ──────────────────────────────

    /// <summary>
    /// True when a screen other than the HUD is on top of the stack.
    ///
    /// The world camera reads the scroll wheel directly from the Mouse device, which
    /// knows nothing about the UI — so scrolling a crafting recipe list at the anvil
    /// zoomed the map at the same time, and every list in the game had the same
    /// problem. Panels are the exception rather than the rule, so this asks "is
    /// anything but the HUD open" rather than naming the station screens: a panel
    /// added later gets the behaviour without anyone remembering to list it.
    /// </summary>
    public static bool IsModalOpen { get; private set; }

    /// <summary>
    /// True while the player is typing into a text field.
    ///
    /// The camera pans on WASD and orbits on Q/E, all read straight from the Keyboard
    /// device. Without this, typing "was" in chat would walk the view off the
    /// character. Set by the field itself on select and deselect, because Unity gives
    /// no reliable global "something has focus" that survives a field being destroyed
    /// while still focused.
    /// </summary>
    public static bool TextInputFocused { get; set; }

    /// <summary>True when world-camera keyboard and scroll input should be ignored.</summary>
    public static bool WorldInputBlocked => IsModalOpen || TextInputFocused;

    /// <summary>
    /// Recomputed after every stack change rather than tracked incrementally — a
    /// counter would drift the first time a screen was closed by any route that did
    /// not decrement it.
    /// </summary>
    private void SyncModalState()
    {
        IsModalOpen = _screenStack.Count > 0 && !(_screenStack.Peek() is GameHUD);
    }

    // ── Screen stack ──────────────────────────────────────────────────────────

    public T Push<T>() where T : UIScreen, new()
    {
        var screen = GetOrCreate<T>();

        // Which screens people actually open. Reported here rather than from each
        // screen's OnShow, because this is the one place every push passes through --
        // the same reasoning that put the position save in ZoneManager.EnterMap after
        // two attempts in the wrong place.
        IdleExplorers.Backend.TelemetrySync.Report(
            IdleExplorers.Backend.Telemetry.ScreenOpen, ("screen", typeof(T).Name));

        // Hide the current top unless the incoming screen is an overlay. OnHide()
        // must be called when we do hide it, mirroring Pop() — screens unsubscribe
        // from GameEvents there, and skipping it leaks a duplicate subscription
        // every time they are re-shown.
        if (_screenStack.Count > 0 && !screen.IsOverlay)
        {
            var previous = _screenStack.Peek();
            previous.OnHide();
            previous.gameObject.SetActive(false);
        }

        screen.gameObject.SetActive(true);
        screen.transform.SetAsLastSibling();   // overlays must draw above the stack

        // Screens that render game state inside Build() need it re-run, or they
        // keep showing whatever was true the first time they appeared.
        if (screen.RebuildOnShow) screen.RebuildContents();

        screen.OnShow();
        _screenStack.Push(screen);

        // One place for the panel-open sound, so a new screen gets it for free.
        GameManager.Audio?.Play(Sfx.Open);

        SyncModalState();
        return screen;
    }

    /// <summary>
    /// Whether a screen of this kind is on the stack.
    ///
    /// For the callers that are told to open something by an EVENT rather than by a
    /// keypress -- a group call arriving on a poll -- and must not stack a second copy
    /// of it on the next poll two seconds later.
    /// </summary>
    public bool IsOpen<T>() where T : UIScreen
    {
        foreach (var screen in _screenStack) if (screen is T) return true;

        return false;
    }

    public void Pop()
    {
        if (_screenStack.Count == 0) return;
        var top = _screenStack.Pop();
        bool wasOverlay = top.IsOverlay;
        top.OnHide();
        top.gameObject.SetActive(false);

        GameManager.Audio?.Play(Sfx.Close);

        if (_screenStack.Count > 0)
        {
            var prev = _screenStack.Peek();
            prev.gameObject.SetActive(true);

            // An overlay never hid the screen below, so that screen never had
            // OnHide called and is still subscribed — resuming it again would
            // double every subscription.
            if (!wasOverlay) prev.OnResume();
        }

        SyncModalState();
    }

    public void PopTo<T>() where T : UIScreen
    {
        while (_screenStack.Count > 0 && !(_screenStack.Peek() is T))
            Pop();
    }

    public void ClearAll()
    {
        // Deliberately not a loop of Pop(): Pop resumes the screen underneath,
        // which would fire OnResume (and its event subscriptions) on every screen
        // in the stack a moment before it too gets torn down.
        while (_screenStack.Count > 0)
        {
            var screen = _screenStack.Pop();
            if (screen == null) continue;
            screen.OnHide();
            screen.gameObject.SetActive(false);
        }

        SyncModalState();
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    private T GetOrCreate<T>() where T : UIScreen, new()
    {
        var type = typeof(T);
        if (_activeScreens.TryGetValue(type, out var existing) && existing != null)
            return (T)existing;

        var go = new GameObject(type.Name, typeof(RectTransform));
        go.transform.SetParent(MainCanvas.transform, false);
        UIFactory.FillParent(go.GetComponent<RectTransform>());

        var screen = go.AddComponent<T>();
        screen.Build();
        _activeScreens[type] = screen;
        return screen;
    }

    // ── Canvas creation ───────────────────────────────────────────────────────

    private static Canvas CreateCanvas(string name, int sortOrder)
    {
        var go = new GameObject(name);
        DontDestroyOnLoad(go);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder  = sortOrder;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }
}
