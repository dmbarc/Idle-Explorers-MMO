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

    // ── Screen stack ──────────────────────────────────────────────────────────

    public T Push<T>() where T : UIScreen, new()
    {
        // Hide the current top without destroying it. OnHide() must be called here
        // to mirror Pop() — screens unsubscribe from GameEvents in OnHide, and
        // skipping it leaks a duplicate subscription every time they are re-shown.
        if (_screenStack.Count > 0)
        {
            var previous = _screenStack.Peek();
            previous.OnHide();
            previous.gameObject.SetActive(false);
        }

        var screen = GetOrCreate<T>();
        screen.gameObject.SetActive(true);
        screen.OnShow();
        _screenStack.Push(screen);
        return screen;
    }

    public void Pop()
    {
        if (_screenStack.Count == 0) return;
        var top = _screenStack.Pop();
        top.OnHide();
        top.gameObject.SetActive(false);

        if (_screenStack.Count > 0)
        {
            var prev = _screenStack.Peek();
            prev.gameObject.SetActive(true);
            prev.OnResume();
        }
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
