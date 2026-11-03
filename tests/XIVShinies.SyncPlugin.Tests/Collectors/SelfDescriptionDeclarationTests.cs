using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>Pins which collections set each of four <see cref="CategoryInfo"/> flags.</summary>
/// <remarks>
/// Each flag changes behavior no collector test would catch (see its property on
/// <see cref="CategoryInfo"/>), and a flag copied along with an existing entry, or lost from one, would
/// do so silently. Comparing whole sets fails both when a category stops declaring and when one starts,
/// so every change to a set has to be made here on purpose.
/// <see cref="CompletenessDeclarationTests.Every_registered_category_is_reachable_by_reflection"/>
/// guards the reflection these sets are checked through.
/// </remarks>
public class SelfDescriptionDeclarationTests
{
    /// <summary>
    /// The collections collected only once the server's <c>/config</c> names them and switches
    /// them on. The reasoning for each lives beside its <see cref="CategoryInfo"/> in
    /// <see cref="CollectorRegistry"/>.
    /// </summary>
    private static readonly HashSet<string> RequiringServerSupport = new()
    {
        CategoryKeys.Appearance,
        CategoryKeys.Glamour,
    };

    /// <summary>
    /// The collections whose facts are one record about the character, which the upload log names
    /// without a count.
    /// </summary>
    private static readonly HashSet<string> SingleRecords = new()
    {
        CategoryKeys.Appearance,
    };

    /// <summary>
    /// The collections that read the character's storage containers and report their scan state.
    /// </summary>
    private static readonly HashSet<string> ReadingStorage = new()
    {
        CategoryKeys.Items,
        CategoryKeys.Glamour,
    };

    /// <summary>
    /// The collections scoped by the server's item manifest and its separately answered consent
    /// groups. The standing answer for new collections never switches these on.
    /// </summary>
    private static readonly HashSet<string> UsingItemManifest = new()
    {
        CategoryKeys.Items,
    };

    [Fact]
    public void Exactly_the_expected_categories_require_server_support()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.RequiresServerSupport)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(RequiringServerSupport, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_are_single_records()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.IsSingleRecord)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(SingleRecords, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_read_storage()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.ReadsStorage)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(ReadingStorage, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_use_the_item_manifest()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.UsesItemManifest)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(UsingItemManifest, actual);
    }
}
