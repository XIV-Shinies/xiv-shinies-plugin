using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The sentence a halt shows on the sync card and both sharing cards. Each halt has its own fix, so
// each test pins where its sentence comes from, and a wrong fix cannot pass for the right one.
public class HaltCopyTests
{
    private const string Host = "xiv-shinies.com";
    private const string DefaultUrl = "https://xiv-shinies.com";

    [Theory]
    [InlineData(ApiStatus.CharacterNotClaimed)]
    [InlineData(ApiStatus.CharacterNotVerified)]
    [InlineData(ApiStatus.CharacterAmbiguous)]
    [InlineData(ApiStatus.CharacterBoundElsewhere)]
    public void A_character_refusal_reads_as_its_refusal_sentence(ApiStatus halt)
    {
        Assert.Equal(
            CharacterRefusalCopy.For(halt, "Some Name", "Excalibur", Host),
            HaltCopy.For(halt, "Some Name", "Excalibur", Host, DefaultUrl, customBackendAcknowledged: false));
    }

    // A settings problem is fixed on this machine, so the backend check that refused it words the fix.
    [Fact]
    public void A_configuration_halt_reads_as_the_backend_check_words_it()
    {
        const string unusable = "not a url";

        Assert.Equal(
            BackendUrl.DescribeUnusableSetting(unusable, customBackendAcknowledged: false),
            HaltCopy.For(
                ApiStatus.NotConfigured, "Some Name", "Excalibur", Host, unusable,
                customBackendAcknowledged: false));
    }

    // The token is the only other status that raises a halt. A null halt, read in the frame a halt is
    // cleared, gets the same sentence rather than none.
    [Theory]
    [InlineData(ApiStatus.InvalidToken)]
    [InlineData(null)]
    public void A_token_halt_asks_for_a_new_token(ApiStatus? halt)
    {
        Assert.Equal(
            "Your token was rejected. Generate a new one on xiv-shinies.com, paste it under Account, " +
            "then press Sync now.",
            HaltCopy.For(halt, "Some Name", "Excalibur", Host, DefaultUrl, customBackendAcknowledged: false));
    }
}
