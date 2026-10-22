using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XIVShinies.SyncPlugin.Beastmaster;

namespace XIVShinies.SyncPlugin.Tests.Crucible;

/// <summary>
/// Loads a window fixture from <c>Crucible/Fixtures</c> as the values a window hands its reader.
/// </summary>
/// <remarks>
/// Each value converts to the <see cref="AddonValue"/> the plugin's own conversion makes. A position
/// the file leaves out, and a kind the readers never use, load as unreadable, like a slot the game
/// left empty.
/// </remarks>
internal static class WindowFixture
{
    public static List<AddonValue> Load(string fileName)
    {
        // The build copies the fixtures beside the test assembly (see the test project's <None>
        // item).
        var path = Path.Combine(AppContext.BaseDirectory, "Crucible", "Fixtures", fileName);

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
