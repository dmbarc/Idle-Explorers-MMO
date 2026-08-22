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
    public static Canvas MainCanvas  { get; private set; }
    public static Canvas DragCanvas  { get; private set; }

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

        // Ensure EventSystem exists
        if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
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
        }
    }

    // ── Screen stack ──────────────────────────────────────────────────────────

    public T Push<T>() where T : UIScreen, new()
    {
        // Hide the current top without destroying it
        if (_screenStack.Count > 0)
            _screenStack.Peek().gameObject.SetActive(false);

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
        while (_screenStack.Count > 0) Pop();
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
