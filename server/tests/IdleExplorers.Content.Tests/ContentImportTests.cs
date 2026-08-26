using System;
using System.IO;
using System.Linq;
using IdleExplorers.Content;
using Xunit;

namespace IdleExplorers.Content.Tests
{
    /// <summary>
    /// The server importing the game as it actually ships.
    ///
    /// Every assertion here is about one question: does the server read the same game
    /// the client reads? A loader that parses a fixture proves only that it compiles.
    /// These read Assets/StreamingAssets, so a content edit that breaks the server
    /// fails here rather than in a playtest.
    /// </summary>
    public class ContentImportTests
    {
        private static ContentFiles.Import Import() => ContentFiles.LoadFrom(StreamingAssets());

        /// <summary>
        /// Walks up from the test binary rather than hard-coding a path, so this runs
        /// on a build agent and on someone else's machine.
        /// </summary>
        private static string StreamingAssets()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Assets", "StreamingAssets");
                if (Directory.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"No Assets/StreamingAssets above {AppContext.BaseDirectory}");
        }

        // ── It loads at all ───────────────────────────────────────────────────

        [Fact]
        public void EveryFileParses()
        {
            var import = Import();

            Assert.Empty(import.Failures);
        }

        /// <summary>
        /// The one failure this whole layer exists to catch. System.Text.Json ignores
        /// public FIELDS unless IncludeFields is set, and the shared content classes
        /// are all fields -- so without it every file parses successfully into a
        /// catalogue of empty objects. Nothing throws. The server just quietly
        /// believes the game has no items in it.
        /// </summary>
        [Fact]
        public void ObjectsComeBackPopulatedRatherThanEmpty()
        {
            var goblin = Import().Catalogue.GetMonster("goblin");

            Assert.NotNull(goblin);
            Assert.Equal("Goblin", goblin.name);
            Assert.Equal(40d, goblin.maxHp);
            Assert.Equal(25L, goblin.xpReward);
            Assert.NotNull(goblin.lootTable);
            Assert.NotEmpty(goblin.lootTable);
        }

        [Fact]
        public void TheCatalogueHasTheContentTheGameShips()
        {
            var content = Import().Catalogue;

            Assert.True(content.Items.Count    > 50, $"only {content.Items.Count} items");
            Assert.True(content.Monsters.Count >= 2, $"only {content.Monsters.Count} monsters");
            Assert.True(content.Zones.Count    >= 2, $"only {content.Zones.Count} zones");
            Assert.True(content.Maps.Count     >= 2, $"only {content.Maps.Count} maps");
            Assert.NotEmpty(content.Skills);
            Assert.NotEmpty(content.Classes);
            Assert.NotEmpty(content.CraftRecipes);
            Assert.NotEmpty(content.ItemSets);
        }

        [Fact]
        public void TheTwoRootObjectsLoadToo()
        {
            var import = Import();

            // base_stats.json and shop_data.json are objects, not arrays -- the shape
            // Unity's array wrapper has to skip. Easy to forget on this side.
            Assert.NotNull(import.Catalogue.BaseStats);
            Assert.NotEmpty(import.Catalogue.CoinPacks);
        }

        // ── It is authored correctly ──────────────────────────────────────────

        /// <summary>
        /// Referential integrity across the whole catalogue. Every one of these
        /// failures is silent in game: a recipe naming a missing item produces
        /// nothing and says nothing, and a monster dropping a missing id simply never
        /// drops it. From inside the game they look like bad luck.
        /// </summary>
        [Fact]
        public void NothingReferencesSomethingThatDoesNotExist()
        {
            var problems = Import().Problems;

            Assert.True(problems.Count == 0,
                        "content problems:" + Environment.NewLine +
                        string.Join(Environment.NewLine, problems.Take(25)));
        }

        [Fact]
        public void TheRebalancedChestplateIsInThere()
        {
            var recipe = Import().Catalogue.GetRecipe("smith_tin_platebody");

            Assert.NotNull(recipe);
            Assert.Equal("tin_platebody", recipe.outputItemId);
            Assert.Contains(recipe.inputs, i => i.itemId == "tin_bar" && i.quantity == 2000L);
        }

        /// <summary>
        /// The negative case, without which the check above only proves that Validate
        /// returns an empty list. A clean catalogue and a validator that never looks
        /// at anything are indistinguishable from the outside.
        /// </summary>
        [Fact]
        public void ADanglingReferenceIsCaught()
        {
            using var scratch = new CopyOfTheContent();

            string recipes = Path.Combine(scratch.Path, "recipe_data.json");
            File.WriteAllText(recipes,
                File.ReadAllText(recipes).Replace("\"tin_ore\"", "\"tin_orr\""));

            var problems = ContentFiles.LoadFrom(scratch.Path).Problems;

            Assert.Contains(problems, p => p.Contains("tin_orr"));
        }

        // ── The version stamp ─────────────────────────────────────────────────

        [Fact]
        public void TheVersionIsStableAcrossImports()
        {
            Assert.Equal(Import().Version, Import().Version);
            Assert.Equal(16, Import().Version.Length);
        }

        /// <summary>
        /// A client whose catalogue disagrees with the server's must be told to
        /// re-download, so the stamp has to actually move when content does.
        /// </summary>
        [Fact]
        public void EditingAFileChangesTheVersion()
        {
            using var scratch = new CopyOfTheContent();

            string before = ContentFiles.LoadFrom(scratch.Path).Version;

            string touched = Path.Combine(scratch.Path, "monster_data.json");
            File.WriteAllText(touched, File.ReadAllText(touched).Replace("\"maxHp\": 40", "\"maxHp\": 41"));

            Assert.NotEqual(before, ContentFiles.LoadFrom(scratch.Path).Version);
        }

        /// <summary>A throwaway directory holding the real twelve files, free to corrupt.</summary>
        private sealed class CopyOfTheContent : IDisposable
        {
            public string Path { get; }

            public CopyOfTheContent()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                              "idle-explorers-content-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);

                foreach (string name in ContentFiles.FileNames)
                    File.Copy(System.IO.Path.Combine(StreamingAssets(), name),
                              System.IO.Path.Combine(Path, name));
            }

            public void Dispose() => Directory.Delete(Path, recursive: true);
        }

        [Fact]
        public void AMissingFileIsReportedRatherThanThrown()
        {
            string empty = Path.Combine(Path.GetTempPath(),
                                        "idle-explorers-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);

            try
            {
                var import = ContentFiles.LoadFrom(empty);

                Assert.Equal(ContentFiles.FileNames.Length, import.Failures.Count);
                Assert.False(import.IsClean);
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }
    }
}
