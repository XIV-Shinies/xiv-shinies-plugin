using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Reads every <see cref="CategoryInfo"/> that <see cref="CollectorRegistry"/> declares, by
/// reflection, so a test can cover every registered collection without anyone remembering to extend
/// a hand-written list.
/// </summary>
/// <remarks>
/// <para>
/// Shared so every test that pins a set of declarations sees the same enumeration and none can
/// drift from another's idea of what the registry holds.
/// <see cref="CompletenessDeclarationTests"/> guards the reflection itself, by checking it finds a
/// category for every declared key.
/// </para>
/// <para>
/// Reachable from a unit test because <see cref="CategoryInfo"/> is Dalamud-free: reading the
/// registry's private static fields runs only their initializers, which construct records and load
/// no game assembly. (Its <c>Create</c> method mentions Dalamud services in its signature, but a
/// signature is not resolved unless the method is called.)
/// </para>
/// </remarks>
internal static class CollectorRegistryReflection
{
    /// <summary>Every <see cref="CategoryInfo"/> the registry holds in a static field.</summary>
    /// <remarks>
    /// <c>BindingFlags.NonPublic</c> because the registry's <see cref="CategoryInfo"/> fields are
    /// private — they are implementation detail to every caller except these tests, and making them
    /// visible purely to be tested would be the wrong trade.
    /// </remarks>
    public static IReadOnlyList<CategoryInfo> Categories() =>
        typeof(CollectorRegistry)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(CategoryInfo))
            .Select(field => (CategoryInfo)field.GetValue(null)!)
            .ToList();
}
