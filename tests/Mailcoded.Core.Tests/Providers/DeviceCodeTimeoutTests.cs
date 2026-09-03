using System.Reflection;
using Mailcoded.Core.Auth;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

/// <summary>The wait for a code is the daemon's problem; the wait after it is the human's.</summary>
public sealed class DeviceCodeTimeoutTests
{
    [Fact]
    public void The_silent_phase_is_bounded_but_the_human_phase_is_not_cut_short()
    {
        Assert.True(
            MicrosoftOAuth.DeviceCodeTimeout > TimeSpan.FromSeconds(10),
            "Microsoft answers in a few seconds; a shorter bound would fail on a slow link.");

        Assert.True(
            MicrosoftOAuth.DeviceCodeTimeout < TimeSpan.FromMinutes(5),
            "This bounds only the silent phase. A bound long enough to cover the sign-in itself "
            + "would leave the terminal looking hung, which is the thing being fixed.");
    }

    /// <summary>A device code is useless without somewhere to type it, so both must be shown.</summary>
    [Fact]
    public void The_prompt_carries_everything_a_human_needs_to_act_on_it()
    {
        var names = typeof(DeviceCodePrompt)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.Contains("VerificationUrl", names);
        Assert.Contains("UserCode", names);
        Assert.Contains("ExpiresUtc", names);
    }
}
