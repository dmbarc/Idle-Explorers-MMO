using System;

// ═══════════════════════════════════════════════════════════════════════════════
//  APPEARANCE — how a character looks, underneath whatever they are wearing.
//
//  Shared rather than save-side because both halves need it: ClassData.previewLook
//  dresses the character-select cards (content), and CharacterData.spumConfig is the
//  player's own body (state the server will own). One definition serves both.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// How a character looks, underneath whatever they are wearing.
///
/// This used to be eight ints indexed into "SPUM's sprite lists", with a comment
/// saying they were passed to SPUM's PlayerObj at runtime. Neither half was true:
/// SPUM ships no runtime appearance API at all — no script in the package assigns a
/// sprite — and nothing in this project ever read the fields. They were written
/// all-zero at character creation and that was the end of it.
///
/// So it is redefined as Resources addresses, which is what the rig actually needs.
/// The format mirrors the manifest SPUM already bakes into each prefab
/// (SPUM_Prefabs.ImageElement), where an ItemPath plus a Structure name resolves
/// directly through SpriteLoader. Indices would have broken the moment a sprite pack
/// was reordered; addresses survive it.
///
/// Colours are hex strings rather than UnityEngine.Color, because JsonUtility writes
/// a Color as four floats and this has to stay legible in account.json.
/// </summary>
[Serializable]
public class SpumSaveData
{
    /// <summary>Body sheet — supplies head, torso, both arms and both feet.</summary>
    public string bodyAddress;

    public string hairAddress;
    public string faceHairAddress;
    public string eyeAddress;

    /// <summary>Held weapon. Cosmetic only — it is not the equipment system.</summary>
    public string weaponAddress;

    /// <summary>Cape or quiver behind the character, under any equipped cape.</summary>
    public string backAddress;

    // "#RRGGBB", or empty for the sprite's own colours.
    public string hairColor;
    public string eyeColor;
    public string skinTint;

    /// <summary>True when this has never been filled in — an old save, or a default.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(bodyAddress) &&
                           string.IsNullOrEmpty(hairAddress) &&
                           string.IsNullOrEmpty(eyeAddress);

    public SpumSaveData Clone() => (SpumSaveData)MemberwiseClone();
}
