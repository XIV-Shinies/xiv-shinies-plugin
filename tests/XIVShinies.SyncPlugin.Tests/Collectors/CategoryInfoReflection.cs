using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Reads every <see cref="CategoryInfo"/> the <see cref="CollectorRegistry"/> declares, by
/// reflection, so a test can pin a property of every collection without a hand-written list.
/// </summary>
/// <remarks>
/// <c>CompletenessDeclarationTests</c> checks that the scan reaches every declared key. Reading the
/// private static fields runs only their initializers, which load no game assembly; the Dalamud types
/// in <c>Create</c>'s signature resolve only if it is called. Keep the fields private: only these
/// tests need them.
/// </remarks>
internal static class CategoryInfoReflection
{
    public static IReadOnlyList<CategoryInfo> All() =>
        typeof(CollectorRegistry)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(CategoryInfo))
            .Select(field => (CategoryInfo)field.GetValue(null)!)
            .ToList();
}
