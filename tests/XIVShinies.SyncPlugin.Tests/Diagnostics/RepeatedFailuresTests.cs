using System;
using Xunit;
using XIVShinies.SyncPlugin.Diagnostics;

namespace XIVShinies.SyncPlugin.Tests.Diagnostics;

// What counts as a failure already reported. Too coarse a signature swallows an unrelated bug that
// throws the same type; too fine a one lets a per-second failure log every second.
public class RepeatedFailuresTests
{
    // Each helper throws from its own method, so the exceptions carry different top frames. Only a real
    // throw populates a stack trace.
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

    // A tick that throws every second must not log every second.
    [Fact]
    public void The_same_failure_thrown_again_is_not_reported()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());

        Assert.False(failures.IsFirstSighting(ThrownFromHere()));
    }

    // Two unrelated bugs can both throw InvalidOperationException; silencing the second would hide it.
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

    // The message is not part of the signature: one that names an id or a count changes every time.
    [Fact]
    public void A_failure_differing_only_in_its_message_is_not_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(new InvalidOperationException("id 1 failed"));

        Assert.False(failures.IsFirstSighting(new InvalidOperationException("id 2 failed")));
    }

    // Production only sees thrown exceptions. One never thrown takes the no-stack-frame path, which the
    // test above covers and real failures never reach.
    [Fact]
    public void A_thrown_failure_differing_only_in_its_message_is_not_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownWithMessage("id 1 failed"));

        Assert.False(failures.IsFirstSighting(ThrownWithMessage("id 2 failed")));
    }

    // For the assemble-failure path; SyncManager.OnLogout says why that memory is dropped.
    [Fact]
    public void A_reset_lets_a_reported_failure_be_reported_again()
    {
        var failures = new RepeatedFailures();

        failures.IsFirstSighting(ThrownFromHere());
        failures.Reset();

        Assert.True(failures.IsFirstSighting(ThrownFromHere()));
    }

    // A never-thrown exception has no stack trace, and reporting it must not throw.
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

    // A reader of the log can tell where it came from without looking anything up.
    [Fact]
    public void The_signature_names_the_method_that_threw()
    {
        var signature = RepeatedFailures.SignatureOf(ThrownFromHere());

        Assert.Contains(nameof(ThrownFromHere), signature);
    }
}
