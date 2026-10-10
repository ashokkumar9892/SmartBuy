using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Matching;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Infrastructure.Services;

public sealed class SupplierOptions
{
    /// <summary>Hard limit for a single provider call; a slow supplier never blocks the others.</summary>
    public int TimeoutSeconds { get; set; } = 8;
    public int MaxResultsPerSearch { get; set; } = 6;
}

public sealed record ProviderIssue(Guid SupplierId, string SupplierName, string Message);

public sealed record RefreshReport(int SuppliersQueried, int ListingsUpdated, IReadOnlyList<ProviderIssue> Issues);

public static class OfferFreshness
{
    /// <summary>LIVE or DEMO while within the supplier's cache window, otherwise STALE.</summary>
    public static string Status(OfferDataSource source, DateTime fetchedAt, int cacheTtlMinutes, DateTime now)
    {
        if (now - fetchedAt > TimeSpan.FromMinutes(Math.Max(1, cacheTtlMinutes))) return "STALE";
        return source == OfferDataSource.Live ? "LIVE" : "DEMO";
    }
}

/// <summary>
/// Pulls offers from every enabled supplier adapter for a set of canonical products, stores them, and links listings
/// to the catalog through the matching engine. One supplier failing or timing out is recorded and reported, never fatal.
/// </summary>
public sealed class SupplierCatalogService(
    AppDbContext db,
    ISupplierProviderRegistry registry,
    IMemoryCache cache,
    IOptions<SupplierOptions> options,
    TimeProvider time,
    ILogger<SupplierCatalogService> logger)
{
    // Serializes catalog writes inside one process so concurrent comparisons cannot race on the unique listing index.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public async Task<RefreshReport> RefreshAsync(IReadOnlyCollection<Guid> canonicalProductIds, bool force, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.IsEnabled).OrderBy(s => s.Name).ToListAsync(ct);
        var products = await db.CanonicalProducts.AsNoTracking().Where(p => canonicalProductIds.Contains(p.Id)).ToListAsync(ct);
        var issues = new List<ProviderIssue>();
        if (products.Count == 0 || suppliers.Count == 0) return new RefreshReport(0, 0, issues);

        var tasks = suppliers.Select(s => QuerySupplierAsync(s, products, force, now, ct)).ToList();
        var results = await Task.WhenAll(tasks);

        var updated = 0;
        await WriteLock.WaitAsync(ct);
        try
        {
            var candidates = await LoadCandidatesAsync(ct);
            foreach (var result in results)
            {
                if (result.Issue is not null) issues.Add(result.Issue);
                if (!result.Queried) continue;
                updated += await PersistAsync(result, candidates, now, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            WriteLock.Release();
        }
        return new RefreshReport(results.Count(r => r.Queried), updated, issues);
    }

    private sealed record SupplierResult(Supplier Supplier, bool Queried, List<ProviderProduct> Products, ProviderIssue? Issue, int DurationMs, List<Guid> SearchedProductIds);

    private async Task<SupplierResult> QuerySupplierAsync(Supplier supplier, List<CanonicalProduct> products, bool force, DateTime now, CancellationToken ct)
    {
        var provider = registry.Get(supplier.ProviderKey);
        if (provider is null)
            return new SupplierResult(supplier, false, new(), new ProviderIssue(supplier.Id, supplier.Name, "No adapter is registered for this supplier."), 0, new());

        var due = products.Where(p => force || !cache.TryGetValue(CacheKey(supplier.Id, p.Id), out _)).ToList();
        if (due.Count == 0) return new SupplierResult(supplier, false, new(), null, 0, new());

        var stopwatch = Stopwatch.StartNew();
        var found = new Dictionary<string, ProviderProduct>(StringComparer.OrdinalIgnoreCase);
        var searched = new List<Guid>();
        try
        {
            // One deadline for the whole batch: a supplier that is down fails fast instead of once per product.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));
            foreach (var product in due)
            {
                var query = new ProviderSearchQuery(product.Name, product.Gtin, product.Mpn, product.Brand, MaxResults: options.Value.MaxResultsPerSearch);
                var listings = await provider.SearchProductsAsync(query, timeout.Token).WaitAsync(timeout.Token);
                foreach (var listing in listings) found[listing.ExternalId] = listing;
                searched.Add(product.Id);
            }
            return new SupplierResult(supplier, true, found.Values.ToList(), null, (int)stopwatch.ElapsedMilliseconds, searched);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Supplier {Supplier} timed out after {Seconds}s", supplier.Code, options.Value.TimeoutSeconds);
            return new SupplierResult(supplier, true, found.Values.ToList(),
                new ProviderIssue(supplier.Id, supplier.Name, $"Timed out after {options.Value.TimeoutSeconds} seconds. Any prices shown for this supplier are from an earlier refresh."),
                (int)stopwatch.ElapsedMilliseconds, searched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Supplier {Supplier} failed", supplier.Code);
            return new SupplierResult(supplier, true, found.Values.ToList(),
                new ProviderIssue(supplier.Id, supplier.Name, "The supplier could not be reached. Any prices shown for this supplier are from an earlier refresh."),
                (int)stopwatch.ElapsedMilliseconds, searched);
        }
    }

    private static string CacheKey(Guid supplierId, Guid productId) => $"supplier-refresh:{supplierId}:{productId}";

    private async Task<List<CanonicalCandidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        var all = await db.CanonicalProducts.AsNoTracking().Include(p => p.Specifications).ToListAsync(ct);
        return all.Select(ToCandidate).ToList();
    }

    public static CanonicalCandidate ToCandidate(CanonicalProduct p) => new(
        p.Id, p.Name, p.Brand, p.Model, p.Mpn, p.Gtin, p.Material, p.BaseUnit,
        p.Specifications.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase));

    private async Task<int> PersistAsync(SupplierResult result, List<CanonicalCandidate> candidates, DateTime now, CancellationToken ct)
    {
        var supplier = result.Supplier;
        db.ProviderSyncLogs.Add(new ProviderSyncLog
        {
            SupplierId = supplier.Id,
            Operation = "search",
            Success = result.Issue is null,
            DurationMs = result.DurationMs,
            ItemCount = result.Products.Count,
            Error = result.Issue?.Message,
        });

        // Only mark products as refreshed when the supplier actually answered for them.
        foreach (var productId in result.SearchedProductIds)
            cache.Set(CacheKey(supplier.Id, productId), true, TimeSpan.FromMinutes(Math.Max(1, supplier.CacheTtlMinutes)));

        if (result.Products.Count == 0) return 0;
        var externalIds = result.Products.Select(p => p.ExternalId).ToList();
        var existing = await db.SupplierProducts
            .Include(p => p.Offers.Where(o => o.IsCurrent))
            .Include(p => p.Matches)
            .Where(p => p.SupplierId == supplier.Id && externalIds.Contains(p.ExternalId))
            .ToDictionaryAsync(p => p.ExternalId, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var listing in result.Products)
        {
            if (!existing.TryGetValue(listing.ExternalId, out var row))
            {
                row = new SupplierProduct { SupplierId = supplier.Id, ExternalId = listing.ExternalId };
                db.SupplierProducts.Add(row);
                existing[listing.ExternalId] = row;
            }
            row.Title = Truncate(listing.Title, 500);
            row.Description = listing.Description is null ? null : Truncate(listing.Description, 2000);
            row.Brand = listing.Brand;
            row.Model = listing.Model;
            row.Mpn = listing.Mpn;
            row.Gtin = ProductMatcher.NormalizeGtin(listing.Gtin);
            row.PackQuantity = listing.PackQuantity <= 0 ? 1 : listing.PackQuantity;
            row.PackUnit = listing.PackUnit;
            row.SpecsJson = JsonSerializer.Serialize(listing.Specs);
            row.ProductUrl = listing.ProductUrl;

            if (listing.Offer is { } offer)
            {
                var current = row.Offers.FirstOrDefault(o => o.IsCurrent);
                if (current is null)
                {
                    current = new ProductOffer { SupplierProduct = row, SupplierId = supplier.Id, IsCurrent = true };
                    row.Offers.Add(current);
                    db.PriceHistory.Add(new PriceHistory { SupplierProduct = row, Price = offer.Price, DataSource = offer.DataSource, RecordedAt = now });
                }
                else if (current.Price != offer.Price)
                {
                    db.PriceHistory.Add(new PriceHistory { SupplierProduct = row, Price = offer.Price, DataSource = offer.DataSource, RecordedAt = now });
                }
                current.Price = offer.Price;
                current.Currency = offer.Currency;
                current.MinOrderPacks = Math.Max(1, offer.MinOrderPacks);
                current.StockPacks = offer.StockPacks;
                current.InStock = offer.InStock;
                current.DeliveryDaysMin = offer.DeliveryDaysMin;
                current.DeliveryDaysMax = offer.DeliveryDaysMax;
                current.DataSource = offer.DataSource;
                current.FetchedAt = offer.FetchedAt;
            }

            ApplyMatch(row, listing, candidates);
        }
        return result.Products.Count;
    }

    private void ApplyMatch(SupplierProduct row, ProviderProduct listing, List<CanonicalCandidate> candidates)
    {
        // A person's decision always wins over the engine.
        if (row.Matches.Any(m => m.Status is MatchStatus.Confirmed)) return;
        var rejected = row.Matches.Where(m => m.Status == MatchStatus.Rejected).Select(m => m.CanonicalProductId).ToHashSet();

        var best = ProductMatcher.FindBestMatch(
            new SupplierListing(listing.Title, listing.Brand, listing.Model, listing.Mpn, listing.Gtin, listing.Specs, listing.PackQuantity, listing.PackUnit),
            candidates.Where(c => !rejected.Contains(c.Id)));

        foreach (var stale in row.Matches.Where(m => m.Status is MatchStatus.AutoMatched or MatchStatus.NeedsReview && m.CanonicalProductId != best?.CanonicalProductId).ToList())
        {
            row.Matches.Remove(stale);
            db.ProductMatches.Remove(stale);
        }
        if (best is null) return;

        var match = row.Matches.FirstOrDefault(m => m.CanonicalProductId == best.CanonicalProductId);
        if (match is null)
        {
            match = new ProductMatch { SupplierProduct = row, CanonicalProductId = best.CanonicalProductId };
            row.Matches.Add(match);
        }
        match.Method = best.Method;
        match.Confidence = best.Confidence;
        match.Status = best.Status;
        match.Reasons = string.Join(" | ", best.Reasons);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
