using System;
using Xunit;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Tests.Diagnostics;

// What counts as "the same failure we already reported". The stakes are asymmetric in both
// directions: too coarse a signature swallows a second, unrelated bug that happens to throw the
// same type, and too fine a one lets a per-second failure log every second forever.
public class RepeatedFailuresTests
{
    // Helpers that throw from named methods, so the captured exceptions carry genuinely different
    // top frames. Catching a real throw is the only way to get a populated stack trace.
    private static Exception ThrownFromHere()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception ThrownFromSomewhereElse()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception ThrownWithMessage(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception DifferentTypeFromHere()
    {
        try
        {
            throw new ArgumentException("boom");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void The_first_sighting_of_a_failure_is_reported()
    {
        var failures = new RepeatedFailures();

        Assert.True(failures.IsFirstSighting(ThrownFromHere()));
    }

    // The whole point: a tick that throws every second must not log every second.
    [Fact]
    public void The_same_failure_thrown_again_is_not_reported()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());

        Assert.False(failures.IsFirstSighting(ThrownFromHere()));
    }

    // The inverse hazard. Two unrelated bugs can both throw InvalidOperationException; silencing
    // the second because the first was reported would hide a defect entirely.
    [Fact]
    public void The_same_exception_type_from_a_different_method_is_reported()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());

        Assert.True(failures.IsFirstSighting(ThrownFromSomewhereElse()));
    }

    [Fact]
    public void A_different_exception_type_from_the_same_method_is_reported()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());

        Assert.True(failures.IsFirstSighting(DifferentTypeFromHere()));
    }

    // The message is deliberately not part of the signature: a failure that names an id or a count
    // produces a new message every time, which would defeat the memory entirely.
    [Fact]
    public void A_failure_differing_only_in_its_message_is_not_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(new InvalidOperationException("id 1 failed"));

        Assert.False(failures.IsFirstSighting(new InvalidOperationException("id 2 failed")));
    }

    // The same rule on a thrown exception, which is the only shape production ever sees. An
    // exception that was never thrown takes the no-stack-frame path instead, so the case above
    // pins the rule on a branch real failures never reach.
    [Fact]
    public void A_thrown_failure_differing_only_in_its_message_is_not_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownWithMessage("id 1 failed"));

        Assert.False(failures.IsFirstSighting(ThrownWithMessage("id 2 failed")));
    }

    // Exists for the assemble-failure path — see SyncManager.OnLogout for why that memory is
    // dropped.
    [Fact]
    public void A_reset_lets_a_reported_failure_be_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());
        failures.Reset();

        Assert.True(failures.IsFirstSighting(ThrownFromHere()));
    }

    // An exception that was constructed but never thrown carries no stack trace at all. It still
    // needs a stable signature rather than throwing while trying to report a failure.
    [Fact]
    public void An_exception_that_was_never_thrown_still_has_a_signature()
    {
        var signature = RepeatedFailures.SignatureOf(new InvalidOperationException("boom"));

        Assert.False(string.IsNullOrWhiteSpace(signature));
    }

    [Fact]
    public void The_signature_names_the_exception_type()
    {
        var signature = RepeatedFailures.SignatureOf(ThrownFromHere());

        Assert.Contains("InvalidOperationException", signature);
    }

    // A reader who finds the signature in a log or a debugger should be able to tell where it came
    // from without cross-referencing anything.
    [Fact]
    public void The_signature_names_the_method_that_threw()
    {
        var signature = RepeatedFailures.SignatureOf(ThrownFromHere());

        Assert.Contains(nameof(ThrownFromHere), signature);
    }
}
