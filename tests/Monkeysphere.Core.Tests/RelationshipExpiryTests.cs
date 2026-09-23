using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

/// <summary>
/// A relationship that has ended is not one that never happened, so ending it is a change rather
/// than a deletion. Two ways of saying so answer different questions, and the rule for when they
/// disagree is the whole of what makes it predictable.
/// </summary>
public sealed class RelationshipExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingRecordedMeansTheRelationshipStillHolds()
    {
        Assert.False(RelationshipExpiry.None.IsExpiredAt(Now));
        Assert.False(RelationshipExpiry.None.IsSet);
    }

    [Fact]
    public void ADateInThePastHasEndedAndOneInTheFutureHasNot()
    {
        Assert.True(new RelationshipExpiry(ExpiresAtUtc: Now.AddSeconds(-1)).IsExpiredAt(Now));
        Assert.False(new RelationshipExpiry(ExpiresAtUtc: Now.AddSeconds(1)).IsExpiredAt(Now));

        // The moment itself counts as over, so an expiry set to now does not linger for a tick.
        Assert.True(new RelationshipExpiry(ExpiresAtUtc: Now).IsExpiredAt(Now));
    }

    [Fact]
    public void SayingItIsOverWinsOverADateThatHasNotArrived()
    {
        // The point of the flag: marking something over must not wait for a date to catch up, and
        // must not be quietly undone by one set months ahead.
        RelationshipExpiry both = new(Expired: true, ExpiresAtUtc: Now.AddYears(1));

        Assert.True(both.IsExpiredAt(Now));
        Assert.True(both.IsSet);
    }
}
