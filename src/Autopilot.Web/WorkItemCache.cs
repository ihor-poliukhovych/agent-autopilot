using Autopilot.Core;

namespace Autopilot.Web;

/// <summary>
/// Keeps the open work items read from a tracker for a short time. One page load asks for the same list several
/// times (tasks, needs a person, pool); without this each ask is a call to the tracker. A fresh entry is returned
/// at once, a stale one is returned at once while one refresh runs in the background, and callers that arrive
/// while a fetch is running share it.
/// </summary>
public sealed class WorkItemCache(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool Enabled => ttl > TimeSpan.Zero;

    public Task<IReadOnlyList<WorkItem>> GetAsync(string key, Func<CancellationToken, Task<IReadOnlyList<WorkItem>>> fetch, CancellationToken ct)
    {
        if (!Enabled)
        {
            return fetch(ct);
        }

        Task<IReadOnlyList<WorkItem>> served;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Task = Start(key, fetch), At = _clock() };
                _entries[key] = entry;
                served = entry.Task;
            }
            else if (entry.Task.IsCompletedSuccessfully && _clock() - entry.At > ttl && !entry.Refreshing)
            {
                entry.Refreshing = true;
                var previous = entry;
                served = entry.Task; // the list we have now, even if the refresh finishes at once
                _ = Refresh(key, previous, fetch);
            }
            else
            {
                served = entry.Task;
            }
        }

        return served.WaitAsync(ct);
    }

    public void Invalidate(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    private Task<IReadOnlyList<WorkItem>> Start(string key, Func<CancellationToken, Task<IReadOnlyList<WorkItem>>> fetch)
    {
        var task = Task.Run(() => fetch(CancellationToken.None));
        _ = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                if (t.IsCompletedSuccessfully && _entries.TryGetValue(key, out var e) && e.Task == t)
                {
                    e.At = _clock();
                }
                else if (!t.IsCompletedSuccessfully && _entries.TryGetValue(key, out var failed) && failed.Task == t)
                {
                    _entries.Remove(key); // do not keep a failure: the next call tries again
                }
            }
        }, TaskScheduler.Default);
        return task;
    }

    private async Task Refresh(string key, Entry previous, Func<CancellationToken, Task<IReadOnlyList<WorkItem>>> fetch)
    {
        try
        {
            var items = await fetch(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && current == previous)
                {
                    previous.Task = Task.FromResult(items);
                    previous.At = _clock();
                }
            }
        }
        catch (Exception)
        {
            // Keep the older list; it is shown as before and the next call tries again.
        }
        finally
        {
            lock (_gate)
            {
                previous.Refreshing = false;
            }
        }
    }

    private sealed class Entry
    {
        public Task<IReadOnlyList<WorkItem>> Task { get; set; } = null!;

        public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

        public bool Refreshing { get; set; }
    }
}

/// <summary>An <see cref="IWorkItemSource"/> whose list of open items comes through a <see cref="WorkItemCache"/>.</summary>
public sealed class CachedWorkItemSource(IWorkItemSource inner, WorkItemCache cache, string key) : IWorkItemSource
{
    public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) => cache.GetAsync(key, inner.ListOpenAsync, ct);

    public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);

    public async Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct)
    {
        await inner.RecordTriageAsync(id, result, ct).ConfigureAwait(false);
        cache.Invalidate(key);
    }
}
