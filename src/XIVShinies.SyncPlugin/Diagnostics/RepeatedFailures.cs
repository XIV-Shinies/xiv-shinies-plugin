using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace XIVShinies.SyncPlugin.Diagnostics;

/// <summary>
/// Remembers which failures have already been reported, so a bug that fires on a repeating path is
/// reported once rather than on every pass.
/// </summary>
/// <remarks>
/// <para>
/// A failure on one of the plugin's self-repeating loops is usually deterministic, so an unguarded
/// log line becomes tens of thousands of identical entries a day.
/// </para>
/// <para>
/// Shared protocol with <see cref="Sync.SyncOutcomeWarnings"/> and
/// <see cref="Collectors.FrameBudgetWarnings"/>: test-and-record, which <c>HashSet.Add</c> does in
/// one call by returning false when the key was already present — JavaScript's
/// <c>Set.prototype.add</c> returns the set, so it cannot. True means be loud; false means say it
/// quietly rather than not at all. <see cref="Sync.PayloadCapWarnings"/> records the same way but
/// hands back the lines to log, so a repeat is simply omitted.
/// </para>
/// <para>
/// Not thread-safe. Callers serialize access: one thread, or a gate that admits one caller at a
/// time and publishes what the previous one wrote.
/// </para>
/// </remarks>
public sealed class RepeatedFailures
{
    /// <summary>
    /// The failure signatures already reported.
    /// </summary>
    /// <remarks>
    /// Bounded only because <see cref="SignatureOf"/> excludes the exception message: the number of
    /// distinct type-and-method pairs a build can produce is small, while messages that name an id
    /// or a count are unbounded. A signature that included one would grow this set without limit on
    /// a once-a-second path.
    /// </remarks>
    private readonly HashSet<string> seen = [];

    /// <summary>What identifies one failure for the purpose of reporting it.</summary>
    /// <remarks>
    /// <para>
    /// The exception's type and the method it was thrown from. Both halves earn their place: the
    /// type alone would silence a second, unrelated bug that happens to throw the same type, and
    /// adding the message would defeat the memory entirely, because a message that names an id or
    /// a count differs on every throw.
    /// </para>
    /// <para>
    /// The frame is read through <see cref="StackTrace"/> rather than parsed out of the string
    /// form, so the signature carries no file path or line number and stays stable when the file is
    /// edited. An exception that was constructed but never thrown has no frame at all and is
    /// identified by its type.
    /// </para>
    /// </remarks>
    public static string SignatureOf(Exception exception)
    {
        // FullName is null for a type that still contains generic parameters, so the short name is
        // the fallback for both branches below.
        var type = exception.GetType().FullName ?? exception.GetType().Name;

        // `false` asks for no source-file information: it is not needed for the signature, and
        // reading it costs a PDB lookup.
        var method = new StackTrace(exception, false).GetFrame(0)?.GetMethod();

        if (method is null)
            return type;

        return $"{type} at {method.DeclaringType?.FullName}.{method.Name}";
    }

    /// <summary>True the first time a failure with this signature is seen.</summary>
    /// <remarks>
    /// Records as it tests, so a caller reports on true and stays quiet afterwards. Callers are
    /// expected to keep logging the recurrence at a lower level rather than dropping it, so a
    /// raised log level still shows that the failure is ongoing.
    /// </remarks>
    public bool IsFirstSighting(Exception exception)
    {
        string signature;

        try
        {
            signature = SignatureOf(exception);
        }
        catch
        {
            // This runs inside catch blocks whose whole job is that nothing escapes into the game's
            // frame dispatch or Dalamud's UI dispatch. Reading a stack trace must never be the thing
            // that breaks that promise, so a failure here degrades to the coarsest signature there
            // is rather than throwing on top of the exception being reported.
            signature = exception.GetType().Name;
        }

        return seen.Add(signature);
    }

    /// <summary>Forgets every failure reported so far, so the next one is reported in full again.</summary>
    /// <remarks>Must be called from whatever context already owns this instance.</remarks>
    public void Reset() => seen.Clear();
}
