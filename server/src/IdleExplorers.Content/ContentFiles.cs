using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IdleExplorers.Rules;

namespace IdleExplorers.Content;

/// <summary>
/// The twelve authored files, read off disk into the shared catalogue.
///
/// ══ WHY THE SERVER READS THE SAME FILES THE GAME SHIPS ════════════════════════
///
/// It would be tidier to keep content in the database and export it to the client.
/// It would also mean two sources of truth during the entire migration, and a
/// balance change landing in one of them. The files stay authoritative; the server
/// imports them, seeds from them, and stamps a version so a client can be told when
/// its copy is stale.
///
/// ══ WHY System.Text.Json AND NOT JsonUtility ══════════════════════════════════
///
/// JsonUtility is Unity's and does not exist here. The two parsers agree on the only
/// thing that matters -- public fields, matched by name -- which is why the shared
/// content classes are written as plain fields rather than properties. IncludeFields
/// is what makes that work, and without it every object in the catalogue would
/// deserialise silently empty.
/// </summary>
public static class ContentFiles
{
    /// <summary>
    /// Ten of the files are bare root arrays; two are root objects.
    ///
    /// Unity wraps the arrays before parsing, because JsonUtility cannot read a
    /// top-level array at all. System.Text.Json can, so this side simply reads them
    /// -- but the SET of files, and which shape each one is, has to agree with
    /// ContentManager.LoadAll or the two hosts load different games.
    /// </summary>
    public static readonly string[] FileNames =
    {
        "base_stats.json",
        "class_data.json",
        "item_data.json",
        "merge_recipes.json",
        "monster_data.json",
        "recipe_data.json",
        "set_data.json",
        "shop_data.json",
        "skill_data.json",
        "slot_unlock.json",
        "spec_data.json",
        "zone_data.json",
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields               = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    /// <summary>Everything one import produced, including what was wrong with it.</summary>
    public sealed class Import
    {
        public GameContent Catalogue { get; init; } = new();

        /// <summary>
        /// A hash of the files as bytes, for the login payload.
        ///
        /// Over the raw bytes rather than the parsed objects, because a client cannot
        /// re-derive the parse but can absolutely re-derive the download. Two servers
        /// on the same commit produce the same string; any edit to any file changes
        /// it.
        /// </summary>
        public string Version { get; init; } = "";

        /// <summary>Dangling references and duplicate ids. Empty is the only good answer.</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

        /// <summary>Files that could not be read or parsed at all.</summary>
        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

        public bool IsClean => Problems.Count == 0 && Failures.Count == 0;
    }

    /// <summary>
    /// Reads every file in <paramref name="directory"/> and indexes it.
    ///
    /// A missing or unparseable file is recorded rather than thrown: an import that
    /// dies on the first bad file tells you about one problem per run, and content
    /// errors arrive in batches.
    /// </summary>
    public static Import LoadFrom(string directory)
    {
        var content  = new GameContent();
        var failures = new List<string>();
        var hash     = SHA256.Create();

        foreach (string fileName in FileNames)
        {
            string path = Path.Combine(directory, fileName);

            if (!File.Exists(path))
            {
                failures.Add($"{fileName}: not found");
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);

            // Name and bytes both, so renaming a file changes the version even if
            // its contents did not.
            byte[] name = Encoding.UTF8.GetBytes(fileName);
            hash.TransformBlock(name, 0, name.Length, null, 0);
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);

            try
            {
                Ingest(content, fileName, bytes);
            }
            catch (JsonException e)
            {
                failures.Add($"{fileName}: {e.Message}");
            }
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        string version = Convert.ToHexString(hash.Hash ?? Array.Empty<byte>()).ToLowerInvariant()[..16];

        return new Import
        {
            Catalogue = content,
            Version   = version,
            Problems  = content.Validate(),
            Failures  = failures,
        };
    }

    private static void Ingest(GameContent content, string fileName, byte[] bytes)
    {
        switch (fileName)
        {
            case "item_data.json":
                content.IngestItems(Read<ItemData[]>(bytes));
                break;

            case "monster_data.json":
                content.IngestMonsters(Read<MonsterData[]>(bytes));
                break;

            case "zone_data.json":
                content.IngestZones(Read<ZoneData[]>(bytes));
                break;

            case "skill_data.json":
                content.IngestSkills(Read<SkillData[]>(bytes));
                break;

            case "class_data.json":
                content.IngestClasses(Read<ClassData[]>(bytes));
                break;

            case "recipe_data.json":
                content.IngestCraftRecipes(Read<CraftRecipe[]>(bytes));
                break;

            case "merge_recipes.json":
                content.IngestMergeRecipes(Read<MergeRecipe[]>(bytes));
                break;

            case "slot_unlock.json":
                content.IngestSlotUnlocks(Read<SlotUnlockRequirement[]>(bytes));
                break;

            case "set_data.json":
                content.IngestItemSets(Read<ItemSetData[]>(bytes));
                break;

            case "spec_data.json":
                content.IngestSpecCombos(Read<SpecCombo[]>(bytes));
                break;

            // The two root objects.
            case "base_stats.json":
                content.IngestBaseStats(Read<StatBlock>(bytes));
                break;

            case "shop_data.json":
                content.IngestShop(Read<ShopCatalog>(bytes));
                break;

            default:
                throw new JsonException($"no importer for '{fileName}'");
        }
    }

    private static T? Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options);
}
