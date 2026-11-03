using Xunit;
using XIVShinies.SyncPlugin.Collectors;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

// Reads the item finder's stored ids back into a base id and a quality flag.
public class GameItemIdTests
{
    // The rows pin both edges of the high-quality range, one million to just under two million. A
    // collectable the game may store as its id plus 500,000 is not decoded, so 500_123 passes through.
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
