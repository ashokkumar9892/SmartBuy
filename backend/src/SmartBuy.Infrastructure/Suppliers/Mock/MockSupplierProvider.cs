using System.Collections.Concurrent;
using System.Globalization;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Matching;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Domain.Units;

namespace SmartBuy.Infrastructure.Suppliers.Mock;

public enum MockDataQuality
{
    /// <summary>Identifiers and full specifications, like a well-structured product API.</summary>
    Full,
    /// <summary>Specifications but no GTIN, like many marketplace feeds.</summary>
    Partial,
    /// <summary>A title and a price only. These listings can only ever reach manual review.</summary>
    TitleOnly
}

public sealed record MockSupplierProfile(
    string Code,
    string Name,
    SupplierKind Kind,
    decimal PriceFactor,
    int CoveragePercent,
    Industry[] StrongIndustries,
    decimal? ShippingFlatFee,
    decimal? FreeShippingThreshold,
    decimal? MinimumOrderAmount,
    int? DeliveryDaysMin,
    int? DeliveryDaysMax,
    bool ReportsStock,
    MockDataQuality Quality,
    string? Website,
    string? SearchUrlTemplate,
    string IntegrationNotes,
    string[]? OnlyProductKeys = null)
{
    public string ProviderKey => "mock:" + Code;
}

public static class MockSupplierProfiles
{
    private static readonly Industry[] Trades = { Industry.Plumbing, Industry.Electrical, Industry.Hvac, Industry.Construction, Industry.HomeRenovation, Industry.Landscaping };

    /// <summary>
    /// Shipping fees, thresholds and delivery windows below are demonstration values, not the retailers' real policies.
    /// </summary>
    public static readonly IReadOnlyList<MockSupplierProfile> All = new[]
    {
        new MockSupplierProfile("homedepot", "Home Depot", SupplierKind.Retailer, 1.00m, 88, Trades, 8.99m, 45m, null, 2, 4, true, MockDataQuality.Full,
            "https://www.homedepot.com", "https://www.homedepot.com/s/{0}",
            "Mock adapter. No public product or pricing API is assumed; a live adapter requires a partner or affiliate data agreement."),
        new MockSupplierProfile("lowes", "Lowe's", SupplierKind.Retailer, 1.015m, 82, Trades, 7.99m, 45m, null, 2, 5, true, MockDataQuality.Full,
            "https://www.lowes.com", "https://www.lowes.com/search?searchTerm={0}",
            "Mock adapter. No public product or pricing API is assumed; a live adapter requires a partner or affiliate data agreement."),
        new MockSupplierProfile("amazon", "Amazon", SupplierKind.Marketplace, 0.97m, 68, new[] { Industry.General, Industry.PropertyManagement, Industry.Electrical, Industry.Hvac }, 6.99m, 35m, null, 1, 3, true, MockDataQuality.Partial,
            "https://www.amazon.com", "https://www.amazon.com/s?k={0}",
            "Mock adapter. A live adapter requires an approved Amazon Associates account and product-advertising API access under its licence terms."),
        new MockSupplierProfile("walmart", "Walmart", SupplierKind.Retailer, 0.96m, 55, new[] { Industry.General, Industry.PropertyManagement, Industry.HomeRenovation }, 6.99m, 35m, null, 2, 4, true, MockDataQuality.Partial,
            "https://www.walmart.com", "https://www.walmart.com/search?q={0}",
            "Mock adapter. A live adapter requires approved Walmart affiliate/developer API access."),
        new MockSupplierProfile("ebay", "eBay", SupplierKind.Marketplace, 0.90m, 42, new[] { Industry.Electrical, Industry.Plumbing, Industry.General }, null, null, null, null, null, false, MockDataQuality.TitleOnly,
            "https://www.ebay.com", "https://www.ebay.com/sch/i.html?_nkw={0}",
            "Mock adapter. Shipping and inventory vary per seller and are reported as unknown. A live adapter requires eBay developer keys and Buy API access under the eBay API licence."),
        new MockSupplierProfile("bestbuy", "Best Buy", SupplierKind.Retailer, 1.03m, 100, Array.Empty<Industry>(), 5.99m, 35m, null, 1, 3, true, MockDataQuality.Full,
            "https://www.bestbuy.com", "https://www.bestbuy.com/site/searchpage.jsp?st={0}",
            "Mock adapter limited to the electronics-adjacent items Best Buy would plausibly carry. A live adapter requires an approved Best Buy developer API key.",
            new[] { "thermostat-ct500", "led-a19-2700", "aa-batteries", "smoke-alarm-sa10", "smoke-alarm-sa12", "led-recessed-6" }),
        new MockSupplierProfile("local-riverside", "Riverside Supply Co.", SupplierKind.LocalSupplier, 0.93m, 74, new[] { Industry.Plumbing, Industry.Electrical, Industry.Hvac, Industry.Landscaping }, 25m, 500m, 50m, 1, 1, true, MockDataQuality.Full,
            null, null,
            "Fictional independent trade supplier. Real local suppliers are onboarded through a price-file upload or their own storefront API."),
        new MockSupplierProfile("shopify-tradedepot", "Trade Depot Direct", SupplierKind.ShopifyMerchant, 0.95m, 40, new[] { Industry.Landscaping, Industry.HomeRenovation }, null, null, null, 3, 6, true, MockDataQuality.Partial,
            null, null,
            "Fictional Shopify merchant. A live adapter uses the merchant's own Storefront API token; shipping is calculated at checkout and reported as unknown."),
        new MockSupplierProfile("woo-prosource", "ProSource Wholesale", SupplierKind.WooCommerceMerchant, 0.91m, 40, new[] { Industry.General, Industry.Construction }, 14.99m, 250m, null, 4, 7, false, MockDataQuality.Partial,
            null, null,
            "Fictional WooCommerce merchant. A live adapter uses the merchant's own REST API keys."),
    };
}

/// <summary>Runtime switches for demonstrating supplier failure handling. In-memory only.</summary>
public sealed class MockSupplierControl
{
    private readonly ConcurrentDictionary<string, string> _faults = new(StringComparer.OrdinalIgnoreCase);
    public int LatencyMs { get; set; }

    /// <param name="fault">"error", "timeout", or null/"none" to clear.</param>
    public void SetFault(string supplierCode, string? fault)
    {
        if (string.IsNullOrEmpty(fault) || fault == "none") _faults.TryRemove(supplierCode, out _);
        else _faults[supplierCode] = fault;
    }

    public string? GetFault(string supplierCode) => _faults.TryGetValue(supplierCode, out var f) ? f : null;
    public IReadOnlyDictionary<string, string> Faults => _faults;
}

/// <summary>
/// Simulated supplier backed by the fictional demo catalog. Every offer it returns is tagged
/// <see cref="OfferDataSource.Demo"/>; it never produces LIVE data and never places orders.
/// </summary>
public sealed class MockSupplierProvider : ISupplierProvider
{
    private sealed record Listing(DemoProduct Product, PackVariant Pack, int PackIndex, string ExternalId, string Title, uint Hash);

    private readonly MockSupplierProfile _profile;
    private readonly MockSupplierControl _control;
    private readonly TimeProvider _time;
    private readonly List<Listing> _listings;
    private readonly Dictionary<string, Listing> _byId;

    public MockSupplierProvider(MockSupplierProfile profile, MockSupplierControl control, TimeProvider time)
    {
        _profile = profile;
        _control = control;
        _time = time;
        _listings = BuildListings(profile);
        _byId = _listings.ToDictionary(l => l.ExternalId, StringComparer.OrdinalIgnoreCase);
    }

    public string Key => _profile.ProviderKey;
    public MockSupplierProfile Profile => _profile;

    private static List<Listing> BuildListings(MockSupplierProfile profile)
    {
        var result = new List<Listing>();
        foreach (var product in DemoCatalog.Products)
        {
            var hash = DemoCatalog.StableHash(profile.Code + "|" + product.Key);
            if (profile.OnlyProductKeys is not null)
            {
                if (!profile.OnlyProductKeys.Contains(product.Key)) continue;
            }
            else
            {
                var coverage = profile.StrongIndustries.Contains(product.Industry) ? profile.CoveragePercent : profile.CoveragePercent * 45 / 100;
                if (hash % 100 >= coverage) continue;
            }
            var packIndex = (int)((hash >> 8) % (uint)product.Packs.Count);
            var pack = product.Packs[packIndex];
            result.Add(new Listing(product, pack, packIndex, $"{profile.Code.ToUpperInvariant()}-{hash:X8}", BuildTitle(profile, product, pack), hash));
        }
        return result;
    }

    private static string PackLabel(DemoProduct product, PackVariant pack)
    {
        var qty = pack.Quantity.ToString("0.##", CultureInfo.InvariantCulture);
        var unit = UnitConverter.Resolve(pack.Unit)!;
        if (unit.Dimension == UnitDimension.Count) return pack.Quantity == 1 ? "" : $"{qty}-Pack";
        return $"{qty} {unit.Label}";
    }

    private static string BuildTitle(MockSupplierProfile profile, DemoProduct product, PackVariant pack)
    {
        var label = PackLabel(product, pack);
        return profile.Quality switch
        {
            MockDataQuality.Full => label.Length == 0 ? product.Name : $"{product.Name} ({label})",
            MockDataQuality.Partial => label.Length == 0 ? product.Name : $"{product.Name}, {label}",
            // Marketplace-style free text: abbreviations, no structure.
            _ => (product.Name.Replace(" in.", "\"").Replace(" ft.", "ft") + (label.Length == 0 ? "" : " " + label.Replace(" ", "")) + " - contractor lot").Trim()
        };
    }

    private decimal PriceToday(Listing l)
    {
        var day = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var noise = ((int)((l.Hash >> 16) % 2001) - 1000) / 1000m * 0.12m; // stable ±12 % per supplier/product
        var drift = ((int)(DemoCatalog.StableHash(_profile.Code + "|" + l.Product.Key + "|" + day) % 601) - 300) / 10000m; // daily ±3 %
        var baseUnits = UnitConverter.Convert(l.Pack.Quantity, l.Pack.Unit, l.Product.BaseUnit) ?? l.Pack.Quantity;
        var price = l.Product.BasePrice * baseUnits * l.Pack.Discount * _profile.PriceFactor * (1 + noise) * (1 + drift);
        return Math.Max(0.05m, decimal.Round(price, 2, MidpointRounding.AwayFromZero));
    }

    private ProviderOffer ToOffer(Listing l)
    {
        int? stock = null;
        bool? inStock = null;
        if (_profile.ReportsStock)
        {
            var roll = (l.Hash >> 4) % 20;
            if (roll == 0) { stock = 0; inStock = false; }
            else if (roll == 1) { stock = null; inStock = true; } // "in stock" without a count
            else { stock = 5 + (int)(l.Hash % 400); inStock = true; }
        }
        int? min = _profile.DeliveryDaysMin, max = _profile.DeliveryDaysMax;
        if (min is not null && max is not null)
        {
            var d = min.Value + (int)(l.Hash % (uint)(max.Value - min.Value + 1));
            min = d;
            max = Math.Max(d, max.Value);
        }
        var price = PriceToday(l);
        var minOrder = _profile.Kind == SupplierKind.WooCommerceMerchant && price < 5m ? 2 : 1;
        return new ProviderOffer(l.ExternalId, price, "USD", minOrder, stock, inStock, min, max, OfferDataSource.Demo, _time.GetUtcNow().UtcDateTime);
    }

    private ProviderProduct ToProduct(Listing l)
    {
        var specs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_profile.Quality != MockDataQuality.TitleOnly)
        {
            foreach (var (k, v) in l.Product.Specs) specs[k] = v;
            if (l.Product.Material is not null) specs["material"] = l.Product.Material;
        }
        var structured = _profile.Quality != MockDataQuality.TitleOnly;
        return new ProviderProduct(
            l.ExternalId,
            l.Title,
            structured ? $"{l.Product.Name}. {l.Product.Category}." : null,
            structured ? l.Product.Brand : null,
            structured ? l.Product.Model : null,
            structured ? l.Product.Model : null,
            // A GTIN identifies one trade item, so only the base pack carries the product's GTIN.
            _profile.Quality == MockDataQuality.Full && l.PackIndex == 0 ? l.Product.Gtin : null,
            l.Pack.Quantity,
            l.Pack.Unit,
            specs,
            null,
            ToOffer(l));
    }

    private async Task SimulateAsync(CancellationToken ct)
    {
        var fault = _control.GetFault(_profile.Code);
        if (fault == "timeout") await Task.Delay(TimeSpan.FromMinutes(5), ct);
        if (_control.LatencyMs > 0) await Task.Delay(_control.LatencyMs, ct);
        if (fault == "error") throw new HttpRequestException($"{_profile.Name} (mock) returned a simulated 503 Service Unavailable.");
    }

    public async Task<IReadOnlyList<ProviderProduct>> SearchProductsAsync(ProviderSearchQuery query, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        var gtin = ProductMatcher.NormalizeGtin(query.Gtin);
        if (gtin is not null && _profile.Quality == MockDataQuality.Full)
        {
            var exact = _listings.Where(l => l.PackIndex == 0 && ProductMatcher.NormalizeGtin(l.Product.Gtin) == gtin).Select(ToProduct).ToList();
            if (exact.Count > 0) return exact;
        }
        return _listings
            .Select(l => (Listing: l, Score: Math.Max(
                ProductMatcher.TitleSimilarity(query.Text, l.Product.Name),
                ProductMatcher.TitleSimilarity(query.Text, l.Product.Name + " " + string.Join(' ', l.Product.Keywords)))))
            .Where(x => x.Score >= 0.45)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Listing.ExternalId, StringComparer.Ordinal)
            .Take(Math.Clamp(query.MaxResults, 1, 25))
            .Select(x => ToProduct(x.Listing))
            .ToList();
    }

    public async Task<ProviderProduct?> GetProductDetailsAsync(string externalId, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        return _byId.TryGetValue(externalId, out var l) ? ToProduct(l) : null;
    }

    public async Task<IReadOnlyList<ProviderOffer>> GetOffersAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        return externalIds.Where(_byId.ContainsKey).Select(id => ToOffer(_byId[id])).ToList();
    }

    public async Task<ProviderAvailability?> GetAvailabilityAsync(string externalId, string? postalCode, CancellationToken cancellationToken)
    {
        await SimulateAsync(cancellationToken);
        if (!_byId.TryGetValue(externalId, out var l)) return null;
        var o = ToOffer(l);
        return new ProviderAvailability(externalId, o.InStock, o.StockPacks, o.DeliveryDaysMin, o.DeliveryDaysMax, o.FetchedAt);
    }

    public Task<PurchaseLink?> BuildPurchaseLinkAsync(string externalId, int packs, CancellationToken cancellationToken)
    {
        if (_profile.SearchUrlTemplate is null || !_byId.TryGetValue(externalId, out var l))
            return Task.FromResult<PurchaseLink?>(null);
        // The demo catalog is fictional, so there is no exact product page. A plain, untagged link to the retailer's own
        // public search is the most that can honestly be offered.
        var url = string.Format(CultureInfo.InvariantCulture, _profile.SearchUrlTemplate, Uri.EscapeDataString(l.Product.Name));
        return Task.FromResult<PurchaseLink?>(new PurchaseLink(url, PurchaseLinkKind.SupplierSearch, false,
            $"Demo data: opens {_profile.Name}'s own search for a similar item. It is not the exact product or price shown here."));
    }

    public Task<ProviderStatus> GetProviderStatusAsync(CancellationToken cancellationToken)
    {
        var fault = _control.GetFault(_profile.Code);
        return Task.FromResult(new ProviderStatus(
            Key, SupplierMode.Mock, true, fault is null,
            fault is null ? "Mock adapter serving demo data." : $"Simulated fault active: {fault}.",
            new ProviderCapabilities(true, _profile.ReportsStock, _profile.SearchUrlTemplate is not null, false, false),
            _time.GetUtcNow().UtcDateTime));
    }
}

public sealed class SupplierProviderRegistry(IEnumerable<ISupplierProvider> providers) : ISupplierProviderRegistry
{
    private readonly Dictionary<string, ISupplierProvider> _providers = providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public ISupplierProvider? Get(string providerKey) => _providers.TryGetValue(providerKey, out var p) ? p : null;
    public IReadOnlyCollection<ISupplierProvider> All => _providers.Values;
}
