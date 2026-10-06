using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XIVShinies.SyncPlugin.Appearance;

namespace XIVShinies.SyncPlugin.Tests.Appearance;

// Pins the appearance category's frozen wire vocabulary. These names are the keys of the
// "customize" object the server reads, so the table is asserted in full and in order: a rename,
// a reordering or a dropped entry would each silently move a byte under the wrong key.
public class CustomizeFieldsTests
{
    [Fact]
    public void The_names_are_the_frozen_wire_vocabulary_in_byte_order()
    {
        var expected = new[]
        {
            "Race", "Gender", "ModelType", "Height", "Tribe", "FaceType", "HairStyle",
            "HasHighlights", "SkinColor", "EyeColor", "HairColor", "HairColor2", "FaceFeatures",
            "FaceFeaturesColor", "Eyebrows", "EyeColor2", "EyeShape", "NoseShape", "JawShape",
            "LipStyle", "LipColor", "RaceFeatureSize", "RaceFeatureType", "BustSize", "Facepaint",
            "FacepaintColor",
        };

        Assert.Equal(expected, CustomizeFields.Names);
    }

    // Two bytes under one name would collapse into a single JSON key, losing one of them.
    [Fact]
    public void Every_name_is_distinct()
    {
        Assert.Equal(CustomizeFields.Names.Count, CustomizeFields.Names.Distinct().Count());
    }

    // The table is shared by every build, so a caller able to write into it would change the wire
    // for everyone. Reaching the list through IList<T> — the interface an array would let you
    // write through — must still refuse the write.
    [Fact]
    public void The_table_cannot_be_edited_through_the_list_it_exposes()
    {
        var asList = Assert.IsAssignableFrom<IList<string>>(CustomizeFields.Names);

        Assert.Throws<NotSupportedException>(() => asList[0] = "Species");
    }
}
