using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Reads the category keys off <see cref="CategoryKeys"/> by reflection, so a test covers every key
/// without a hand-written list.
/// </summary>
internal static class CategoryKeyReflection
{
    /// <summary>
    /// Every declared key, by the name of its constant, so a failing assertion names the constant
    /// responsible.
    /// </summary>
    /// <remarks>
    /// The <c>IsLiteral</c> filter is required: <c>GetRawConstantValue</c> throws on a field that is
    /// not a constant.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ByName() =>
        typeof(CategoryKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

    /// <summary>
    /// Every declared key, duplicates kept, so a test can find two categories sharing a key.
    /// </summary>
    public static IReadOnlyList<string> All() => ByName().Values.ToList();
}
