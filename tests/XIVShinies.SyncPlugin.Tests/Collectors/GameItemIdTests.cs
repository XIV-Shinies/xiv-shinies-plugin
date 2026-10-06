using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// GameItemId is the pure rule for reading the game's stored item ids back into a base id plus a
// quality flag. The containers it is applied to are verified by in-game QA.
public class GameItemIdTests
{
    // A [Theory] runs one test body once per [InlineData] row, like `it.each([...])` in Jest. The
    // rows pin both edges of the high-quality range (one million up to, but not including, two
    // million). 500_123 sits in the band where the game may encode collectables as id + 500,000;
    // that encoding is not decoded here, so such an id passes through unchanged.
    [Theory]
    [InlineData(0u, 0u, false)]
    [InlineData(2_642u, 2_642u, false)]
    [InlineData(500_123u, 500_123u, false)]
    [InlineData(999_999u, 999_999u, false)]
    [InlineData(1_000_000u, 0u, true)]
    [InlineData(1_003_707u, 3_707u, true)]
    [InlineData(1_999_999u, 999_999u, true)]
    [InlineData(2_000_000u, 2_000_000u, false)]
    [InlineData(2_000_123u, 2_000_123u, false)]
    public void An_id_splits_into_its_base_id_and_quality(uint storedId, uint baseId, bool isHq)
    {
        Assert.Equal((baseId, isHq), GameItemId.Split(storedId));
    }
}
