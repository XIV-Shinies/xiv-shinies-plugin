using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// The server drops a category key it does not recognize and still answers 200, so a typo in a key
// fails nowhere else: the facts just never arrive. These tests spell out the keys
// docs/api-contract.md defines.
public class CategoryKeysTests
{
    // The contract's spelling for every category, keyed by `nameof` so renaming or deleting a
    // constant breaks the build. Compared as a whole map, so it fails for a key whose spelling drifts
    // and for a new key with no line here. A key declared `static readonly` instead of `const` is
    // missing from the reflected map, so it fails here too.
    private static readonly Dictionary<string, string> ContractSpellings = new()
    {
        [nameof(CategoryKeys.AchievementProgress)] = "achievementProgress",
        [nameof(CategoryKeys.Achievements)] = "achievements",
        [nameof(CategoryKeys.Items)] = "items",
        [nameof(CategoryKeys.Minions)] = "minions",
        [nameof(CategoryKeys.Mounts)] = "mounts",
        [nameof(CategoryKeys.OccultProgression)] = "occultProgression",
        [nameof(CategoryKeys.OccultRecords)] = "occultRecords",
        [nameof(CategoryKeys.OrchestrionRolls)] = "orchestrionRolls",
        [nameof(CategoryKeys.QuestSequences)] = "questSequences",
        [nameof(CategoryKeys.Quests)] = "quests",
        [nameof(CategoryKeys.TamedBeasts)] = "tamedBeasts",
        [nameof(CategoryKeys.TripleTriadCards)] = "tripleTriadCards",
        [nameof(CategoryKeys.TripleTriadNpcs)] = "tripleTriadNpcs",
    };

    [Fact]
    public void Every_key_matches_the_string_the_contract_defines()
    {
        Assert.Equal(ContractSpellings, CategoryKeyReflection.ByName());
    }

    // A shared key would collide in every dictionary the payload, kill switches and settings rows
    // use, and the second category would overwrite the first.
    [Fact]
    public void No_two_categories_share_a_key()
    {
        var keys = CategoryKeyReflection.All();

        // A wire key matches character for character, never by locale.
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // The contract's keys are camelCase, and a slip is invisible at runtime. The serializer's
    // DictionaryKeyPolicy lowercases a leading uppercase run, so a PascalCase key ships unlike its
    // constant; a separator in a lowercase key travels as is, and the server does not recognize it.
    [Fact]
    public void Every_key_is_camel_case()
    {
        foreach (var key in CategoryKeyReflection.All())
        {
            Assert.NotEmpty(key);

            // Also rejects the separators a first-letter check would pass: snake_case, kebab-case,
            // a space or padding.
            Assert.Matches("^[a-z][A-Za-z0-9]*$", key);
        }
    }
}
