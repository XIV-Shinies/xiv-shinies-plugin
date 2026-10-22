using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace XIVShinies.SyncPlugin.Diagnostics;

/// <summary>
/// Remembers which failures have already been reported, so a bug that fires on a repeating path is
/// reported once rather than on every pass.
/// </summary>
/// <remarks>
/// A failure on one of the plugin's repeating loops is usually deterministic, so an unguarded log line
/// becomes tens of thousands of identical entries a day. Not thread-safe: callers serialize access,
/// through one thread or a gate that admits one caller at a time and publishes what the previous one
/// wrote.
/// </remarks>
public sealed class RepeatedFailures
{
    /// <summary>The failure signatures already reported.</summary>
    /// <remarks>Stays small only because <see cref="SignatureOf"/> leaves out the message.</remarks>
    private readonly HashSet<string> seen = [];

    /// <summary>What identifies one failure for the purpose of reporting it.</summary>
    /// <remarks>
    /// The exception's type and the method that threw it. The type alone would silence an unrelated
    /// bug that throws the same type, and the message, which can name an id or a count, would differ
    /// on every throw and grow the memory without limit on a once-a-second path. The frame comes from
    /// <see cref="StackTrace"/>, not the string form, so the signature carries no file path or line
    /// number and survives edits; an exception never thrown has no frame and is identified by its type.
    /// </remarks>
    public static string SignatureOf(Exception exception)
    {
        // FullName is null for a type that still contains generic parameters.
        var type = exception.GetType().FullName ?? exception.GetType().Name;

        // No source-file information: the signature does not need it, and reading it costs a PDB lookup.
        var method = new StackTrace(exception, false).GetFrame(0)?.GetMethod();

        if (method is null)
            return type;

        return $"{type} at {method.DeclaringType?.FullName}.{method.Name}";
    }

    /// <summary>True the first time a failure with this signature is seen.</summary>
    /// <remarks>
    /// Records as it tests. Callers keep logging a recurrence at a lower level rather than dropping it,
    /// so a raised log level still shows the failure is ongoing.
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
            // This runs inside catch blocks that must not throw into the frame or UI dispatch, so a
            // failure here falls back to the coarsest signature.
            signature = exception.GetType().Name;
        }

        return seen.Add(signature);
    }

    /// <summary>Forgets every failure reported so far, so the next one is reported in full again.</summary>
    public void Reset() => seen.Clear();
}
