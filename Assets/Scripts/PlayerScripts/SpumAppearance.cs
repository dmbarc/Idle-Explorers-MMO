using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Applies a SpumSaveData look to any SPUM rig, and lists what a player may choose.
///
/// Static and rig-agnostic on purpose: the same code dresses the live player, the
/// character-creation preview and the class-card previews. Writing it three times is
/// how they would end up disagreeing about what "Human_1" looks like.
///
/// SPUM itself supplies none of this. A search of the whole package finds no script
/// that ever assigns a sprite at runtime — the appearance tooling is editor-only and
/// is not in this project. What SPUM does supply is the addressing convention, which
/// this follows exactly: a sheet path plus a sub-sprite name, resolved by SpriteLoader.
/// </summary>
public static class SpumAppearance
{
    private const string Root = "Addons/Legacy/0_Unit/0_Sprite/";

    // ── The catalogue ─────────────────────────────────────────────────────────
    //
    // Addresses rather than a folder scan, because Resources cannot be enumerated at
    // runtime and a build would silently offer nothing. Verified present on disk.

    /// <summary>
    /// Body sheets. Every one is cut into the same six sub-sprites (Head, Body,
    /// Arm_L, Arm_R, Foot_L, Foot_R), which is why changing race is a six-renderer
    /// write on one rig rather than swapping to a different prefab — and why the four
    /// Orc bodies are selectable even though SPUM ships no Orc prefab.
    /// </summary>
    public static readonly (string Address, string Name)[] Bodies =
    {
        (Root + "1_Body/Human_1",  "Human"),
        (Root + "1_Body/Human_2",  "Human, Tanned"),
        (Root + "1_Body/Human_3",  "Human, Pale"),
        (Root + "1_Body/Human_4",  "Human, Dark"),
        (Root + "1_Body/Human_5",  "Human, Weathered"),
        (Root + "1_Body/Elf_1",    "Elf"),
        (Root + "1_Body/Elf_2",    "Elf, Dusk"),
        (Root + "1_Body/Orc_1",    "Orc"),
        (Root + "1_Body/Orc_2",    "Orc, Mossgreen"),
        (Root + "1_Body/Orc_3",    "Orc, Ashen"),
        (Root + "1_Body/Orc_4",    "Orc, Bloodtusk"),
        (Root + "1_Body/Devil_1",  "Infernal"),
        (Root + "1_Body/Skelton_1","Revenant"),
    };

    public static readonly (string Address, string Name)[] Hairs =
    {
        ("", "Bald"),
        (Root + "0_Hair/Hair_1", "Cropped"),
        (Root + "0_Hair/Hair_2", "Swept"),
        (Root + "0_Hair/Hair_3", "Braided"),
        (Root + "0_Hair/Hair_4", "Long"),
        (Root + "0_Hair/Hair_5", "Tousled"),
        (Root + "0_Hair/Hair_6", "Topknot"),
        (Root + "0_Hair/Hair_7", "Wild"),
        (Root + "0_Hair/Hair_8", "Shorn"),
        (Root + "0_Hair/Hair_9", "Flowing"),
    };

    public static readonly (string Address, string Name)[] FaceHairs =
    {
        ("", "Clean-shaven"),
        (Root + "1_FaceHair/FaceHair_1", "Stubble"),
        (Root + "1_FaceHair/FaceHair_2", "Moustache"),
        (Root + "1_FaceHair/FaceHair_3", "Goatee"),
        (Root + "1_FaceHair/FaceHair_4", "Full Beard"),
        (Root + "1_FaceHair/FaceHair_5", "Braided Beard"),
    };

    /// <summary>
    /// Eye sheets. Eye_Close is deliberately absent — it is the blink frame the rig
    /// swaps to, not a look, and offering it would let a player choose to be
    /// permanently asleep.
    /// </summary>
    public static readonly (string Address, string Name)[] Eyes =
    {
        (Root + "0_Eye/Eye",   "Steady"),
        (Root + "0_Eye/Eye0",  "Wide"),
        (Root + "0_Eye/Eye1",  "Narrow"),
        (Root + "0_Eye/Eye2",  "Sharp"),
        (Root + "0_Eye/Eye3",  "Tired"),
        (Root + "0_Eye/Eye4",  "Fierce"),
        (Root + "0_Eye/Eye5",  "Gentle"),
        (Root + "0_Eye/Eye6",  "Sly"),
        (Root + "0_Eye/Eye7",  "Hollow"),
        (Root + "0_Eye/Eye8",  "Bright"),
        (Root + "0_Eye/Eye9",  "Heavy"),
        (Root + "0_Eye/Eye10", "Piercing"),
        (Root + "0_Eye/Eye11", "Distant"),
        (Root + "0_Eye/Eye16", "Burning"),
    };

    public static readonly (string Address, string Name)[] Weapons =
    {
        ("", "Empty-handed"),
        (Root + "6_Weapons/0_Sword/Sword_1", "Shortsword"),
        (Root + "6_Weapons/0_Sword/Sword_2", "Arming Sword"),
        (Root + "6_Weapons/0_Sword/Sword_3", "Broadsword"),
        (Root + "6_Weapons/0_Sword/Sword_4", "Sabre"),
        (Root + "6_Weapons/0_Sword/Sword_5", "Falchion"),
        (Root + "6_Weapons/0_Sword/Sword_6", "Greatsword"),
        (Root + "6_Weapons/1_Axe/Axe_1",     "Hand Axe"),
        (Root + "6_Weapons/2_Bow/Bow_1",     "Shortbow"),
        (Root + "6_Weapons/4_Spear/Spear_1", "Spear"),
        (Root + "6_Weapons/5_Wand/Ward_1",   "Wand"),
        (Root + "6_Weapons/6_Hammer/F_SR_Hammer", "Smith's Hammer"),
    };

    public static readonly (string Address, string Name)[] Backs =
    {
        ("", "Nothing"),
        (Root + "7_Back/Back_1",    "Travelling Cloak"),
        (Root + "7_Back/Back_2",    "Tattered Cape"),
        (Root + "7_Back/Back_3",    "Long Mantle"),
        (Root + "7_Back/BowBack_1", "Quiver"),
    };

    // ── Defaults ──────────────────────────────────────────────────────────────

    /// <summary>A plain starting look, for a character created before this existed.</summary>
    public static SpumSaveData Default() => new SpumSaveData
    {
        bodyAddress     = Bodies[0].Address,
        hairAddress     = Hairs[1].Address,
        faceHairAddress = "",
        eyeAddress      = Eyes[0].Address,
        weaponAddress   = "",
        backAddress     = "",
    };

    /// <summary>A random look, for the creation screen's randomise button.</summary>
    public static SpumSaveData Random()
    {
        static string Pick((string Address, string Name)[] set) =>
            set[UnityEngine.Random.Range(0, set.Length)].Address;

        return new SpumSaveData
        {
            bodyAddress     = Pick(Bodies),
            hairAddress     = Pick(Hairs),
            faceHairAddress = Pick(FaceHairs),
            eyeAddress      = Pick(Eyes),
            weaponAddress   = "",
            backAddress     = "",
        };
    }

    /// <summary>Index of an address within a catalogue, or 0 when it is not in it.</summary>
    public static int IndexOf((string Address, string Name)[] set, string address)
    {
        for (int i = 0; i < set.Length; i++)
            if (set[i].Address == (address ?? "")) return i;
        return 0;
    }

    // ── Applying ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Which rig parts each piece of the look writes to.
    ///
    /// The ancestor qualifier is load-bearing: P_Arm and P_Weapon each appear twice,
    /// once per arm, so an unqualified lookup would dress whichever the depth-first
    /// search happened to reach first.
    /// </summary>
    private static readonly (string Part, string Ancestor, string Sub)[] BodyParts =
    {
        ("P_Body",  null,      "Body"),
        ("P_Head",  "HeadSet", "Head"),
        ("P_Arm",   "P_LArm",  "Arm_L"),
        ("P_Arm",   "P_RArm",  "Arm_R"),
        ("P_LFoot", null,      "Foot_L"),
        ("P_RFoot", null,      "Foot_R"),
    };

    /// <summary>
    /// Hides every layer the equipment system owns, leaving the character in nothing
    /// but their own skin.
    ///
    /// ══ WHY THE CHARACTER WAS ALWAYS WEARING ARMOUR ═══════════════════════════
    ///
    /// SPUM's prefabs ship dressed. Apply writes body, hair, eyes, weapon and cloak,
    /// and used to leave the rig's own helmet and cloth exactly where they were — so
    /// the appearance editor showed a hooded figure in plate whatever you chose, and
    /// all five class cards showed the same one. There was no "naked" option to pick
    /// because nothing ever took the clothes off.
    ///
    /// ══ WHY THIS DISABLES RENDERERS INSTEAD OF CLEARING SPRITES ═══════════════
    ///
    /// SpumRig.Layer.WasDrawn is `OriginalSprite != null`, and CharacterAppearance
    /// refuses to draw an item onto a layer the rig never drew — that check is what
    /// stopped the tin helmet rendering behind the rig's own horns. Nulling the
    /// sprites here would make every one of those layers look "never drawn", and worn
    /// equipment would stop appearing on the live player entirely.
    ///
    /// Returns how many layers were hidden.
    /// </summary>
    public static int Undress(Transform rig)
    {
        if (rig == null) return 0;

        int hidden = 0;

        foreach (var slot in EquipmentSlots.All)
        {
            if (!slot.RendersOnCharacter) continue;

            foreach (var part in slot.SpumParts)
            {
                foreach (var layer in SpumRig.Collect(rig, part))
                {
                    if (layer.Renderer == null) continue;
                    layer.Renderer.enabled = false;
                    hidden++;
                }
            }
        }

        return hidden;
    }

    /// <summary>
    /// Recolours a rig's skin without touching anything else it is wearing.
    ///
    /// For monsters built from a human-shaped rig: a green elf reads as something
    /// grown rather than as an elf. Tinting every renderer would take the eyes and
    /// the weapon with it, which reads as a lighting bug.
    /// </summary>
    public static int TintBody(Transform rig, string hex)
    {
        if (rig == null || string.IsNullOrEmpty(hex)) return 0;

        int tinted = 0;
        foreach (var (part, ancestor, _) in BodyParts)
        {
            foreach (var layer in SpumRig.Collect(rig, part, ancestor))
            {
                if (layer.Renderer == null) continue;
                Tint(layer.Renderer, hex);
                tinted++;
            }
        }
        return tinted;
    }

    /// <summary>
    /// Draws a look onto a rig. Safe to call repeatedly and on any SPUM prefab.
    ///
    /// Returns the number of renderers actually written, so callers can tell a look
    /// that resolved from one that silently did nothing.
    /// </summary>
    public static int Apply(Transform rig, SpumSaveData look)
    {
        if (rig == null) return 0;
        look ??= Default();

        int written = 0;

        // Strip the rig's shipped clothing FIRST. The body write immediately below is
        // what puts the bare feet back: P_LFoot and P_RFoot are the boots slot and the
        // body's own feet at the same time, so undressing takes the feet off with the
        // boots and only the body restores them.
        Undress(rig);

        // ── Body: one sheet, six renderers ────────────────────────────────────
        //
        // Never skipped. A look with no body address would leave the character with
        // no feet, because Undress has just hidden them.
        string bodyAddress = string.IsNullOrEmpty(look.bodyAddress)
            ? Default().bodyAddress
            : look.bodyAddress;

        {
            foreach (var (part, ancestor, sub) in BodyParts)
            {
                // Always an explicit sub-sprite. A bare path would fall through
                // SpriteLoader's "Body" fallback and put the torso on the feet.
                var sprite = SpriteLoader.Load(bodyAddress + "#" + sub);
                if (sprite == null) continue;

                foreach (var layer in SpumRig.Collect(rig, part, ancestor))
                {
                    SpumRig.Show(layer, sprite);
                    Tint(layer.Renderer, look.skinTint);
                    written++;
                }
            }
        }

        written += ApplySingle(rig, "P_Hair",     null,     look.hairAddress,     look.hairColor);
        written += ApplySingle(rig, "P_Mustache", null,     look.faceHairAddress, look.hairColor);
        written += ApplySingle(rig, "P_Weapon",   "P_RArm", look.weaponAddress,   null);
        written += ApplySingle(rig, "P_Back",     null,     look.backAddress,     null);

        // ── Eyes: two parts, each with a Front and a Back renderer ────────────
        if (!string.IsNullOrEmpty(look.eyeAddress))
        {
            foreach (var part in new[] { "P_LEye", "P_REye" })
            {
                foreach (var layer in SpumRig.Collect(rig, part))
                {
                    // The renderer's own name is Front or Back, which is exactly the
                    // sub-sprite name on the eye sheet.
                    var sprite = SpriteLoader.Load(look.eyeAddress + "#" + layer.Renderer.name);
                    if (sprite == null) continue;

                    SpumRig.Show(layer, sprite);
                    Tint(layer.Renderer, look.eyeColor);
                    written++;
                }
            }
        }

        return written;
    }

    /// <summary>
    /// One address onto one part. An EMPTY address hides the part rather than leaving
    /// the previous look behind — that is what makes "Bald" and "Empty-handed" real
    /// choices instead of no-ops.
    /// </summary>
    private static int ApplySingle(Transform rig, string part, string ancestor,
                                    string address, string colorHex)
    {
        var layers = SpumRig.Collect(rig, part, ancestor);
        if (layers.Count == 0) return 0;

        if (string.IsNullOrEmpty(address))
        {
            foreach (var layer in layers) layer.Renderer.enabled = false;
            return 0;
        }

        int written = 0;
        foreach (var layer in layers)
        {
            var sprite = SpriteLoader.Load(address, layer.Side);
            if (sprite == null) continue;

            SpumRig.Show(layer, sprite);
            Tint(layer.Renderer, colorHex);
            written++;
        }
        return written;
    }

    /// <summary>
    /// Hides hair under a helmet, and puts it back when the helmet comes off.
    ///
    /// SPUM draws P_Hair and P_Helmet as separate layers with the helmet in front, so
    /// a character in a full helm wore their hair sticking out through the metal. The
    /// rig has no notion of one part occluding another — sorting order is all it has —
    /// so somebody has to decide, and this is the whole of that decision.
    ///
    /// It takes the hair address rather than reading the save, because it is called
    /// from previews as well as from the world character, and a preview is showing a
    /// look that may not be the saved one. It is also what makes "Bald" survive taking
    /// a helmet off: an empty address means there was never any hair to restore, and
    /// re-enabling the renderer would show whatever sprite it happened to be holding.
    ///
    /// Returns the number of renderers changed, so a caller can tell "no hair to hide"
    /// from "this is not a SPUM rig".
    /// </summary>
    public static int ApplyHelmetRule(Transform rig, string hairAddress, bool helmetWorn)
    {
        if (rig == null) return 0;

        bool show = !helmetWorn && !string.IsNullOrEmpty(hairAddress);

        int touched = 0;
        foreach (var layer in SpumRig.Collect(rig, "P_Hair"))
        {
            if (layer.Renderer == null) continue;

            layer.Renderer.enabled = show;
            touched++;
        }

        return touched;
    }

    private static void Tint(SpriteRenderer renderer, string hex)
    {
        if (renderer == null) return;

        // No tint means the sprite's own colours, not white-on-white.
        renderer.color = TryParseColor(hex, out var color) ? color : Color.white;
    }

    /// <summary>"#RRGGBB" or "RRGGBB". Returns false for empty or malformed input.</summary>
    public static bool TryParseColor(string hex, out Color color)
    {
        color = Color.white;
        if (string.IsNullOrEmpty(hex)) return false;

        string s = hex[0] == '#' ? hex : "#" + hex;
        return ColorUtility.TryParseHtmlString(s, out color);
    }

    public static string ToHex(Color color) => "#" + ColorUtility.ToHtmlStringRGB(color);

    /// <summary>
    /// Swatches offered for hair and eyes. A fixed set rather than a colour wheel:
    /// the sprites are small and low-contrast, and most of a continuous picker's range
    /// produces something indistinguishable from its neighbour at this size.
    /// </summary>
    public static readonly (string Hex, string Name)[] Swatches =
    {
        ("",        "Natural"),
        ("#2B2118", "Black"),
        ("#5A3A22", "Brown"),
        ("#C08A3E", "Blonde"),
        ("#A33B22", "Auburn"),
        ("#8C8C93", "Silver"),
        ("#E8E4D8", "White"),
        ("#3E6B8C", "Cobalt"),
        ("#5E3B7A", "Violet"),
        ("#2F7A4F", "Verdant"),
        ("#A32B4E", "Crimson"),
    };
}
