namespace Monkeysphere.Core;

public static class DiscoveryPagination
{
    public const int MaximumPageSize = 100;
    public const int MaximumPage = 10_000;

    public static void Validate(int page, int pageSize)
    {
        if (page is < 1 or > MaximumPage || pageSize is < 1 or > MaximumPageSize)
        {
            throw new DomainValidationException($"Page must be between 1 and {MaximumPage}, and page size between 1 and {MaximumPageSize}.");
        }
    }

    public static PagedResult<T> From<T>(IReadOnlyList<T> orderedItems, int page, int pageSize)
    {
        Validate(page, pageSize);
        return new(orderedItems.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, orderedItems.Count);
    }
}
