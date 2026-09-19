using Microsoft.Extensions.Configuration;

namespace Monkeysphere.Web.Security;

/// <summary>
/// How long a signed-in browser session lasts. Two separate limits, because they answer different
/// questions:
///
/// <list type="bullet">
/// <item><see cref="IdleTimeout"/> is how long a session survives without being used. It slides, so
/// ordinary use keeps it alive indefinitely; it is what signs out a browser left alone.</item>
/// <item><see cref="AbsoluteLifetime"/> is the ceiling regardless of activity, measured from the
/// sign-in itself. It is what stops a continuously-used session from lasting forever.</item>
/// </list>
///
/// The defaults are deliberately generous. Monkeysphere is a single-administrator application that
/// is normally reached from a trusted machine, and an idle timeout short enough to interrupt
/// ordinary use trains people to keep re-authenticating rather than making anything safer. A
/// deployment exposed more widely should shorten both, which is why they are configuration rather
/// than constants.
/// </summary>
public sealed record SessionLifetime(TimeSpan IdleTimeout, TimeSpan AbsoluteLifetime)
{
    /// <summary>A week of inactivity, and a month in total.</summary>
    public static readonly SessionLifetime Default = new(TimeSpan.FromDays(7), TimeSpan.FromDays(30));

    /// <summary>
    /// Reads <c>Monkeysphere:Session:IdleTimeoutMinutes</c> and
    /// <c>Monkeysphere:Session:AbsoluteLifetimeHours</c>. An absent, unparseable or non-positive
    /// value falls back to the default rather than producing a session that expires immediately or
    /// never expires at all; the absolute lifetime is never allowed below the idle timeout, since a
    /// ceiling under the idle window would make the idle setting meaningless.
    /// </summary>
    public static SessionLifetime Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        TimeSpan idle = Positive(configuration["Monkeysphere:Session:IdleTimeoutMinutes"], TimeSpan.FromMinutes,
            Default.IdleTimeout);
        TimeSpan absolute = Positive(configuration["Monkeysphere:Session:AbsoluteLifetimeHours"], TimeSpan.FromHours,
            Default.AbsoluteLifetime);
        return new(idle, absolute < idle ? idle : absolute);
    }

    private static TimeSpan Positive(string? value, Func<double, TimeSpan> convert, TimeSpan fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0
            ? convert(parsed)
            : fallback;
}
