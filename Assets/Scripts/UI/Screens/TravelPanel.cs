using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Map travel: every map in zone_data.json, grouped by zone, with its requirements.
///
/// Locked maps are shown rather than hidden, with the reason spelled out — a
/// destination you cannot see is indistinguishable from one that does not exist, and
/// "requires character level 5" is the thing that tells a player what to do next.
///
/// A map whose scene is not in Build Settings is called out separately, because that
/// is an authoring problem rather than a progression one and the two look identical
/// from the player's side: a button that does nothing.
/// </summary>
public class TravelPanel : UIScreen
{
    public override bool IsOverlay     => true;

    /// <summary>Requirements depend on live character state.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var panel   = UIFactory.Panel(transform, "TravelPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.20f, 0.10f);
        panelRt.anchorMax = new Vector2(0.80f, 0.90f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildList(panel.transform, theme);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.35f, 0.02f, 0.65f, 0.09f);
    }

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.90f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform, "TRAVEL", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0.45f, 0.55f, 0.95f);

        string here = GameManager.Zone?.CurrentMap?.DisplayName ?? "nowhere";
        var location = UIFactory.Label(header.transform, $"Currently in {here}",
                                        theme.fontSizeLabel, theme.textSecondary,
                                        TextAlignmentOptions.MidlineRight);
        UIFactory.At(location, 0.45f, 0.45f, 0.98f, 0.95f);

        var hint = UIFactory.Label(header.transform,
                                    "Travelling banks your progress and carries your current activity with you.",
                                    theme.fontSizeLabel, theme.textSecondary,
                                    TextAlignmentOptions.MidlineLeft);
        UIFactory.At(hint, 0.02f, 0.06f, 0.98f, 0.44f);
    }

    private void BuildList(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "TravelScroll", theme.spacing);
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.03f, 0.11f);
        scrollRt.anchorMax = new Vector2(0.97f, 0.885f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var zones = GameManager.Content?.Zones;
        if (zones == null || zones.Count == 0)
        {
            UIFactory.Label(content, "No zones are defined.", theme.fontSizeBody,
                             theme.textSecondary, TextAlignmentOptions.Center);
            return;
        }

        foreach (var zone in zones.Values)
        {
            if (zone?.maps == null || zone.maps.Length == 0) continue;

            var heading = UIFactory.Label(content, zone.DisplayName.ToUpperInvariant(),
                                           theme.fontSizeSmall, theme.accentGold,
                                           TextAlignmentOptions.MidlineLeft);
            var headingEl = heading.gameObject.AddComponent<LayoutElement>();
            headingEl.minHeight = headingEl.preferredHeight = 34f;

            foreach (var map in zone.maps)
                if (map != null) BuildMapRow(content, theme, map);
        }
    }

    private void BuildMapRow(Transform parent, UITheme theme, MapData map)
    {
        bool isHere = GameManager.Zone?.CurrentMapId == map.id;

        string reason   = null;
        bool   canEnter = GameManager.Zone != null &&
                          GameManager.Zone.CanEnterMap(map.id, out reason);

        // A scene missing from Build Settings fails at load with a console error and
        // nothing visible in game, so it is reported here as its own kind of problem.
        bool sceneReady = SceneIsBuildable(map);

        var row   = UIFactory.Panel(parent, $"Map_{map.id}", theme.cardBg, false);
        var rowEl = row.AddComponent<LayoutElement>();
        rowEl.minHeight = rowEl.preferredHeight = 84f;

        var name = UIFactory.Label(row.transform,
                                    isHere ? $"{map.DisplayName}  (you are here)" : map.DisplayName,
                                    theme.fontSizeSmall,
                                    isHere ? theme.accentGreen
                                           : (canEnter ? theme.textPrimary : theme.textDisabled),
                                    TextAlignmentOptions.BottomLeft);
        UIFactory.At(name, 0.03f, 0.55f, 0.74f, 0.94f);

        string subtitle = !sceneReady
            ? (string.IsNullOrEmpty(map.sceneAddress)
                ? "Not built yet — this map has no scene."
                : $"Scene '{map.sceneAddress}' is not in Build Settings — run Setup Everything")
            : canEnter ? map.description
                       : (string.IsNullOrEmpty(reason) ? "Locked." : reason);

        var description = UIFactory.Label(row.transform, subtitle, theme.fontSizeLabel,
                                           !sceneReady ? theme.accentRed
                                                       : (canEnter ? theme.textSecondary : theme.accentRed),
                                           TextAlignmentOptions.TopLeft);
        UIFactory.At(description, 0.03f, 0.08f, 0.74f, 0.53f);
        description.textWrappingMode = TextWrappingModes.Normal;

        var travel = UIFactory.Button(row.transform, isHere ? "HERE" : "TRAVEL",
                                       () => Travel(map), width: 0f);
        UIFactory.At(travel, 0.77f, 0.20f, 0.97f, 0.80f);
        travel.interactable = canEnter && !isHere && sceneReady;
    }

    private static string SceneNameFor(MapData map) =>
        string.IsNullOrEmpty(map.sceneAddress) ? ZoneManager.DefaultMapScene : map.sceneAddress;

    /// <summary>
    /// Whether the map has a scene of its own that is in Build Settings.
    ///
    /// An empty sceneAddress means no scene has been authored for this map yet. It
    /// falls back to the default scene at load, which for the four unbuilt maps means
    /// walking into the Goblin Camp wearing a different map's node list — every node
    /// in the scene fails to resolve and logs a warning, and the map you asked for is
    /// nowhere. Better to say it is not built than to deliver the wrong place.
    /// </summary>
    private static bool SceneIsBuildable(MapData map)
    {
        if (string.IsNullOrEmpty(map.sceneAddress)) return false;

        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings; i++)
        {
            string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == map.sceneAddress) return true;
        }
        return false;
    }

    private void Travel(MapData map)
    {
        if (GameManager.Zone == null) return;
        if (!GameManager.Zone.CanEnterMap(map.id, out string reason))
        {
            GameEvents.FireToast(reason ?? "You cannot go there yet.");
            return;
        }

        // Save before the scene swaps. The activity snapshot and logout stamp are what
        // AFK accrual reads, and a crash mid-transition should not cost the session.
        GameManager.Save?.Save();

        GameManager.UI?.Pop();
        GameManager.Zone.EnterMap(map.id);
        GameEvents.FireToast($"Travelling to {map.DisplayName}…");
    }
}
