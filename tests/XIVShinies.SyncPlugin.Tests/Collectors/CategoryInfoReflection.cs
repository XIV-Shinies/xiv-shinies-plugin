using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Reads every <see cref="CategoryInfo"/> the <see cref="CollectorRegistry"/> declares, by
/// reflection, so a test can pin a property of every collection, including one added tomorrow,
/// without a hand-written list.
/// </summary>
/// <remarks>
/// <para>
/// Shared so every test that pins a self-description flag sees the same enumeration and none can
/// drift from another's idea of what the registry declares; <c>CompletenessDeclarationTests</c>
/// checks that the scan reaches every declared key.
/// </para>
/// <para>
/// Reachable from a unit test because <see cref="CategoryInfo"/> is Dalamud-free: reading the
/// registry's private static fields runs only their initializers, which construct records and load
/// no game assembly. (The registry's <c>Create</c> method mentions Dalamud services in its signature,
/// but a signature is not resolved unless the method is called.) The fields stay private: they are
/// implementation detail to every caller but these tests, and making them visible purely to be tested
/// would be the wrong trade.
/// </para>
/// </remarks>
// `internal` makes it visible only inside the test project. A `static class` holds only shared
// members and is never instantiated: a module of functions.
internal static class CategoryInfoReflection
{
    /// <summary>Every <see cref="CategoryInfo"/> the registry declares.</summary>
    /// <remarks>
    /// <c>BindingFlags.NonPublic | BindingFlags.Static</c> asks for private static fields; <c>|</c>
    /// combines the two flags. <c>typeof(T)</c> is the type as a value that can be inspected.
    /// <c>(CategoryInfo)x</c> treats the value as its real type, like <c>x as CategoryInfo</c> in
    /// TypeScript but checked at run time (a wrong type throws), and the <c>!</c> after
    /// <c>GetValue(null)</c> tells the compiler it is not null.
    /// </remarks>
    // `IReadOnlyList<T>` is a list its holder cannot change, like `readonly T[]`, and `=>` makes the
    // expression after it the whole method body. `.Where`, `.Select` and `.ToList` work like the array
    // methods `filter`, `map` and a copy into a new array.
    public static IReadOnlyList<CategoryInfo> All() =>
        typeof(CollectorRegistry)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(CategoryInfo))
            .Select(field => (CategoryInfo)field.GetValue(null)!)
            .ToList();
}
