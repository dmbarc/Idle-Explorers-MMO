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
