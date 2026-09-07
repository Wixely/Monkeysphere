namespace Monkeysphere.Data;

// The deployment instance lock guarantees one process per data root. Entries live only while held or awaited.
public sealed class RecordMediaLocks
{
    private readonly Lock _sync = new();
    private readonly Dictionary<(Guid Domain, Guid Record), Entry> _entries = [];

    public async Task<IDisposable> AcquireAsync(Guid domain, Guid record, CancellationToken cancellationToken) =>
        (await TryAcquireAsync(domain, record, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false))!;

    public async Task<IDisposable?> TryAcquireAsync(Guid domain, Guid record, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = (domain, record);
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new());
            entry.References++;
        }
        try
        {
            if (await entry.Gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)) return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
        ReleaseReference(key, entry);
        return null;
    }

    private void ReleaseReference((Guid Domain, Guid Record) key, Entry entry)
    {
        lock (_sync)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(RecordMediaLocks owner, (Guid Domain, Guid Record) key, Entry entry) : IDisposable
    {
        private RecordMediaLocks? _owner = owner;

        public void Dispose()
        {
            RecordMediaLocks? current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            entry.Gate.Release();
            current.ReleaseReference(key, entry);
        }
    }
}
