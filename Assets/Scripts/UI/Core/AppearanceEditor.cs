using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The appearance editor: a live character on the left, one row per category on the
/// right, and every change visible immediately.
///
/// Built as a reusable component rather than a screen because two places need exactly
/// this — character creation, and the barber item that reopens it later. A second copy
/// would be two editors that offer different options, which is how a game ends up with
/// hairstyles you can only pick at creation.
///
/// It edits a SpumSaveData in place and repaints the preview after every change, so
/// there is no "apply" step to forget and nothing to keep in sync.
/// </summary>
public class AppearanceEditor
{
    private readonly SpumSaveData     _look;
    private readonly CharacterPreview _preview;
    private readonly List<Action>     _refreshers = new();

    private AppearanceEditor(SpumSaveData look, CharacterPreview preview)
    {
        _look    = look;
        _preview = preview;
    }

    /// <summary>
    /// Fills <paramref name="parent"/> with the editor. The look is edited in place,
    /// so the caller keeps whatever reference it passed in.
    /// </summary>
    public static AppearanceEditor Build(Transform parent, SpumSaveData look,
                                          float xMin, float yMin, float xMax, float yMax)
    {
        var theme = UIManager.Theme;

        // Portrait on the left, controls on the right. The preview is the whole point
        // of the screen — a list of names with no picture is a form, not a character.
        var portrait = UIFactory.Panel(parent, "AppearancePortrait", theme.slotBg, false);
        UIFactory.At(portrait.transform, xMin, yMin, xMin + (xMax - xMin) * 0.38f, yMax);

        var preview = CharacterPreview.Create(portrait.transform, look, "AppearancePreview");

        var editor = new AppearanceEditor(look, preview);

        var (scroll, content) = UIFactory.ScrollList(parent, "AppearanceOptions", theme.spacing);
        UIFactory.At(scroll, xMin + (xMax - xMin) * 0.40f, yMin, xMax, yMax);

        editor.BuildCategory(content, theme, "Body",      SpumAppearance.Bodies,
                              () => look.bodyAddress,     v => look.bodyAddress = v);
        editor.BuildCategory(content, theme, "Hair",      SpumAppearance.Hairs,
                              () => look.hairAddress,     v => look.hairAddress = v);
        editor.BuildSwatches(content, theme, "Hair Color",
                              () => look.hairColor,       v => look.hairColor = v);
        editor.BuildCategory(content, theme, "Facial Hair", SpumAppearance.FaceHairs,
                              () => look.faceHairAddress, v => look.faceHairAddress = v);
        editor.BuildCategory(content, theme, "Eyes",      SpumAppearance.Eyes,
                              () => look.eyeAddress,      v => look.eyeAddress = v);
        editor.BuildSwatches(content, theme, "Eye Color",
                              () => look.eyeColor,        v => look.eyeColor = v);
        editor.BuildCategory(content, theme, "Weapon",    SpumAppearance.Weapons,
                              () => look.weaponAddress,   v => look.weaponAddress = v);
        editor.BuildCategory(content, theme, "Cloak",     SpumAppearance.Backs,
                              () => look.backAddress,     v => look.backAddress = v);

        editor.BuildRandomise(content, theme);

        return editor;
    }

    /// <summary>Repaints the preview and every row's caption.</summary>
    public void Refresh()
    {
        _preview?.SetLook(_look);
        foreach (var refresh in _refreshers) refresh();
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// One category: a label, a previous/next pair, and the current option's name.
    ///
    /// Prev/next rather than a grid, because several categories run to fourteen
    /// options and every one of them is a small sprite that would be unreadable as a
    /// thumbnail. The live preview is the picker.
    /// </summary>
    private void BuildCategory(Transform parent, UITheme theme, string label,
                                (string Address, string Name)[] options,
                                Func<string> get, Action<string> set)
    {
        var row = UIFactory.Panel(parent, $"Row_{label}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 56f;

        var title = UIFactory.Label(row.transform, label, theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.04f, 0.52f, 0.60f, 0.94f);

        var current = UIFactory.Label(row.transform, "", theme.fontSizeSmall,
                                       theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(current, 0.04f, 0.08f, 0.60f, 0.50f);

        void Step(int direction)
        {
            int index = SpumAppearance.IndexOf(options, get());

            // Wraps in both directions, so a player who overshoots the end can carry
            // on rather than having to walk all the way back.
            index = (index + direction + options.Length) % options.Length;

            set(options[index].Address);
            Refresh();
        }

        var prev = UIFactory.Button(row.transform, "◄", () => Step(-1), width: 0f);
        UIFactory.At(prev, 0.63f, 0.15f, 0.75f, 0.85f);

        var next = UIFactory.Button(row.transform, "►", () => Step(1), width: 0f);
        UIFactory.At(next, 0.86f, 0.15f, 0.98f, 0.85f);

        var counter = UIFactory.Label(row.transform, "", theme.fontSizeLabel,
                                       theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(counter, 0.755f, 0.15f, 0.855f, 0.85f);

        _refreshers.Add(() =>
        {
            int index    = SpumAppearance.IndexOf(options, get());
            current.text = options[index].Name;
            counter.text = $"{index + 1}/{options.Length}";
        });
    }

    /// <summary>
    /// A colour row: fixed swatches rather than a wheel.
    ///
    /// The sprites are small and low-contrast, so most of a continuous picker's range
    /// produces something indistinguishable from its neighbour. Eleven named choices
    /// are all a player can actually tell apart at this size.
    /// </summary>
    private void BuildSwatches(Transform parent, UITheme theme, string label,
                                Func<string> get, Action<string> set)
    {
        var row = UIFactory.Panel(parent, $"Row_{label}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 56f;

        var title = UIFactory.Label(row.transform, label, theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.04f, 0.52f, 0.40f, 0.94f);

        var chosen = UIFactory.Label(row.transform, "", theme.fontSizeLabel,
                                      theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(chosen, 0.04f, 0.08f, 0.40f, 0.50f);

        var swatches = SpumAppearance.Swatches;
        var buttons  = new List<Image>();

        var strip = UIFactory.HStack(row.transform, 4f, "Swatches");
        UIFactory.At(strip, 0.42f, 0.18f, 0.98f, 0.82f);
        strip.childForceExpandWidth = true;
        strip.childControlWidth     = true;

        for (int i = 0; i < swatches.Length; i++)
        {
            var (hex, name) = swatches[i];

            var swatch = UIFactory.Panel(strip.transform, $"Swatch_{name}", Color.white, false);
            var img    = swatch.GetComponent<Image>();

            // "Natural" has no colour of its own, so it shows as the panel background
            // rather than as white — which would look like a real choice.
            img.color = SpumAppearance.TryParseColor(hex, out var c) ? c : theme.slotBg;

            var btn = swatch.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition    = Selectable.Transition.None;
            btn.onClick.AddListener(() => { set(hex); Refresh(); });

            buttons.Add(img);
        }

        _refreshers.Add(() =>
        {
            string value = get() ?? "";
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] == null) continue;

                // The selected swatch is outlined by shrinking the others slightly —
                // a border image per swatch would double the object count of this row.
                bool selected = swatches[i].Hex == value;
                buttons[i].transform.localScale = selected ? Vector3.one : Vector3.one * 0.72f;

                if (selected) chosen.text = swatches[i].Name;
            }
        });
    }

    private void BuildRandomise(Transform parent, UITheme theme)
    {
        var btn = UIFactory.Button(parent, "SURPRISE ME", () =>
        {
            var random = SpumAppearance.Random();

            // Copied field by field rather than replacing the object: the caller holds
            // a reference to this look and would keep editing the old one.
            _look.bodyAddress     = random.bodyAddress;
            _look.hairAddress     = random.hairAddress;
            _look.faceHairAddress = random.faceHairAddress;
            _look.eyeAddress      = random.eyeAddress;

            Refresh();
        }, width: 0f);

        var le = btn.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 44f;
    }
}
