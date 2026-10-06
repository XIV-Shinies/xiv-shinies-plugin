using System.Collections.Generic;
using System.Linq;

namespace XIVShinies.SyncPlugin.Collectors;

/// <summary>
/// Decides which collections the standing "turn on new collections automatically" answer may
/// switch on.
/// </summary>
/// <remarks>
/// <para>
/// That answer is the one path that switches a collection on with no action from the user on the
/// consent list (see <see cref="PluginSettings.AutoEnableUnseenCategories"/>), so what it may reach
/// is decided here, in one place, and unit-tested. The plugin's load path passes this list to it
/// unchanged, and anything else that ever enables a collection on the user's behalf must ask this
/// class too.
/// </para>
/// <para>
/// Two kinds of collection are left out, each by its own declaration:
/// </para>
/// <list type="bullet">
/// <item>one that requires its own opt-in (<see cref="ICollector.RequiresOwnOptIn"/>), because it
/// sends a kind of data the standing answer was never asked about;</item>
/// <item>one whose groups the user answers separately, because switching it on without them would
/// strand it ticked and collecting nothing (see
/// <see cref="ManifestConsent.FixedScopeCategoryKeys"/>).</item>
/// </list>
/// </remarks>
// `static class`: never instantiated, only static members — the C# analog of a TypeScript module
// that exports plain functions. It holds no game types, which is what lets the tests call it.
public static class AutoEnableScope
{
    /// <summary>The keys of the collections the standing answer may switch on.</summary>
    /// <param name="collectors">Every registered collector.</param>
    /// <returns>The eligible keys, in registry order.</returns>
    // `=>` after the signature makes this an expression-bodied method: the single expression is the
    // return value, like an arrow function without braces. `.Where(collector => ...)` is LINQ's
    // filter, `array.filter(...)` in JavaScript, except that it is lazy: nothing is filtered until
    // FixedScopeCategoryKeys loops over the result.
    //
    // The own-opt-in filter runs first, then the result is handed to the manifest rule rather than
    // restating it, so each exclusion keeps a single home.
    public static IReadOnlyList<string> EligibleCategoryKeys(IEnumerable<ICollector> collectors) =>
        ManifestConsent.FixedScopeCategoryKeys(
            collectors.Where(collector => !collector.RequiresOwnOptIn));
}
