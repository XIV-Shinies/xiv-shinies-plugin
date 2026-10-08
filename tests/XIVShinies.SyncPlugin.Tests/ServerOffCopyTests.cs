using Xunit;

namespace XIVShinies.SyncPlugin.Tests;

// The sentences for something the website has switched off, does not offer, or has not yet answered
// about. Each is pinned whole, with the address it was handed: a keyword check would still pass if
// two were swapped. Each `[Fact]` is one test, like `it(...)` in Jest.
public class ServerOffCopyTests
{
    // A made-up address, so a test can tell one that was passed in from one written into the copy.
    // `const` fixes the value when the code compiles.
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

    [Fact]
    public void A_pause_keeps_the_users_choices()
    {
        Assert.Equal(
            "shinies.example has paused syncing for everyone. Your own choices are unchanged.",
            ServerOffCopy.Paused(Host));
    }
}
