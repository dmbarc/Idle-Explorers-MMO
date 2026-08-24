using TMPro;
using UnityEngine;

/// <summary>
/// What a character just said, floating above their head.
///
/// Built in code with a world-space TextMeshPro, the same way damage numbers and node
/// labels are — no prefab, no canvas, and nothing for the map builder to strip when it
/// regenerates a scene.
///
/// One bubble per speaker. Saying something while the last line is still up replaces
/// it rather than stacking a second bubble over the first, which is what a chat line
/// actually means: this is the most recent thing they said.
/// </summary>
public class ChatBubble : MonoBehaviour
{
    /// <summary>Height above the speaker's origin. Clear of the damage numbers at 1.8.</summary>
    private const float HeightAboveSpeaker = 2.4f;

    private const float MinLifetime  = 3.5f;
    private const float PerCharacter = 0.06f;
    private const float MaxLifetime  = 9f;

    /// <summary>Fraction of the lifetime spent fading out at the end.</summary>
    private const float FadeFraction = 0.25f;

    /// <summary>
    /// Longer than this and a bubble stops being a speech bubble and starts being a
    /// wall between the player and their own character.
    /// </summary>
    public const int MaxMessageLength = 120;

    private static readonly Color TextColor = new Color(1f, 0.98f, 0.90f);

    private TMP_Text _label;
    private float    _bornAt;
    private float    _lifetime;

    /// <summary>
    /// Puts a line of text above a character. Returns null for an empty message, so a
    /// stray Enter does not spawn an invisible bubble that has to time out.
    /// </summary>
    public static ChatBubble Say(Transform speaker, string message)
    {
        if (speaker == null) return null;

        message = Sanitise(message);
        if (string.IsNullOrEmpty(message)) return null;

        // Replace rather than stack. Parented to the speaker so it walks with them,
        // which is also how Destroy on the character takes the bubble with it.
        var existing = speaker.GetComponentInChildren<ChatBubble>(includeInactive: true);
        if (existing != null) Destroy(existing.gameObject);

        var go = new GameObject("ChatBubble");
        go.transform.SetParent(speaker, worldPositionStays: false);

        // A SPUM rig root can be a RectTransform, which re-derives its local x and y
        // from anchoredPosition — so localPosition is set through the transform that
        // was just created, never assumed to have survived a parenting.
        go.transform.localPosition = new Vector3(0f, HeightAboveSpeaker, 0f);
        go.transform.localRotation = Quaternion.identity;

        var label = go.AddComponent<TextMeshPro>();

        // <mark> draws a translucent slab behind the glyphs, which is the whole
        // bubble — a separate quad would need its own material, its own sorting and
        // its own sizing against text that has not been laid out yet.
        label.text          = $"<mark=#12182CCC>{message}</mark>";
        label.fontSize      = 3.2f;
        label.alignment     = TextAlignmentOptions.Center;
        label.color         = TextColor;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.GetComponent<RectTransform>().sizeDelta = new Vector2(8f, 3f);

        go.AddComponent<Billboard>();

        var bubble = go.AddComponent<ChatBubble>();
        bubble._label    = label;
        bubble._bornAt   = Time.time;
        bubble._lifetime = Mathf.Min(MaxLifetime, MinLifetime + message.Length * PerCharacter);

        Destroy(go, bubble._lifetime);
        return bubble;
    }

    void Update()
    {
        if (_label == null) return;

        float age  = Time.time - _bornAt;
        float fade = Mathf.InverseLerp(_lifetime * (1f - FadeFraction), _lifetime, age);

        _label.color = new Color(TextColor.r, TextColor.g, TextColor.b, 1f - fade);

        // The mark slab is a separate colour from the text and does not follow
        // label.color, so it is faded through the alpha the renderer applies to
        // everything the mesh contains.
        _label.alpha = 1f - fade;
    }

    /// <summary>
    /// Trims, caps the length, and removes the angle brackets.
    ///
    /// TextMeshPro parses rich-text tags out of whatever it is given, so a player who
    /// typed "&lt;size=400&gt;" would get exactly that — and one who typed an unclosed
    /// tag would silently reformat every bubble drawn after theirs. &lt;noparse&gt;
    /// is not a defence, because the escape from it is a literal string a player can
    /// type. Removing the characters entirely is the only version of this with no
    /// remaining case to think about, and chat loses nothing that matters.
    /// </summary>
    private static string Sanitise(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        message = message.Replace('<', ' ').Replace('>', ' ').Trim();
        if (message.Length == 0) return null;

        return message.Length > MaxMessageLength
            ? message.Substring(0, MaxMessageLength)
            : message;
    }
}
