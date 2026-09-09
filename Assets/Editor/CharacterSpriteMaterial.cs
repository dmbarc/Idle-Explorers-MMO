using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The material that keeps a character visible in front of the world.
///
/// A SPUM character is a flat sprite standing in a 3D scene, and a sprite is depth
/// tested like anything else — so walking up to a copper rock to mine it buries the
/// character's legs inside the rock. The shader this material uses draws with
/// ZTest Always; see Assets/Shaders/CharacterSprite.shader for why that is the right
/// trade for a fixed-angle camera with one character in the middle of it.
///
/// Created as a real asset rather than at runtime, because the prefabs have to
/// reference it and a material made in memory does not survive being saved into one.
/// </summary>
public static class CharacterSpriteMaterial
{
    private const string SHADER_NAME   = "Idle Explorers/Character Sprite";
    private const string MATERIAL_PATH = "Assets/Materials/CharacterSprite.mat";

    /// <summary>
    /// The material, created on first use. Null when the shader is missing or the
    /// platform cannot run it — callers must treat that as "leave the rig alone".
    /// </summary>
    public static Material Ensure()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Material>(MATERIAL_PATH);
        if (existing != null && existing.shader != null && existing.shader.isSupported)
            return existing;

        var shader = Shader.Find(SHADER_NAME);

        // isSupported as well as null. A shader with a compile error still resolves by
        // name, and assigning it paints every character bright magenta — which is a
        // far worse outcome than the clipping this is meant to fix.
        if (shader == null || !shader.isSupported)
        {
            Debug.LogError($"[CharacterSprite] Shader '{SHADER_NAME}' is missing or will not " +
                           "compile, so character sprites keep the default material and will " +
                           "still clip into props. Check Assets/Shaders/CharacterSprite.shader.");
            return null;
        }

        if (existing != null)
        {
            existing.shader = shader;
            EditorUtility.SetDirty(existing);
            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MATERIAL_PATH));

        var material = new Material(shader) { name = "CharacterSprite" };
        AssetDatabase.CreateAsset(material, MATERIAL_PATH);
        AssetDatabase.SaveAssets();

        Debug.Log($"[CharacterSprite] Created {MATERIAL_PATH}.");
        return material;
    }

    /// <summary>
    /// Puts that material on every sprite in a rig. Returns how many it changed.
    ///
    /// Silently does nothing when the material could not be made, so a shader problem
    /// costs the depth fix and not the character.
    /// </summary>
    public static int ApplyTo(GameObject rig)
    {
        if (rig == null) return 0;

        var material = Ensure();
        if (material == null) return 0;

        int changed = 0;
        foreach (var renderer in rig.GetComponentsInChildren<SpriteRenderer>(includeInactive: true))
        {
            if (renderer == null || renderer.sharedMaterial == material) continue;

            renderer.sharedMaterial = material;
            changed++;
        }

        return changed;
    }
}
