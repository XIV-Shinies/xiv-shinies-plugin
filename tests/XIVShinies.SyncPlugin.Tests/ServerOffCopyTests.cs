using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// The sentences for something the website has switched off or does not offer. Each is pinned whole,
// with the address it was handed: a keyword check would still pass if two were swapped.
public class ServerOffCopyTests
{
    // Made up, so a test can tell an address passed in from one written into the copy.
    private const string Host = "shinies.example";

    [Fact]
    public void A_switched_off_feature_names_the_website()
    {
        Assert.Equal("Temporarily switched off by shinies.example.", ServerOffCopy.Feature(Host));
    }

    [Fact]
    public void A_feature_the_website_never_offered_says_so()
    {
        Assert.Equal("Not offered by shinies.example.", ServerOffCopy.NotOffered(Host));
    }

    [Fact]
    public void Every_collection_switched_off_keeps_the_users_choices()
    {
        Assert.Equal(
            "shinies.example has switched off every collection for now. Your own choices are unchanged.",
            ServerOffCopy.EveryCollection(Host));
    }

    [Fact]
    public void A_pause_keeps_the_users_choices()
    {
        Assert.Equal(
            "shinies.example has paused syncing for everyone. Your own choices are unchanged.",
            ServerOffCopy.Paused(Host));
    }
}
