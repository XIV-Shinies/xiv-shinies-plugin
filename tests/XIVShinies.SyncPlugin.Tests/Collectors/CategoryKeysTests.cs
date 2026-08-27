using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Category keys are wire strings, and the server treats one it does not recognize as absent: it
// strips the payload key and reports nothing. A typo therefore has no observable failure anywhere
// — the upload still returns 200, the plugin still says it synced, and the facts quietly never
// arrive. These tests give that typo somewhere to fail, by spelling out the keys the API contract
// defines (docs/api-contract.md) beside the constants the collectors announce.
public class CategoryKeysTests
{
    // The contract's spelling for every category, keyed by the constant that declares it.
    //
    // Compared as a whole map, so equality fails in both directions — a key whose spelling drifts,
    // and a key nobody added a line for. A per-key assertion list would pass green after a new
    // category arrived with its spelling unpinned.
    //
    // It also covers a subtler slip. CategoryKeyReflection reads compile-time constants only, so a
    // key declared `static readonly` instead of `const` is missing from the reflected map and fails
    // here rather than quietly escaping every test in this file.
    private static readonly Dictionary<string, string> ContractSpellings = new()
    {
        [nameof(CategoryKeys.Achievements)] = "achievements",
        [nameof(CategoryKeys.Items)] = "items",
        [nameof(CategoryKeys.Minions)] = "minions",
        [nameof(CategoryKeys.Mounts)] = "mounts",
        [nameof(CategoryKeys.OccultProgression)] = "occultProgression",
        [nameof(CategoryKeys.OccultRecords)] = "occultRecords",
        [nameof(CategoryKeys.OrchestrionRolls)] = "orchestrionRolls",
        [nameof(CategoryKeys.QuestSequences)] = "questSequences",
        [nameof(CategoryKeys.Quests)] = "quests",
        [nameof(CategoryKeys.TripleTriadCards)] = "tripleTriadCards",
        [nameof(CategoryKeys.TripleTriadNpcs)] = "tripleTriadNpcs",
    };

    // `nameof` above is a compile-time check that each entry names a constant that really exists:
    // renaming or deleting one breaks the build here rather than silently dropping its coverage.
    [Fact]
    public void Every_key_matches_the_string_the_contract_defines()
    {
        Assert.Equal(ContractSpellings, CategoryKeyReflection.ByName());
    }

    // Two categories sharing a key would collide in every dictionary the payload, the kill
    // switches and the settings rows are keyed by — the second would silently overwrite the
    // first.
    [Fact]
    public void No_two_categories_share_a_key()
    {
        var keys = CategoryKeyReflection.All();

        // Distinct with no comparer already uses ordinal equality for strings, so naming
        // StringComparer.Ordinal states the intent rather than changing the result: a wire key has
        // to match character for character, and nothing about it should ever follow a locale.
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // The wire keys are camelCase per the contract, and a slip is invisible at runtime either way.
    // The serializer's DictionaryKeyPolicy lowercases a leading uppercase run, so a key written in
    // PascalCase ships as something its own constant does not say; a key already starting
    // lowercase is passed through untouched, so a separator in it travels verbatim and the server
    // does not recognize it. Pinning the shape at the constant catches both.
    [Fact]
    public void Every_key_is_camel_case()
    {
        foreach (var key in CategoryKeyReflection.All())
        {
            Assert.NotEmpty(key);

            // One expression for the whole rule: a lowercase first letter, then letters and digits
            // only. That rejects PascalCase, and equally the separators a camelCase check by first
            // letter alone would wave through — snake_case, kebab-case, a stray space or padding.
            Assert.Matches("^[a-z][A-Za-z0-9]*$", key);
        }
    }
}
