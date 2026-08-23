using UnityEngine;

/// <summary>
/// Draws the character themselves — body, hair, face hair, eyes, held weapon, cape —
/// underneath whatever equipment CharacterAppearance draws on top.
///
/// Two components rather than one because they answer to different data and change at
/// different times: equipment changes constantly and comes from the inventory,
/// appearance changes rarely and comes from the save. Merging them would mean every
/// ring swap re-resolved the whole body.
///
/// They overlap on exactly two renderers. The boots slot writes to _3L_Foot and
/// _12R_Foot, which are also where the body sheet's bare feet live — that is correct
/// for equipment (boots replace feet) but it means this component has to tell
/// CharacterAppearance to forget its cached "original" sprites whenever the body
/// changes. Otherwise taking boots off after a race change restores the OLD race's
/// feet, and the character ends up with legs from two different bodies.
/// </summary>
[DisallowMultipleComponent]
public class CharacterBaseAppearance : MonoBehaviour
{
    private SpumSaveData _applied;

    void OnEnable()
    {
        GameEvents.OnAppearanceChanged += Refresh;
        Refresh();
    }

    void OnDisable() => GameEvents.OnAppearanceChanged -= Refresh;

    /// <summary>Redraws from the active character's saved look.</summary>
    public void Refresh()
    {
        var character = CharacterManager.Current;
        if (character == null) return;

        // A character created before appearance existed carries an empty look rather
        // than a broken one — dress them in the default instead of stripping the rig.
        if (character.spumConfig == null || character.spumConfig.IsEmpty)
            character.spumConfig = SpumAppearance.Default();

        Apply(character.spumConfig);
    }

    /// <summary>Draws a specific look. Used by the editor for live preview on the player.</summary>
    public void Apply(SpumSaveData look)
    {
        if (look == null) return;

        int written = SpumAppearance.Apply(transform, look);
        if (written == 0)
        {
            Debug.LogWarning("[CharacterBaseAppearance] Nothing was drawn for this look — " +
                             "check the addresses resolve under a Resources folder. " +
                             "Run: Idle Explorers → Validate Equipment Art.");
            return;
        }

        _applied = look.Clone();

        // The body just overwrote the feet, which the boots slot also owns. Tell the
        // equipment layer to re-read what it considers "original", then let it redraw
        // on top of the new body.
        var equipment = GetComponent<CharacterAppearance>();
        if (equipment != null)
        {
            equipment.InvalidateLayerCache();
            equipment.Refresh();
        }
    }

    /// <summary>The look currently drawn, for a preview that wants to start from it.</summary>
    public SpumSaveData Current => _applied;
}
