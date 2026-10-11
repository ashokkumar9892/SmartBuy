using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Domain.Units;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Suppliers.Mock;

namespace SmartBuy.Infrastructure.Services;

public sealed record SearchOffer(
    string Supplier,
    string Title,
    decimal PackQuantity,
    string PackUnit,
    decimal Price,
    decimal? UnitPrice,
    string StockStatus,
    int? DeliveryDaysMin,
    int? DeliveryDaysMax,
    decimal? ShippingFlatFee,
    decimal? FreeShippingThreshold,
    string DataStatus,
    string? BuyUrl,
    string? BuyNote);

public sealed record SearchProduct(string ProductId, string Name, string Category, string Unit, bool IsSafetyCritical, IReadOnlyList<SearchOffer> Offers);

public sealed record SearchResponse(string Query, bool DemoData, IReadOnlyList<SearchProduct> Results, IReadOnlyList<string> Suggestions);

/// <summary>
/// Public product search: type a product, see each supplier's price, follow a link to buy at the supplier.
/// Uses the same catalog, matching and supplier adapters as the business workspace. Nothing is purchased here.
/// </summary>
public sealed class PublicSearchService(AppDbContext db, CatalogResolver resolver, SupplierCatalogService catalog, ISupplierProviderRegistry registry, TimeProvider time)
{
    public static readonly string[] Suggestions = { "copper pipe", "LED bulb", "air filter", "interior paint", "smoke alarm", "mulch", "GFCI outlet", "faucet", "drywall screws", "AA batteries" };

    public async Task<SearchResponse> SearchAsync(string query, CancellationToken ct)
    {
        query = DraftSanitizer.Clean(query, 120);
        if (query.Length < 2) return new SearchResponse(query, false, Array.Empty<SearchProduct>(), Suggestions);

        var candidates = (await resolver.ResolveAsync(query, null, null, null, ct, take: 6)).Where(c => c.Confidence >= 0.35).ToList();
        if (candidates.Count == 0) return new SearchResponse(query, false, Array.Empty<SearchProduct>(), Suggestions);

        var ids = candidates.Select(c => c.ProductId).ToList();
        await catalog.RefreshAsync(ids, force: false, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var matches = await db.ProductMatches.AsNoTracking()
            .Where(m => ids.Contains(m.CanonicalProductId) && (m.Status == MatchStatus.AutoMatched || m.Status == MatchStatus.Confirmed) && m.SupplierProduct!.Supplier!.IsEnabled)
            .Include(m => m.SupplierProduct!).ThenInclude(p => p.Offers.Where(o => o.IsCurrent))
            .Include(m => m.SupplierProduct!).ThenInclude(p => p.Supplier)
            .AsSplitQuery().ToListAsync(ct);

        var results = new List<SearchProduct>();
        var anyDemo = false;
        foreach (var candidate in candidates)
        {
            var offers = new List<SearchOffer>();
            foreach (var match in matches.Where(m => m.CanonicalProductId == candidate.ProductId))
            {
                var listing = match.SupplierProduct!;
                var offer = listing.Offers.FirstOrDefault(o => o.IsCurrent);
                if (offer is null) continue;
                var supplier = listing.Supplier!;
                var status = OfferFreshness.Status(offer.DataSource, offer.FetchedAt, supplier.CacheTtlMinutes, now);
                anyDemo |= status == "DEMO";

                string? url = null, note = null;
                if (registry.Get(supplier.ProviderKey) is { } provider)
                {
                    var link = await provider.BuildPurchaseLinkAsync(listing.ExternalId, 1, ct);
                    // Only HTTPS links on the supplier's own domain are ever handed to a browser.
                    if (link is not null && SafeLink.IsAllowed(link.Url, supplier.WebsiteUrl)) (url, note) = (link.Url, link.Note);
                }
                // Shoppers come here to click through and buy, so offers with no permitted supplier link are left out.
                if (url is null) continue;
                var stock = offer.InStock == false || offer.StockPacks == 0 ? "out_of_stock" : offer.InStock == true || offer.StockPacks > 0 ? "in_stock" : "unknown";
                offers.Add(new SearchOffer(supplier.Name, listing.Title, listing.PackQuantity, listing.PackUnit, offer.Price,
                    PackMath.UnitPrice(offer.Price, listing.PackQuantity, listing.PackUnit, candidate.BaseUnit), stock,
                    offer.DeliveryDaysMin, offer.DeliveryDaysMax, supplier.ShippingFlatFee, supplier.FreeShippingThreshold, status, url, note));
            }
            if (offers.Count == 0) continue;
            results.Add(new SearchProduct(candidate.ProductId.ToString(), candidate.Name, candidate.Category, candidate.BaseUnit, candidate.IsSafetyCritical,
                offers.OrderBy(o => o.StockStatus == "out_of_stock").ThenBy(o => o.UnitPrice ?? decimal.MaxValue).ThenBy(o => o.Supplier, StringComparer.Ordinal).ToList()));
        }
        return new SearchResponse(query, anyDemo, results, Suggestions);
    }
}

/// <summary>
/// Writes the demo catalog and every mock supplier listing to a JSON file. The Netlify-hosted search function reads
/// that file, so the static deployment shows the same demo products and prices as the API.
/// </summary>
public static class DemoDataExport
{
    public static async Task WriteAsync(string path)
    {
        var control = new MockSupplierControl();
        var products = DemoCatalog.Products.Select(p => new
        {
            id = p.Id, key = p.Key, name = p.Name, category = p.Category, industry = p.Industry.ToString(), unit = p.BaseUnit,
            brand = p.Brand, safetyCritical = p.SafetyCritical, keywords = p.Keywords,
        }).ToList();
        var suppliers = MockSupplierProfiles.All.Select(s => new { code = s.Code, name = s.Name, shippingFlatFee = s.ShippingFlatFee, freeShippingThreshold = s.FreeShippingThreshold }).ToList();

        var listings = new List<object>();
        foreach (var profile in MockSupplierProfiles.All)
        {
            var provider = new MockSupplierProvider(profile, control, TimeProvider.System);
            foreach (var product in DemoCatalog.Products)
            {
                var found = await provider.SearchProductsAsync(new ProviderSearchQuery(product.Name, MaxResults: 25), CancellationToken.None);
                // A search also returns near neighbours; keep only this product's own listing.
                var listing = found.FirstOrDefault(l => l.Title.StartsWith(product.Name, StringComparison.Ordinal) || l.Title.StartsWith(product.Name.Replace(" in.", "\"").Replace(" ft.", "ft"), StringComparison.Ordinal));
                if (listing?.Offer is not { } offer) continue;
                var link = await provider.BuildPurchaseLinkAsync(listing.ExternalId, 1, CancellationToken.None);
                listings.Add(new
                {
                    productKey = product.Key, supplier = profile.Code, title = listing.Title, packQuantity = listing.PackQuantity, packUnit = listing.PackUnit,
                    price = offer.Price, unitPrice = PackMath.UnitPrice(offer.Price, listing.PackQuantity, listing.PackUnit, product.BaseUnit),
                    stockStatus = offer.InStock == false || offer.StockPacks == 0 ? "out_of_stock" : offer.InStock == true ? "in_stock" : "unknown",
                    deliveryDaysMin = offer.DeliveryDaysMin, deliveryDaysMax = offer.DeliveryDaysMax,
                    buyUrl = link is not null && SafeLink.IsAllowed(link.Url, profile.Website) ? link.Url : null, buyNote = link?.Note,
                });
            }
        }
        var json = JsonSerializer.Serialize(new { suggestions = PublicSearchService.Suggestions, products, suppliers, listings }, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json);
    }
}
