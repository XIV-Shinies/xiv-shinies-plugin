using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using XIVShinies.SyncPlugin.Api;
using XIVShinies.SyncPlugin.Collectors;
using Xunit;

namespace XIVShinies.SyncPlugin.Tests.Collectors;

/// <summary>
/// Pins which collections drop each fact once a response confirms its store, and the key that confirms
/// it.
/// </summary>
/// <remarks>
/// Each key must match the contract exactly; see <see cref="CategoryInfo.StoredResponseKey"/>.
/// </remarks>
public class StoreConfirmationDeclarationTests
{
    [Fact]
    public void Exactly_the_expected_categories_name_a_store_confirmation_key()
    {
        var actual = CategoryInfoReflection.All()
            .Where(info => info.StoredResponseKey is not null)
            .ToDictionary(info => info.Key, info => info.StoredResponseKey!);

        var expected = new Dictionary<string, string>
        {
            [CategoryKeys.AchievementProgress] = "storedAchievementProgress",
        };
        Assert.Equal(expected, actual);
    }

    // SyncResponse.Carries sees only keys no property models, so modeling one would silently stop its
    // facts from ever being dropped.
    [Fact]
    public void No_store_confirmation_key_is_modeled_as_a_response_property()
    {
        var modeled = typeof(SyncResponse).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToHashSet();

        var keys = CategoryInfoReflection.All().Select(info => info.StoredResponseKey).OfType<string>();

        Assert.DoesNotContain(keys, modeled.Contains);
    }
}
