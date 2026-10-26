using System.Collections.Concurrent;
using Fms.BuildingBlocks.Integration;

namespace Fms.Integration.Services;

/// <summary>
/// An accounting system that lives in memory. It follows the adapter contract exactly (a repeated key is the same request) and behaves like a real one in the two ways that matter
/// to the sync: it refuses an order for a vendor it has not seen yet (a retryable error), and tests can make it fail or change a record behind the integration's back.
/// </summary>
public sealed class FakeErpAdapter : IErpAdapter
{
    public const string AdapterName = "fake";

    public sealed record Record(string Reference, string PayloadJson);

    private readonly ConcurrentDictionary<string, ErpResult> _byKey = new();
    private readonly ConcurrentDictionary<(string Company, ErpEntity Entity, Guid Id), Record> _records = new();
    private int _counter;

    public string Name => AdapterName;

    /// <summary>Tests set this to make a call fail; return null to let it through.</summary>
    public Func<ErpExportCommand, ErpResult?>? Intercept { get; set; }

    public int Calls { get; private set; }

    public Task<ErpResult> ExportAsync(ErpExportCommand c, CancellationToken ct = default)
    {
        Calls++;
        if (Intercept?.Invoke(c) is { } forced) return Task.FromResult(forced);
        if (_byKey.TryGetValue(c.IdempotencyKey, out var done)) return Task.FromResult(done); // the same request again

        if (c.Entity == ErpEntity.PurchaseOrder
            && System.Text.Json.JsonDocument.Parse(c.PayloadJson).RootElement.TryGetProperty("vendorId", out var vendorId)
            && vendorId.GetGuid() is var vendor
            && !_records.ContainsKey((c.CompanyId, ErpEntity.Vendor, vendor)))
            return Task.FromResult(new ErpResult(false, null, true, "The vendor is not in the accounting system yet."));

        var key = (c.CompanyId, c.Entity, c.EntityId);
        var reference = _records.TryGetValue(key, out var existing) ? existing.Reference : $"FAKE-{c.Entity.ToString().ToUpperInvariant()}-{Interlocked.Increment(ref _counter):D5}";
        _records[key] = new Record(reference, c.PayloadJson);
        var result = new ErpResult(true, reference, false, null);
        _byKey[c.IdempotencyKey] = result;
        return Task.FromResult(result);
    }

    public Task<ErpVerification> VerifyAsync(ErpVerifyCommand c, CancellationToken ct = default)
    {
        if (!_records.TryGetValue((c.CompanyId, c.Entity, c.EntityId), out var record)) return Task.FromResult(new ErpVerification(ErpMatch.Missing, "Not found in the accounting system."));
        return Task.FromResult(record.PayloadJson == c.PayloadJson ? new ErpVerification(ErpMatch.Matched, null) : new ErpVerification(ErpMatch.Mismatch, "The accounting system holds different values."));
    }

    // --- for tests ---

    public Record? Find(string company, ErpEntity entity, Guid id) => _records.TryGetValue((company, entity, id), out var r) ? r : null;

    public void Tamper(string company, ErpEntity entity, Guid id) { if (_records.TryGetValue((company, entity, id), out var r)) _records[(company, entity, id)] = r with { PayloadJson = r.PayloadJson + " " }; }

    public void Remove(string company, ErpEntity entity, Guid id) => _records.TryRemove((company, entity, id), out _);

    public int Count(string company, ErpEntity entity) => _records.Count(r => r.Key.Company == company && r.Key.Entity == entity);
}
