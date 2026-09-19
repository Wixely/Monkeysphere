using Microsoft.Extensions.Configuration;
using Monkeysphere.Web.Security;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Session lifetime is configuration rather than a constant, so the parsing has to fail towards a
/// working session: a bad value must not produce one that expires instantly or never expires.
/// </summary>
public sealed class SessionLifetimeTests
{
    private static SessionLifetime Load(params (string Key, string Value)[] settings) =>
        SessionLifetime.Load(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build());

    [Fact]
    public void TheDefaultIsGenerousEnoughNotToInterruptOrdinaryUse()
    {
        SessionLifetime lifetime = Load();
        Assert.Equal(TimeSpan.FromDays(7), lifetime.IdleTimeout);
        Assert.Equal(TimeSpan.FromDays(30), lifetime.AbsoluteLifetime);
    }

    [Fact]
    public void BothLimitsCanBeConfigured()
    {
        SessionLifetime lifetime = Load(
            ("Monkeysphere:Session:IdleTimeoutMinutes", "45"),
            ("Monkeysphere:Session:AbsoluteLifetimeHours", "8"));

        Assert.Equal(TimeSpan.FromMinutes(45), lifetime.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(8), lifetime.AbsoluteLifetime);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void AnUnusableValueFallsBackRatherThanExpiringImmediately(string value)
    {
        // Zero or negative would sign the administrator out on every request, and an unparseable
        // value must not be read as "no limit" either.
        SessionLifetime lifetime = Load(("Monkeysphere:Session:IdleTimeoutMinutes", value));
        Assert.Equal(SessionLifetime.Default.IdleTimeout, lifetime.IdleTimeout);
    }

    [Fact]
    public void TheCeilingIsNeverBelowTheIdleWindow()
    {
        // An absolute lifetime under the idle timeout would make the idle setting meaningless:
        // the session would be cut short well before it could ever go idle.
        SessionLifetime lifetime = Load(
            ("Monkeysphere:Session:IdleTimeoutMinutes", "10080"),
            ("Monkeysphere:Session:AbsoluteLifetimeHours", "1"));

        Assert.Equal(TimeSpan.FromDays(7), lifetime.IdleTimeout);
        Assert.Equal(TimeSpan.FromDays(7), lifetime.AbsoluteLifetime);
    }
}
