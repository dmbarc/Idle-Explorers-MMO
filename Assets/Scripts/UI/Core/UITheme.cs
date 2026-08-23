using UnityEngine;
using TMPro;

/// <summary>
/// Central color + typography theme. Place a UITheme.asset in Assets/Resources/
/// so UIManager can load it with Resources.Load&lt;UITheme&gt;("UITheme").
/// All UIFactory calls read from this — change once, updates everywhere.
/// </summary>
[CreateAssetMenu(fileName = "UITheme", menuName = "IdleExplorers/UITheme")]
public class UITheme : ScriptableObject
{
    [Header("Background Colors")]
    public Color panelBg        = new Color(0.10f, 0.08f, 0.12f, 0.95f);  // deep purple-black
    public Color cardBg         = new Color(0.15f, 0.12f, 0.18f, 1.00f);
    public Color headerBg       = new Color(0.08f, 0.06f, 0.10f, 1.00f);
    public Color overlayBg      = new Color(0.00f, 0.00f, 0.00f, 0.75f);

    [Header("Accent Colors")]
    public Color accentGold     = new Color(1.00f, 0.78f, 0.20f, 1.00f);
    public Color accentBlue     = new Color(0.25f, 0.60f, 1.00f, 1.00f);
    public Color accentGreen    = new Color(0.20f, 0.85f, 0.40f, 1.00f);
    public Color accentRed      = new Color(0.90f, 0.25f, 0.25f, 1.00f);
    public Color accentPurple   = new Color(0.65f, 0.30f, 1.00f, 1.00f);

    [Header("Text Colors")]
    public Color textPrimary    = new Color(0.95f, 0.90f, 0.80f, 1.00f);  // warm off-white
    public Color textSecondary  = new Color(0.65f, 0.60f, 0.55f, 1.00f);  // muted
    public Color textDisabled   = new Color(0.40f, 0.38f, 0.35f, 1.00f);

    [Header("Button")]
    public Color buttonNormal   = new Color(0.22f, 0.18f, 0.28f, 1.00f);
    public Color buttonHover    = new Color(0.30f, 0.25f, 0.38f, 1.00f);
    public Color buttonPressed  = new Color(0.15f, 0.12f, 0.20f, 1.00f);

    [Header("HP / MP Bars")]
    public Color hpFill         = new Color(0.85f, 0.20f, 0.20f, 1.00f);
    public Color mpFill         = new Color(0.20f, 0.45f, 0.90f, 1.00f);
    public Color xpFill         = new Color(1.00f, 0.78f, 0.20f, 1.00f);  // matches accentGold
    public Color barBg          = new Color(0.08f, 0.08f, 0.10f, 1.00f);

    [Header("Slot")]
    public Color slotBg         = new Color(0.12f, 0.10f, 0.15f, 1.00f);
    public Color slotBorder     = new Color(0.35f, 0.30f, 0.40f, 1.00f);
    public Color slotHighlight  = new Color(1.00f, 0.78f, 0.20f, 1.00f);  // matches accentGold

    [Header("Sprites (optional 9-sliced art)")]
    // Every one of these is optional. UIFactory applies a sprite only when the theme
    // supplies one and otherwise keeps the flat colour above, so the art pass can
    // never break a layout — an unassigned field simply looks like it did before.
    //
    // Assign via: Idle Explorers → Apply UI Sprite Theme
    public Sprite panelSprite;
    public Sprite cardSprite;
    public Sprite headerSprite;
    public Sprite buttonSprite;
    /// <summary>Optional. When set, buttons use SpriteSwap instead of colour shading.</summary>
    public Sprite buttonPressedSprite;
    public Sprite slotSprite;
    public Sprite barBgSprite;
    public Sprite barFillSprite;
    public Sprite dividerSprite;
    public Sprite inputSprite;

    [Header("Sprite tints")]
    // Multiplied over the sprite art above.
    //
    // Every UI panel Kenney ships is a mid-tone tan, beige or grey — there is no dark
    // variant in any of the packs. Drawn at full white the beige card sprite fills most
    // of the screen with bright khaki, which is what made the UI painful to look at.
    // Tinting keeps the painted grain, bevels and rivets of the artwork and moves the
    // whole thing into a dark blue, which is a thing multiply CAN do: the tints below
    // are blue-dominant, so blue survives the multiply while red and green are cut.
    //
    // Chrome goes blue; buttons stay wood, so they read as the thing you press.
    // Set a tint to white to see the art untouched.
    public Color panelSpriteTint  = new Color(0.20f, 0.38f, 0.92f, 1.00f);
    public Color cardSpriteTint   = new Color(0.22f, 0.32f, 0.62f, 1.00f);
    public Color headerSpriteTint = new Color(0.20f, 0.27f, 0.46f, 1.00f);
    public Color slotSpriteTint   = new Color(0.18f, 0.26f, 0.50f, 1.00f);
    public Color inputSpriteTint  = new Color(0.24f, 0.32f, 0.55f, 1.00f);
    public Color buttonSpriteTint = new Color(0.72f, 0.62f, 0.52f, 1.00f);
    public Color barBgSpriteTint  = new Color(0.30f, 0.36f, 0.52f, 1.00f);
    public Color dividerSpriteTint = new Color(0.75f, 0.68f, 0.52f, 1.00f);

    [Header("Typography")]
    public TMP_FontAsset font;      // assign LiberationSans SDF or custom font in Inspector
    public float fontSizeTitle  = 32f;
    public float fontSizeBody   = 18f;
    public float fontSizeSmall  = 14f;
    public float fontSizeLabel  = 12f;

    [Header("Sizing")]
    public float buttonHeight   = 52f;  // ≥ 48px for touch targets
    public float slotSize       = 64f;
    public float iconSize       = 48f;
    public float cornerRadius   = 8f;
    public float padding        = 12f;
    public float spacing        = 8f;
}
