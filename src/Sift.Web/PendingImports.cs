using Microsoft.Extensions.Caching.Memory;
using Sift.Core.Ingestion;
using Sift.Core.Pipeline;

namespace Sift.Web;

public sealed record PendingImport(string Id, RawEmail Raw, PipelineResult Result);

/// <summary>
/// Imports awaiting review. Held in memory only, never written to disk, and dropped after
/// 30 minutes of inactivity.
/// </summary>
public sealed class PendingImports(IMemoryCache cache)
{
    private static readonly MemoryCacheEntryOptions Expiry = new() { SlidingExpiration = TimeSpan.FromMinutes(30) };

    public PendingImport Add(RawEmail raw, PipelineResult result)
    {
        var pending = new PendingImport(Guid.NewGuid().ToString("N"), raw, result);
        cache.Set(Key(pending.Id), pending, Expiry);
        return pending;
    }

    public PendingImport? Get(string? id) =>
        id is not null && cache.TryGetValue(Key(id), out PendingImport? pending) ? pending : null;

    public void Update(PendingImport pending) => cache.Set(Key(pending.Id), pending, Expiry);

    public void Remove(string id) => cache.Remove(Key(id));

    private static string Key(string id) => "pending:" + id;
}
