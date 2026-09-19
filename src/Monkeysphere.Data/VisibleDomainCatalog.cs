using Monkeysphere.Core;

namespace Monkeysphere.Data;

/// <summary>
/// The domain catalogue as the current caller may observe it. Every surface that enumerates or
/// resolves a domain resolves <see cref="IDomainCatalog"/>, so hiding is applied once here rather
/// than being remembered at each of them; infrastructure that must reach every domain asks for
/// <see cref="IDomainRegistry"/> instead, which is deliberately the more awkward thing to reach.
///
/// Withholding is the default: the backstage visibility this wraps answers false wherever
/// authority has not been established, and false again whenever the deployment gate is off, which
/// is what makes that gate a real kill switch for hidden domains as well as hidden records.
/// </summary>
internal sealed class VisibleDomainCatalog(DomainCatalog inner, IBackstageVisibility visibility) : IDomainCatalog
{
    private bool Unrestricted => visibility.IncludeBackstageRecords;

    public IReadOnlyList<MonkeysphereDomain> Snapshot =>
        Unrestricted ? inner.All : inner.All.Where(domain => !domain.IsHidden).ToArray();

    // Never hidden, so it needs no filtering and always resolves. This is what an invalid or
    // now-hidden selection falls back to.
    public MonkeysphereDomain DefaultDomain => inner.DefaultDomain;

    /// <summary>
    /// A hidden domain answers exactly as an unknown one does. The two are deliberately
    /// indistinguishable: a caller that could tell them apart could probe for hidden domains by
    /// selecting identifiers and reading the difference in the refusal.
    /// </summary>
    public bool TryGet(Guid id, out MonkeysphereDomain? domain)
    {
        if (!inner.TryGet(id, out MonkeysphereDomain? found) || (found!.IsHidden && !Unrestricted))
        {
            domain = null;
            return false;
        }

        domain = found;
        return true;
    }

    public Task<MonkeysphereDomain> SetHiddenAsync(Guid id, bool hidden, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        // Concealing a domain from everyone else, or revealing one that was deliberately concealed,
        // requires standing backstage rather than merely being able to reach the domain. This is
        // the same rule that governs changing a record's backstage state.
        if (!Unrestricted)
        {
            throw new DomainValidationException("Hiding or revealing a domain requires backstage.");
        }

        return inner.SetHiddenAsync(id, hidden, expectedRevision, cancellationToken);
    }
}
