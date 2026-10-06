using Xunit;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Windows;

namespace XIVShinies.SyncPlugin.Tests.Windows;

// The sentence a halt shows, on the sync card and on the live tracker's and Crucible run sharing's
// cards alike. Each kind of halt has its own fix, so each test pins where its sentence comes from
// (the refusal copy, the backend check, or, for the token, the exact string), and a sentence that
// named the wrong fix cannot pass for the right one.
public class HaltCopyTests
{
    private const string Host = "xiv-shinies.com";
    private const string DefaultUrl = "https://xiv-shinies.com";

    // A character refusal reads exactly as the refusal copy words it.
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

    // A settings problem is fixed on this machine, so its sentence comes from the backend check
    // that refused the setting.
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

    // Every other halt is the token: it is the only other status that raises one. An unknown halt
    // (read in the frame a halt is being cleared) gets the same sentence rather than none.
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
