using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

/// <summary>
/// Loads a window fixture from <c>Crucible/Fixtures</c> as the list of values a window is handed,
/// the input every reader takes.
/// </summary>
/// <remarks>
/// Each value kind becomes the <see cref="AddonValue"/> the plugin's own conversion makes of it: a
/// boolean a flag, an unsigned number a number, a non-negative signed number a number, text its raw
/// string. A position the file leaves out, and a kind the readers never use, load as unreadable, the
/// same as a slot the game left empty.
/// </remarks>
// `internal` makes the class visible inside this test project only, like a helper shared between a
// package's own files but not exported from it.
internal static class WindowFixture
{
    /// <summary>Loads the fixture with this file name.</summary>
    /// <param name="fileName">The file in <c>Crucible/Fixtures</c>, e.g. <c>hud.json</c>.</param>
    public static List<AddonValue> Load(string fileName)
    {
        // AppContext.BaseDirectory is the folder the test assembly runs from, where the build copies
        // the fixtures (see the test project's <None> item).
        var path = Path.Combine(AppContext.BaseDirectory, "Crucible", "Fixtures", fileName);

        // `using` disposes the parsed document when this method returns, like a cleanup at the end
        // of a function in TypeScript.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var count = root.GetProperty("valueCount").GetInt32();
        var values = new List<AddonValue>(count);
        for (var i = 0; i < count; i++)
            values.Add(AddonValue.Unreadable);

        foreach (var entry in root.GetProperty("values").EnumerateArray())
        {
            var index = entry.GetProperty("i").GetInt32();
            var value = entry.GetProperty("value");

            // A `switch` expression picks the value for the first arm whose pattern matches, like
            // a lookup table; `A or B or C` matches any of the names, and `_` is the fallback arm.
            // The `!` on `GetString()!` tells the compiler a text value is never null here.
            values[index] = entry.GetProperty("type").GetString() switch
            {
                "Bool" => AddonValue.FromBoolean(value.GetBoolean()),
                "UInt" => AddonValue.FromInteger(value.GetUInt32()),
                "Int" => value.GetInt32() >= 0
                    ? AddonValue.FromInteger((uint)value.GetInt32())
                    : AddonValue.Unreadable,
                "String" or "ConstString" or "ManagedString" => AddonValue.FromText(value.GetString()!),
                _ => AddonValue.Unreadable,
            };
        }

        return values;
    }
}
