using System.Collections.Generic;
using System.Linq;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Pins the exact sets of collections that declare <see cref="CategoryInfo.RequiresServerSupport"/>,
/// <see cref="CategoryInfo.IsSingleRecord"/>, <see cref="CategoryInfo.ReadsStorage"/> and
/// <see cref="CategoryInfo.RequiresOwnOptIn"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each flag changes behavior no collector test would catch: the first holds a collection back,
/// unread and unsent, until the server names it; the second drops its count from the upload log;
/// the third decides whether the settings panel shows the storage container lines while the
/// collection is on; the fourth keeps the standing "turn on new collections automatically" answer
/// from switching the collection on. A flag copied along with an existing
/// <see cref="CategoryInfo"/>, or lost from one, would do any of these silently. Comparing the whole
/// set is what makes each test fail in both directions — a category that stops declaring, and one
/// that starts — so every change to any set has to be made here on purpose.
/// </para>
/// <para>
/// The same pattern as <see cref="CompletenessDeclarationTests"/>, reading the registry through the
/// same <see cref="CollectorRegistryReflection"/>.
/// <see cref="CompletenessDeclarationTests.Every_registered_category_is_reachable_by_reflection"/>
/// checks that the reflection reaches every registered category.
/// </para>
/// </remarks>
public class SelfDescriptionDeclarationTests
{
    /// <summary>
    /// The collections collected only once the server's <c>/config</c> names them and switches
    /// them on. The reasoning for each lives beside its <see cref="CategoryInfo"/> in
    /// <see cref="CollectorRegistry"/>.
    /// </summary>
    // `new()` with no type after it is a "target-typed" new: the compiler takes the type from the
    // declaration on the left, so this is a HashSet<string>. The braces are a collection
    // initializer, filling the set as it is built, like passing an array to `new Set([...])` in
    // TypeScript.
    private static readonly HashSet<string> RequiringServerSupport = new()
    {
        CategoryKeys.Appearance,
        CategoryKeys.Glamour,
    };

    /// <summary>
    /// The collections whose facts are one record about the character rather than a collection of
    /// things, which the upload log names without a count.
    /// </summary>
    private static readonly HashSet<string> SingleRecords = new()
    {
        CategoryKeys.Appearance,
    };

    /// <summary>
    /// The collections whose facts come from the character's storage containers and which report
    /// those containers' scan state, so the settings panel shows the container lines while any of
    /// them is on.
    /// </summary>
    private static readonly HashSet<string> ReadingStorage = new()
    {
        CategoryKeys.Items,
        CategoryKeys.Glamour,
    };

    /// <summary>
    /// The collections the standing answer for new collections never reaches, so only the user
    /// switches them on.
    /// </summary>
    private static readonly HashSet<string> RequiringOwnOptIn = new()
    {
        CategoryKeys.Appearance,
    };

    [Fact]
    public void Exactly_the_expected_categories_require_server_support()
    {
        var actual = CollectorRegistryReflection.Categories()
            .Where(info => info.RequiresServerSupport)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(RequiringServerSupport, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_are_single_records()
    {
        var actual = CollectorRegistryReflection.Categories()
            .Where(info => info.IsSingleRecord)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(SingleRecords, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_read_storage()
    {
        var actual = CollectorRegistryReflection.Categories()
            .Where(info => info.ReadsStorage)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(ReadingStorage, actual);
    }

    [Fact]
    public void Exactly_the_expected_categories_require_their_own_opt_in()
    {
        var actual = CollectorRegistryReflection.Categories()
            .Where(info => info.RequiresOwnOptIn)
            .Select(info => info.Key)
            .ToHashSet();

        Assert.Equal(RequiringOwnOptIn, actual);
    }
}
