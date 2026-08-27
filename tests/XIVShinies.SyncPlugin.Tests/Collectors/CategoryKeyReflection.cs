using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Reads the category keys straight off <see cref="CategoryKeys"/> by reflection, so a test can
/// cover every key without anyone remembering to update a hand-written list.
/// </summary>
/// <remarks>
/// Shared so every test that needs the keys sees the same enumeration and none can drift from
/// another's idea of what <see cref="CategoryKeys"/> declares.
/// </remarks>
internal static class CategoryKeyReflection
{
    /// <summary>Every declared key, paired with the constant that declares it.</summary>
    /// <remarks>
    /// <para>
    /// Keyed by constant name, so a test can assert the whole set at once and have a missing or
    /// misspelled entry point at the constant responsible.
    /// </para>
    /// <para>
    /// <c>BindingFlags.Static</c> is required even though not one declaration in
    /// <see cref="CategoryKeys"/> says the word <c>static</c>: in C# a <c>const</c> is
    /// <b>implicitly</b> static, because its value belongs to the type rather than to any
    /// instance. <c>IsLiteral</c> is what separates a <c>const</c> from a <c>static readonly</c>
    /// field, and it is mandatory rather than decorative — <c>GetRawConstantValue</c> throws on
    /// any field that is not a compile-time constant.
    /// </para>
    /// <para>
    /// <c>GetRawConstantValue</c> reads the value out of the assembly's metadata. A <c>const</c>
    /// has no storage to read at runtime: the compiler substitutes its value directly into every
    /// place that mentions it, so there is nothing to fetch from an object.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ByName() =>
        typeof(CategoryKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

    /// <summary>
    /// Every declared key as a plain list, duplicates included.
    /// </summary>
    /// <remarks>
    /// A list rather than a set on purpose: a caller checking that no two categories share a key
    /// needs the duplicate to still be present to find it.
    /// </remarks>
    public static IReadOnlyList<string> All() => ByName().Values.ToList();
}
