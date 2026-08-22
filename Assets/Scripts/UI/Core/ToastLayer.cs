using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The listener for GameEvents.OnToastRequested.
/// Lives on its own overlay canvas above every screen so toasts are visible
/// regardless of which screen is on top of the UIManager stack.
///
/// Created by UIManager.Awake — never placed in a scene.
/// </summary>
public class ToastLayer : MonoBehaviour
{
    private const int   MaxVisible   = 4;
    private const float LifetimeSecs = 2.0f;
    private const float RiseDistance = 40f;

    private RectTransform      _container;
    private readonly List<GameObject> _active = new();

    // ── Setup ─────────────────────────────────────────────────────────────────

    public static ToastLayer Create()
    {
        var go = new GameObject("ToastCanvas");
        DontDestroyOnLoad(go);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;   // above MainCanvas (0) and DragCanvas (999)

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        // No GraphicRaycaster: toasts must never intercept clicks meant for the
        // screen underneath them.

        return go.AddComponent<ToastLayer>();
    }

    void Awake()
    {
        // Toasts stack downward from just below the top edge, centred.
        var containerGo = new GameObject("ToastContainer", typeof(RectTransform));
        containerGo.transform.SetParent(transform, false);

        _container = containerGo.GetComponent<RectTransform>();
        _container.anchorMin = new Vector2(0.5f, 1f);
        _container.anchorMax = new Vector2(0.5f, 1f);
        _container.pivot     = new Vector2(0.5f, 1f);
        _container.anchoredPosition = new Vector2(0f, -90f);
        _container.sizeDelta = new Vector2(700f, 0f);

        var vlg = containerGo.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = 8f;
        vlg.childAlignment         = TextAnchor.UpperCenter;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = containerGo.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    void OnEnable()  => GameEvents.OnToastRequested += Show;
    void OnDisable() => GameEvents.OnToastRequested -= Show;

    // ── Display ───────────────────────────────────────────────────────────────

    public void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        // Retire the oldest rather than letting a burst of level-ups fill the screen
        while (_active.Count >= MaxVisible)
        {
            var oldest = _active[0];
            _active.RemoveAt(0);
            if (oldest != null) Destroy(oldest);
        }

        var toast = BuildToast(message);
        _active.Add(toast);
        StartCoroutine(AnimateAndDestroy(toast));
    }

    private GameObject BuildToast(string message)
    {
        var theme = UIManager.Theme;

        var go  = new GameObject("Toast", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        go.transform.SetParent(_container, false);
        go.GetComponent<Image>().color = theme != null ? theme.cardBg : new Color(0.15f, 0.12f, 0.18f, 1f);

        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight       = 44f;
        layout.preferredHeight = 44f;

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(go.transform, false);
        UIFactory.FillParent(labelGo.GetComponent<RectTransform>());

        var tmp = labelGo.GetComponent<TextMeshProUGUI>();
        tmp.text          = message;
        tmp.fontSize      = theme != null ? theme.fontSizeBody : 18f;
        tmp.color         = theme != null ? theme.textPrimary : Color.white;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        if (theme != null && theme.font != null) tmp.font = theme.font;

        return go;
    }

    private IEnumerator AnimateAndDestroy(GameObject toast)
    {
        var group = toast.GetComponent<CanvasGroup>();
        var rt    = toast.GetComponent<RectTransform>();
        Vector2 startPos = rt.anchoredPosition;

        // Fade in quickly so the message registers immediately
        const float fadeIn = 0.15f;
        for (float t = 0f; t < fadeIn; t += Time.unscaledDeltaTime)
        {
            if (toast == null) yield break;
            group.alpha = Mathf.Lerp(0f, 1f, t / fadeIn);
            yield return null;
        }
        if (toast == null) yield break;
        group.alpha = 1f;

        // Hold
        yield return new WaitForSecondsRealtime(LifetimeSecs);

        // Rise and fade out
        const float fadeOut = 0.5f;
        for (float t = 0f; t < fadeOut; t += Time.unscaledDeltaTime)
        {
            if (toast == null) yield break;
            float k = t / fadeOut;
            group.alpha       = Mathf.Lerp(1f, 0f, k);
            rt.anchoredPosition = startPos + new Vector2(0f, RiseDistance * k);
            yield return null;
        }

        _active.Remove(toast);
        if (toast != null) Destroy(toast);
    }
}
