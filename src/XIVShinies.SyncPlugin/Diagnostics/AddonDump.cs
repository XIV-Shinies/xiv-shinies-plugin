// The whole file is a development-build diagnostic: a Release compile contains none of it, so its
// usings are guarded alongside it.
#if DEBUG
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XIVShinies.SyncPlugin.Diagnostics;

/// <summary>
/// Prints what an open game window is made of: the values it was handed to draw itself from, and
/// where each piece of its artwork comes from.
/// </summary>
/// <remarks>
/// <para>
/// Answers two questions no amount of reading files from outside the game can. "Where does that
/// icon come from?" — some are numbered files under <c>ui/icon</c>, findable from outside, but the
/// rest are rectangles cropped from a shared window texture and nothing outside the running client
/// knows which rectangle. And "what does this window actually know?" — a window's backing values
/// are where per-entry state lives, which is how a collection's facts are found in the first place.
/// </para>
/// <para>
/// Reached from <c>/shinies dumpaddon &lt;name&gt;</c>. Point it at the windows that display
/// catalogue artwork, never at party or social ones: where it is pointed is the protection, for
/// the reason set out in the addon-dump bullet of <c>docs/dalamud-compliance.md</c>.
/// </para>
/// </remarks>
// `unsafe` because a window's values and nodes are reached through raw pointers into the game's own
// memory — C#'s references and bounds checks do not apply, so every step is checked by hand.
internal static unsafe class AddonDump
{
    /// <summary>
    /// Prints an open game window's backing values, and every image node with the texture it
    /// draws from.
    /// </summary>
    /// <param name="addonName">The window's internal addon name, as the Addon Inspector shows it.</param>
    /// <remarks>
    /// <para>
    /// Answers "where does the game get that icon?" for artwork the site wants to reuse. Some
    /// icons are ordinary numbered ones under <c>ui/icon</c>, findable from outside the game; the
    /// rest are rectangles cropped out of a shared window texture, and nothing outside the running
    /// client knows which rectangle. This reads the answer off the window itself.
    /// </para>
    /// <para>
    /// Each node is reported with its position, the part it is currently drawing, and that part's
    /// rectangle within its texture — enough to crop the same image from the texture file. The
    /// indentation is component nesting; within one component the nodes print in the order its
    /// layout declares them, which is not their parent-and-child order.
    /// </para>
    /// <para>
    /// Reads game memory through raw pointers, so every step is checked by hand: a bad dereference
    /// is an access violation that no <c>catch</c> can rescue, and it would take the game down
    /// rather than the plugin. Two kinds of check are needed, and a null test is only one of them
    /// — see <see cref="DescribeImagePart"/> for the union whose wrong arm is non-null and still
    /// fatal to follow.
    /// </para>
    /// <para>
    /// Point it at the windows that display catalogue artwork, never at party or social ones:
    /// where it is pointed is the protection, for the reason set out in the addon-dump bullet of
    /// <c>docs/dalamud-compliance.md</c>.
    /// </para>
    /// </remarks>
    internal static void Run(string addonName, IGameGui gameGui, IPluginLog log)
    {
        // The marshalling that gets here discards the returned Task, and a throw would be captured
        // onto it and never surfaced — leaving a diagnostic whose whole product is log output
        // silently producing none. Managed failures are reported here instead. An access violation
        // is beyond any catch, which is why the checks above matter rather than this.
        try
        {
            DumpAddonCore(addonName, gameGui, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Addon dump of \"{addonName}\" failed.");
        }
    }

    /// <summary>Walks the window; see <see cref="DumpAddon"/>, which reports anything thrown here.</summary>
    private static void DumpAddonCore(string addonName, IGameGui gameGui, IPluginLog log)
    {
        // The lookup hands back a safe pointer wrapper; its Address is the raw AtkUnitBase*.
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(addonName).Address;
        if (addon == null)
        {
            log.Information(
                $"Addon dump: \"{addonName}\" is not open. Open the window first, and check the " +
                "spelling against Dalamud's Addon Inspector.");
            return;
        }

        // A window can be found while it is still being built, when the node count is real but the
        // tree behind it is not finished. Saying so beats printing a short list that looks whole.
        if (addon->UldManager.LoadedState != AtkLoadState.Loaded)
        {
            log.Information(
                $"Addon dump: \"{addonName}\" is open but still loading " +
                $"({addon->UldManager.LoadedState}). Try again in a moment.");
            return;
        }

        log.Information($"Addon dump: {addonName}, {addon->UldManager.NodeListCount} top-level nodes.");

        DumpAtkValues(addon, log);

        VisitedNodes.Clear();
        TruncatedDepth = false;
        DumpNodeList(&addon->UldManager, 0, log);
        ReportUnlistedNodes(addon, log);

        log.Information($"Addon dump: {addonName} complete.");
    }

    /// <summary>Prints the values the game handed the window to draw itself from.</summary>
    /// <remarks>
    /// <para>
    /// A window's nodes are its appearance; these are the data behind it. Where a node says "this
    /// tile is drawn bright", a value says "entry 30 is owned" — which is the form worth reading,
    /// because it survives scrolling, filtering and every other thing that changes what is on
    /// screen without changing what the character has.
    /// </para>
    /// <para>
    /// Printed as a flat indexed list because the meaning of each slot is the window's own
    /// business and differs per window. Working out which slots mean what is the point of looking.
    /// </para>
    /// </remarks>
    private static void DumpAtkValues(AtkUnitBase* addon, IPluginLog log)
    {
        var values = addon->AtkValues;
        var count = addon->AtkValuesCount;

        if (values == null || count == 0)
        {
            log.Information("Addon dump: the window carries no values.");
            return;
        }

        log.Information($"Addon dump: {count} values.");

        for (var i = 0; i < count; i++)
        {
            log.Information($"  [{i}] {DescribeAtkValue(&values[i])}");
        }
    }

    /// <summary>Renders one of a window's backing values, whatever type it turns out to hold.</summary>
    private static string DescribeAtkValue(AtkValue* value)
    {
        switch (value->Type)
        {
            case AtkValueType.Int:
                return $"int {value->Int}";

            case AtkValueType.UInt:
                return $"uint {value->UInt}";

            case AtkValueType.Bool:
                return $"bool {value->Byte != 0}";

            case AtkValueType.Float:
                return $"float {value->Float}";

            case AtkValueType.String:
            case AtkValueType.ConstString:
            case AtkValueType.ManagedString:
                // A string value is a pointer the game owns; null means the slot is typed as text
                // and carries none. The wrapper reads to the terminator rather than trusting a
                // length this code cannot verify.
                return value->String.Value == null
                    ? "string (none)"
                    : $"string \"{value->String}\"";

            default:
                return $"{value->Type}";
        }
    }

    /// <summary>
    /// Every node the listing walk reported, so the tree walk afterwards can spot what it missed.
    /// </summary>
    /// <remarks>
    /// A field rather than a parameter threaded through the recursion, because the walk is a
    /// development command that runs once on the framework thread and never concurrently with
    /// itself. Cleared at the start of each dump.
    /// </remarks>
    private static readonly HashSet<nint> VisitedNodes = [];

    /// <summary>Whether the walk stopped short of some component nesting this run.</summary>
    private static bool TruncatedDepth;

    /// <summary>How deep the walk follows components before giving up.</summary>
    private const int MaxComponentDepth = 8;

    /// <summary>Walks one component's node list, recursing into any nested components.</summary>
    private static void DumpNodeList(AtkUldManager* uld, int depth, IPluginLog log)
    {
        if (uld == null || uld->NodeList == null)
            return;

        // The game's own windows nest nowhere near this deep, so reaching the cap means either an
        // unusually composed window or memory that is not what it claims. Either way the listing
        // below it is missing, and a dump that truncates in silence is worse than one that stops:
        // the reader concludes the artwork is not there.
        if (depth > MaxComponentDepth)
        {
            TruncatedDepth = true;
            return;
        }

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null)
                continue;

            VisitedNodes.Add((nint)node);
            DumpNode(node, depth, log);
        }
    }

    /// <summary>
    /// Walks the window's live node tree and reports any node the listing above never reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The listing walks each component's node list, which is what the layout file declared. A
    /// window may also build nodes while it runs and link them into the tree directly, and those
    /// appear in no list. Without this check an icon absent from the dump would be ambiguous
    /// between "the window does not draw it" and "the walk could not see it" — and the first is
    /// the only answer this command is for.
    /// </para>
    /// <para>
    /// Reports a count rather than the nodes themselves: the point is to say whether the listing
    /// can be trusted, and a zero here is the whole answer.
    /// </para>
    /// </remarks>
    private static void ReportUnlistedNodes(AtkUnitBase* addon, IPluginLog log)
    {
        var unlisted = CountUnlisted(addon->RootNode, 0);

        if (TruncatedDepth)
        {
            log.Warning(
                $"Addon dump: stopped at {MaxComponentDepth} levels of component nesting, so " +
                "anything deeper is missing from the listing.");
        }

        if (unlisted > 0)
        {
            log.Warning(
                $"Addon dump: {unlisted} nodes are in the window's tree but in no node list, so " +
                "the listing above is INCOMPLETE — an icon missing from it may still be drawn.");
        }
        else
        {
            log.Information(
                "Addon dump: every node in the window's tree was listed, so an icon missing from " +
                "the listing is genuinely not drawn by this window.");
        }
    }

    /// <summary>Counts nodes reachable through the tree that the listing walk never visited.</summary>
    private static int CountUnlisted(AtkResNode* node, int depth)
    {
        // The same cap as the listing walk, for the same reason: this follows pointers out of
        // memory the plugin does not own.
        if (node == null || depth > MaxComponentDepth * 4)
            return 0;

        var unlisted = VisitedNodes.Contains((nint)node) ? 0 : 1;

        unlisted += CountUnlisted(node->ChildNode, depth + 1);

        // Siblings are followed at the same depth they are, since they are not nested — but the
        // cap still applies through the recursion, which is why it is generous.
        unlisted += CountUnlisted(node->PrevSiblingNode, depth + 1);

        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->UldManager.RootNode != null)
                unlisted += CountUnlisted(component->UldManager.RootNode, depth + 1);
        }

        return unlisted;
    }

    /// <summary>Reports one node, and descends when it turns out to be a component.</summary>
    /// <remarks>
    /// Visibility is read from the node's own flag rather than by asking the game whether the node
    /// is effectively visible. The flag is a plain memory read, where the question is a native call
    /// the bindings locate by scanning for a byte pattern — one that a game patch can leave
    /// unresolved, at which point calling it jumps through a null pointer. The flag also answers
    /// the more useful question for a dump: whether THIS node is hidden, rather than whether some
    /// ancestor is hiding it.
    /// </remarks>
    private static void DumpNode(AtkResNode* node, int depth, IPluginLog log)
    {
        // Guarded here as well as at the call site, so the rule that this walk never dereferences
        // an unchecked pointer holds within each method rather than across them.
        if (node == null)
            return;

        var indent = new string(' ', depth * 2);

        // Component types are numbered from 1000 up; everything below that is a leaf the game
        // draws directly.
        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            log.Information(
                $"{indent}#{node->NodeId} component {(int)node->Type} " +
                $"at {node->X},{node->Y} {node->Width}x{node->Height} " +
                $"visible={node->NodeFlags.HasFlag(NodeFlags.Visible)}");

            if (component != null)
                DumpNodeList(&component->UldManager, depth + 1, log);

            return;
        }

        // Three node types draw from a parts list, and all three are artwork worth reporting:
        // plain images, the nine-grid nodes that frames and panels are built from, and clipping
        // masks. Text and collision nodes carry no texture and are skipped so the interesting rows
        // are not buried.
        //
        // Image and clipping-mask nodes share a layout, so one cast serves both; nine-grid keeps
        // its part id in a wider field and is read separately.
        AtkUldPartsList* parts;
        uint partId;
        switch (node->Type)
        {
            case NodeType.Image:
                parts = ((AtkImageNode*)node)->PartsList;
                partId = ((AtkImageNode*)node)->PartId;
                break;

            case NodeType.ClippingMask:
                parts = ((AtkClippingMaskNode*)node)->PartsList;
                partId = ((AtkClippingMaskNode*)node)->PartId;
                break;

            case NodeType.NineGrid:
                parts = ((AtkNineGridNode*)node)->PartsList;
                partId = ((AtkNineGridNode*)node)->PartId;
                break;

            default:
                return;
        }

        log.Information(
            $"{indent}#{node->NodeId} {node->Type} at {node->X},{node->Y} " +
            $"{node->Width}x{node->Height} ownVisible={node->NodeFlags.HasFlag(NodeFlags.Visible)} " +
            $"part={partId} -> {DescribeImagePart(parts, partId)}");
    }

    /// <summary>Names the texture a parts list draws from, and the rectangle it takes from it.</summary>
    /// <param name="parts">The node's parts list, which may be null.</param>
    /// <param name="partId">Which part of that list the node is currently drawing.</param>
    private static string DescribeImagePart(AtkUldPartsList* parts, uint partId)
    {
        if (parts == null || parts->Parts == null)
            return "(no parts list)";

        if (partId >= parts->PartCount)
            return $"(part {partId} beyond the list's {parts->PartCount})";

        var part = &parts->Parts[partId];
        var rectangle = $"rect {part->U},{part->V} {part->Width}x{part->Height}";

        var asset = part->UldAsset;
        if (asset == null)
            return $"(no asset) {rectangle}";

        // AtkTexture keeps a file resource, a crest and a kernel texture in ONE union, with
        // TextureType saying which of them is live. Reading the file-resource arm when it is not
        // the live one hands back a perfectly non-null pointer into an unrelated object, whose
        // bytes would then be followed as though they were a resource handle and a filename — an
        // access violation on a wild address, which kills the game rather than the plugin. The
        // null check below cannot catch that: the pointer is not null, it is simply not a
        // resource. Only the tag can tell them apart.
        var texture = asset->AtkTexture;
        if (texture.TextureType != TextureType.Resource)
            return $"({texture.TextureType} texture, not a file) {rectangle}";

        var resource = texture.Resource;
        if (resource == null)
            return $"(no texture loaded) {rectangle}";

        // An icon id is set when the texture came from the numbered ui/icon tree; it is the more
        // useful answer when present, because it needs no rectangle to reuse.
        var iconId = resource->IconId;
        var handle = resource->TexFileResourceHandle;
        var path = handle == null
            ? "(no file handle)"
            : handle->ResourceHandle.FileName.ToString();

        // The game stores -1, not 0, for a texture that did not come from the icon tree — so
        // uint.MaxValue is the "this is not an icon" answer. Testing against zero alone would
        // report an icon id for every window-texture node in the dump, which is exactly the
        // false positive the reader scans this column to rule out. Zero is excluded too: a
        // zeroed resource is a plausible not-yet-loaded state, and icon 0 is not a real icon.
        var hasIconId = iconId is not 0 and not uint.MaxValue;

        return hasIconId
            ? $"iconId {iconId} ({path}) {rectangle}"
            : $"{path} {rectangle}";
    }
}
#endif
