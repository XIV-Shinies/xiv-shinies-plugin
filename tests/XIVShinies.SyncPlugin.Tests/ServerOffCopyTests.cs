using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// Sentences for what the website switched off, does not offer, or has not answered about yet. Each is
// pinned whole, with the address it was handed, since a keyword check would pass with two swapped.
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

    // Before any /config the answer is unknown, so the sentence claims neither way.
    [Fact]
    public void A_collection_awaiting_the_websites_answer_says_it_is_waiting()
    {
        Assert.Equal(
            "Waiting for shinies.example to say whether it offers this.",
            ServerOffCopy.AwaitingAnswer(Host));
    }

    [Fact]
    public void Every_collection_switched_off_keeps_the_users_choices()
    {
        Assert.Equal(
            "shinies.example has switched off every collection for now. Your own choices are unchanged.",
            ServerOffCopy.EveryCollection(Host));
    }

    // Why the sentence names the profile switch is in ServerOffCopy.Paused's remarks.
    [Fact]
    public void A_pause_names_the_profile_switch_and_keeps_the_users_choices()
    {
        Assert.Equal(
            "Syncing is off on shinies.example. If you turned off \"Sync from the plugin\" on your "
            + "profile, turn it back on there. Your own choices are unchanged.",
            ServerOffCopy.Paused(Host));
    }
}
